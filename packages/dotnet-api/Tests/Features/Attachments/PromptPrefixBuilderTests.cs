using Source.Features.Attachments.Models;
using Source.Features.Attachments.Services;

namespace Api.Tests.Features.Attachments;

/// <summary>
/// Pure-unit tests for <see cref="PromptPrefixBuilder"/>. No DbContext, no DI —
/// the helper is intentionally static + side-effect free so we can exercise
/// every branch directly.
///
/// <para>The chat-file-attachments spec pins three behaviours we have to keep
/// honest as it evolves:</para>
/// <list type="number">
///   <item>Zero attachments is a byte-for-byte passthrough — the daemon-side
///         prompt rendering and any future analytics that dedup on prompt text
///         must not see a phantom prefix when nothing was attached.</item>
///   <item>One attachment renders identically to many (no special-case empty
///         list / single-line shorthand) so the agent's tokenisation is
///         deterministic across the two cases.</item>
///   <item>The per-attachment <c>LocalPathFor</c> derivation is the single
///         source of truth for the on-runtime path used by BOTH the prompt
///         prefix block AND the daemon staging handshake.</item>
/// </list>
/// </summary>
public class PromptPrefixBuilderTests
{
    [Fact]
    public void BuildPromptWithAttachments_NoAttachments_ReturnsPromptUnchanged()
    {
        var prompt = "hello world";

        var result = PromptPrefixBuilder.BuildPromptWithAttachments(prompt, Array.Empty<Attachment>());

        // Identity passthrough — no prefix, no trailing newline shifts.
        result.Should().Be(prompt);
    }

    [Fact]
    public void BuildPromptWithAttachments_NullAttachments_ReturnsPromptUnchanged()
    {
        var prompt = "hello world";

        // The contract permits null for the attachments list (the dispatcher
        // already normalises but the public surface accepts both shapes).
        var result = PromptPrefixBuilder.BuildPromptWithAttachments(prompt, null!);

        result.Should().Be(prompt);
    }

    [Fact]
    public void BuildPromptWithAttachments_SingleAttachment_PrependsBlockAndBlankLine()
    {
        var conversationId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var attachment = new Attachment
        {
            Id = attachmentId,
            ConversationId = conversationId,
            FileName = "report.pdf",
            R2Key = $"attachments/{conversationId}/{attachmentId}-report.pdf",
        };

        var result = PromptPrefixBuilder.BuildPromptWithAttachments("summarise this", new[] { attachment });

        result.Should().Contain("[Attached files for this turn:");
        result.Should().Contain($"  - report.pdf  ->  /data/project/repo/.glenn/attachments/{conversationId}/{attachmentId}-report.pdf");
        result.Should().Contain("You can read them with the Read tool.]");
        result.Should().EndWith("summarise this");
    }

    [Fact]
    public void BuildPromptWithAttachments_MultipleAttachments_RendersOneBulletPerFileInOrder()
    {
        var conversationId = Guid.NewGuid();
        var firstId = Guid.NewGuid();
        var secondId = Guid.NewGuid();
        var attachments = new[]
        {
            new Attachment
            {
                Id = firstId,
                ConversationId = conversationId,
                FileName = "report.pdf",
                R2Key = $"attachments/{conversationId}/{firstId}-report.pdf",
            },
            new Attachment
            {
                Id = secondId,
                ConversationId = conversationId,
                FileName = "logo.png",
                R2Key = $"attachments/{conversationId}/{secondId}-logo.png",
            },
        };

        var result = PromptPrefixBuilder.BuildPromptWithAttachments("look at these", attachments);

        // Bullet for each file, preserving list order — the agent reads
        // top-to-bottom and call sites rely on the input ordering being
        // honoured.
        var firstBulletIndex = result.IndexOf($"  - report.pdf  ->  /data/project/repo/.glenn/attachments/{conversationId}/{firstId}-report.pdf", StringComparison.Ordinal);
        var secondBulletIndex = result.IndexOf($"  - logo.png  ->  /data/project/repo/.glenn/attachments/{conversationId}/{secondId}-logo.png", StringComparison.Ordinal);

        firstBulletIndex.Should().BeGreaterThan(0);
        secondBulletIndex.Should().BeGreaterThan(firstBulletIndex);

        // Blank line between the closing bracket and the user's prompt — the
        // agent treats it as a paragraph break.
        result.Should().Contain("You can read them with the Read tool.]");
        result.Should().EndWith("look at these");
    }

    [Fact]
    public void LocalPathFor_DerivesPathFromConversationAndAttachmentIds()
    {
        var conversationId = Guid.NewGuid();
        var attachmentId = Guid.NewGuid();
        var attachment = new Attachment
        {
            Id = attachmentId,
            ConversationId = conversationId,
            FileName = "doc.pdf",
            R2Key = "ignored-by-this-method",
        };

        // Format is the single source of truth shared with the daemon's
        // staging handshake — keep this assertion verbatim so a drift gets
        // caught by CI rather than at runtime on a missing file.
        var path = PromptPrefixBuilder.LocalPathFor(attachment);

        path.Should().Be($"/data/project/repo/.glenn/attachments/{conversationId}/{attachmentId}-doc.pdf");
    }

    [Fact]
    public void LocalPathFor_PreservesFileNameVerbatim()
    {
        // The on-runtime path embeds the original filename so the agent's Read
        // tool gets a human-meaningful path. We deliberately do NOT sanitise
        // here (that happens at presign time when the R2Key is built); the
        // staged file on disk is named with whatever survived presign.
        var attachment = new Attachment
        {
            Id = Guid.NewGuid(),
            ConversationId = Guid.NewGuid(),
            FileName = "weird name (final).pdf",
            R2Key = "doesnt-matter",
        };

        var path = PromptPrefixBuilder.LocalPathFor(attachment);

        path.Should().EndWith($"-weird name (final).pdf");
    }
}

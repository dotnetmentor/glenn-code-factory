using System.Text.Json;
using Api.Tests.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Source.Features.RuntimeBootstrap.Contracts;
using Source.Features.RuntimePresets.Contracts;
using Source.Features.RuntimePresets.Models;
using Source.Features.RuntimePresets.Services;

namespace Api.Tests.Features.RuntimeCuration;

/// <summary>
/// Coverage for the branch-level required-env merge in the REAL
/// <see cref="PresetExpander"/> (slice 2). A V3 service entry resolves a preset
/// that declares a required var (<c>FOO</c>) and ALSO declares its own ad-hoc
/// vars on the service entry (<c>BAR</c>, plus a duplicate <c>FOO</c>). The
/// expander must:
///
/// <list type="bullet">
///   <item>merge preset-declared + ad-hoc vars into <c>ServiceSpec.RequiredEnv</c>;</item>
///   <item>dedupe by key (first-wins → the preset's <c>FOO</c> survives, the
///         ad-hoc duplicate is dropped);</item>
///   <item>render <c>{{handlebars}}</c> in the description against the same
///         template bag the rest of the preset uses;</item>
///   <item>leave <c>RequiredEnv</c> null when nothing is declared.</item>
/// </list>
///
/// <para>Unlike <see cref="CreateRuntimeProposalCommandHandlerTests"/> (which
/// mocks <see cref="IPresetExpander"/>), this seeds a real
/// <see cref="ServicePreset"/> into the in-memory DB and runs the actual
/// expander end-to-end.</para>
/// </summary>
public class PresetExpanderRequiredEnvTests : HandlerTestBase
{
    private PresetExpander NewExpander() =>
        new(Context, NullLogger<PresetExpander>.Instance);

    /// <summary>
    /// Seeds a minimal preset. <paramref name="requiredEnvJson"/> is the raw
    /// jsonb stored in <c>RequiredEnvContribution</c> (null = preset declares
    /// nothing). The command template references <c>{{repoDir}}</c> only so the
    /// expander succeeds with no params supplied.
    /// </summary>
    private async Task SeedPresetAsync(string slug, string? requiredEnvJson)
    {
        Context.ServicePresets.Add(new ServicePreset
        {
            Id = Guid.NewGuid(),
            Slug = slug,
            DisplayName = slug,
            Description = "test preset",
            Category = PresetCategory.Other,
            IsBuiltIn = false,
            CommandTemplate = "bash -lc 'sleep infinity'",
            EnvTemplate = "{}",
            Parameters = "[]",
            RequiredEnvContribution = requiredEnvJson,
            Autorestart = true,
        });
        await Context.SaveChangesAsync();
    }

    private static ServiceInstance ServiceWith(
        string kind, string name, List<RequiredEnvVar>? adHoc) => new()
    {
        Kind = kind,
        Name = name,
        Values = new Dictionary<string, JsonElement>(),
        RequiredEnv = adHoc,
    };

    [Fact]
    public async Task Expand_merges_preset_and_adhoc_required_env_deduped_first_wins()
    {
        // Preset declares FOO (secret, with a templated description).
        await SeedPresetAsync(
            "with-foo",
            """[{"key":"FOO","description":"key for {{repoDir}}","secret":true}]""");

        var spec = new RuntimeSpecV3
        {
            Version = 3,
            Services = new List<ServiceInstance>
            {
                ServiceWith("with-foo", "svc", new List<RequiredEnvVar>
                {
                    // Ad-hoc BAR — should be added.
                    new() { Key = "BAR", Description = "ad-hoc var", Secret = false },
                    // Duplicate FOO — should be dropped (preset's FOO wins).
                    new() { Key = "FOO", Description = "should be ignored", Secret = false },
                }),
            },
        };

        var result = await NewExpander().ExpandAsync(spec);

        Assert.True(result.IsSuccess, result.Error);
        var service = Assert.Single(result.Value.Services!);
        Assert.NotNull(service.RequiredEnv);

        var keys = service.RequiredEnv!.Select(e => e.Key).ToList();
        Assert.Contains("FOO", keys);
        Assert.Contains("BAR", keys);
        // FOO appears exactly once despite being declared twice.
        Assert.Equal(1, keys.Count(k => k == "FOO"));
        Assert.Equal(2, service.RequiredEnv!.Count);

        // First-wins: the preset's FOO survived, so secret=true and the
        // templated description rendered against the bag ({{repoDir}}).
        var foo = service.RequiredEnv!.Single(e => e.Key == "FOO");
        Assert.True(foo.Secret);
        Assert.Equal("key for /data/project/repo", foo.Description);

        var bar = service.RequiredEnv!.Single(e => e.Key == "BAR");
        Assert.False(bar.Secret);
    }

    [Fact]
    public async Task Expand_preserves_required_false_on_adhoc_vars_through_json_roundtrip()
    {
        await SeedPresetAsync("bare", requiredEnvJson: null);

        var spec = new RuntimeSpecV3
        {
            Version = 3,
            Services = new List<ServiceInstance>
            {
                ServiceWith("bare", "svc", new List<RequiredEnvVar>
                {
                    new() { Key = "Jwt__Key", Secret = true },
                    new() { Key = "OpenRouter__ApiKey", Secret = true, Required = false },
                }),
            },
        };

        var result = await NewExpander().ExpandAsync(spec);

        Assert.True(result.IsSuccess, result.Error);
        var service = Assert.Single(result.Value.Services!);
        var openRouter = service.RequiredEnv!.Single(e => e.Key == "OpenRouter__ApiKey");
        Assert.False(openRouter.Required);
        Assert.True(service.RequiredEnv!.Single(e => e.Key == "Jwt__Key").IsRequired);

        var json = result.Value.ToJson();
        Assert.Contains("\"required\":false", json);
        Assert.DoesNotContain("isRequired", json);

        var parsed = RuntimeSpecV2.TryParse(json);
        Assert.True(parsed.IsSuccess);
        var roundtripped = parsed.Value.Services!.Single()
            .RequiredEnv!.Single(e => e.Key == "OpenRouter__ApiKey");
        Assert.False(roundtripped.Required);
        Assert.False(roundtripped.IsRequired);
    }

    [Fact]
    public async Task Expand_leaves_required_env_null_when_nothing_declared()
    {
        await SeedPresetAsync("bare", requiredEnvJson: null);

        var spec = new RuntimeSpecV3
        {
            Version = 3,
            Services = new List<ServiceInstance>
            {
                ServiceWith("bare", "svc", adHoc: null),
            },
        };

        var result = await NewExpander().ExpandAsync(spec);

        Assert.True(result.IsSuccess, result.Error);
        var service = Assert.Single(result.Value.Services!);
        Assert.Null(service.RequiredEnv);
    }
}

using Source.Shared.CQRS;
using Source.Shared.Results;

namespace Source.Features.ProjectTemplates.Commands.ArchiveProjectTemplate;

/// <summary>
/// Soft-delete (archive) a <see cref="Models.ProjectTemplate"/>. Flips
/// <c>IsDeleted</c>; <see cref="ApplicationDbContext.SaveChangesAsync"/> stamps
/// <c>DeletedAt</c> / <c>DeletedBy</c> via the <c>ISoftDelete</c> interceptor.
///
/// <para><b>Idempotent.</b> A second archive call on an already-tombstoned row
/// (or a non-existent id) returns success — the contract is "this row is
/// archived after the call returns OK". Existing projects holding a
/// <c>TemplateId</c> pointer are unaffected because the FK is configured with
/// <c>ON DELETE SET NULL</c> and the snapshot semantic means the runtime spec
/// was already copied in at create time.</para>
///
/// <para><b>Errors</b> (mapped at the controller):</para>
/// <list type="bullet">
///   <item><c>not_authorized</c> — caller is missing the SuperAdmin role.</item>
/// </list>
/// </summary>
public sealed record ArchiveProjectTemplateCommand(
    Guid TemplateId,
    string CallerUserId
) : ICommand<Result>;

using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Source.Features.ProjectSecrets.Models;
using Source.Features.SystemSettings.Services;
using Source.Features.Workspaces.Models;
using Source.Infrastructure;

namespace Source.Features.ProjectSecrets.Services;

/// <summary>
/// Envelope-encryption core for project secrets. AES-256-GCM with a per-project
/// data encryption key (DEK) wrapped under a master key stored in
/// <see cref="ISystemSettingsService"/>.
///
/// <para><b>Lifetime.</b> Singleton — the resolved master key is cached in a
/// private field after the first read and never re-fetched. Per-project DEKs
/// are NOT cached (we re-unwrap on each call) so plaintext-DEK lifetime stays
/// scoped to a single encrypt/decrypt operation.</para>
///
/// <para><b>Master key.</b> Stored under <see cref="MasterKeyName"/> as base64
/// of 32 raw bytes. Lazily generated on first call if absent. A unique
/// constraint on <see cref="Models.SystemSetting.Key"/> is the race-tiebreaker
/// when two requests trigger lazy creation simultaneously.</para>
///
/// <para><b>DEK wrap.</b> The DEK plaintext is 32 random bytes. We wrap it with
/// AES-GCM using the master key and a deterministic 12-byte nonce derived from
/// SHA-256("dek-wrap-v1" || MasterKeyVersion-BE-4). Determinism is acceptable
/// because we never wrap two distinct DEKs with the same (master, version)
/// pair — a fresh DEK per project means a fresh "message" per nonce. See
/// NIST SP 800-38D §8.2.1 (deterministic IV construction is permitted when the
/// key+IV pair never repeats with a different message). RFC 3394's AES-KW is
/// not usable here because BCL's <c>AesKeyWrap</c> requires 128-bit input
/// blocks aligned, and we want AEAD authentication on the wrap itself anyway.
/// </para>
///
/// <para><b>Value encryption.</b> Per-call random 12-byte nonce. Output layout
/// is <c>ciphertext-payload || 16-byte-tag</c> packed into one byte[]; the
/// nonce is returned alongside it. Decryption splits the trailing 16 bytes
/// back out as the tag.</para>
///
/// <para><b>Errors.</b> AEAD authentication failures (wrong DEK, tampered bytes)
/// surface as <see cref="CryptographicException"/>. Master-key unavailability
/// surfaces as <see cref="InvalidOperationException"/>. We do NOT wrap these in
/// <c>Result&lt;T&gt;</c> — they're security/technical failures, not business
/// outcomes. Callers (CQRS handlers in Card 3) catch and translate as needed.
/// </para>
/// </summary>
public sealed class SecretEncryptionService
{
    public const string MasterKeyName = "secrets.master_key_v1";
    private const string MasterKeyCategory = "secrets.master_key_v1";
    private const string AutoSeedAuthor = "system:auto-seed";
    private const string DekWrapPersonalization = "dek-wrap-v1";
    private const int KeyByteSize = 32;
    private const int NonceByteSize = 12;
    private const int TagByteSize = 16;

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<SecretEncryptionService> _logger;

    // Cached master key. Loaded once; never re-read after first success.
    // Singleton lifetime makes this safe.
    private readonly object _masterGate = new();
    private byte[]? _masterKey;
    private int _masterKeyVersion;

    public SecretEncryptionService(
        IServiceScopeFactory scopeFactory,
        ILogger<SecretEncryptionService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Encrypt <paramref name="plaintext"/> under the project's DEK. Lazily creates
    /// the project's <see cref="ProjectKeyMaterial"/> row on first call. Returns
    /// (ciphertext, nonce, dekVersion) — the caller persists those alongside the
    /// secret row.
    /// </summary>
    /// <remarks>
    /// Ciphertext layout: <c>encrypted-payload || 16-byte-AEAD-tag</c>. Nonce is
    /// 12 random bytes per call. <paramref name="dekVersion"/> records which DEK
    /// produced the ciphertext so the row can be decrypted across rotations.
    /// </remarks>
    public async Task<(byte[] Ciphertext, byte[] Nonce, int DekVersion)> EncryptAsync(
        Guid projectId,
        string plaintext,
        CancellationToken ct)
    {
        var (dek, dekVersion) = await EnsureDekAsync(projectId, ct);
        try
        {
            var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            var nonce = new byte[NonceByteSize];
            RandomNumberGenerator.Fill(nonce);

            var ciphertext = new byte[plaintextBytes.Length + TagByteSize];
            var ctSpan = new Span<byte>(ciphertext, 0, plaintextBytes.Length);
            var tagSpan = new Span<byte>(ciphertext, plaintextBytes.Length, TagByteSize);

            using var gcm = new AesGcm(dek, TagByteSize);
            gcm.Encrypt(nonce, plaintextBytes, ctSpan, tagSpan);

            return (ciphertext, nonce, dekVersion);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>
    /// Decrypt <paramref name="ciphertext"/> using the project's DEK at
    /// <paramref name="dekVersion"/>. Throws <see cref="CryptographicException"/>
    /// on tag mismatch (wrong key, wrong project, tampered bytes, wrong nonce).
    /// </summary>
    /// <remarks>
    /// Expects the ciphertext to follow the layout produced by
    /// <see cref="EncryptAsync"/>: payload concatenated with a trailing 16-byte
    /// AEAD tag. The plaintext byte[] is zeroed before the method returns so the
    /// only remaining copy is the immutable string this method hands back.
    /// </remarks>
    public async Task<string> DecryptAsync(
        Guid projectId,
        byte[] ciphertext,
        byte[] nonce,
        int dekVersion,
        CancellationToken ct)
    {
        if (ciphertext is null) throw new ArgumentNullException(nameof(ciphertext));
        if (nonce is null) throw new ArgumentNullException(nameof(nonce));
        if (ciphertext.Length < TagByteSize)
        {
            throw new CryptographicException(
                $"Ciphertext is too short ({ciphertext.Length} bytes) — must be at least {TagByteSize} bytes for the AEAD tag.");
        }

        var (dek, currentDekVersion) = await EnsureDekAsync(projectId, ct);
        try
        {
            // We don't yet support DEK rotation (Card 2 scope). If the row's DekVersion
            // ever drifts from the row in storage we'd surface a clear error rather
            // than silently fail an AEAD check.
            if (dekVersion != currentDekVersion)
            {
                throw new CryptographicException(
                    $"DEK version mismatch for project {projectId}: ciphertext was produced under v{dekVersion} but the live DEK is v{currentDekVersion}. DEK rotation is not yet supported.");
            }

            var payloadLen = ciphertext.Length - TagByteSize;
            var payload = new ReadOnlySpan<byte>(ciphertext, 0, payloadLen);
            var tag = new ReadOnlySpan<byte>(ciphertext, payloadLen, TagByteSize);

            var plaintextBytes = new byte[payloadLen];
            try
            {
                using var gcm = new AesGcm(dek, TagByteSize);
                gcm.Decrypt(nonce, payload, tag, plaintextBytes);
                return Encoding.UTF8.GetString(plaintextBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintextBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public async Task<(byte[] Ciphertext, byte[] Nonce, int DekVersion)> EncryptForWorkspaceAsync(
        Guid workspaceId,
        string plaintext,
        CancellationToken ct)
    {
        var (dek, dekVersion) = await EnsureWorkspaceDekAsync(workspaceId, ct);
        try
        {
            var plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            var nonce = new byte[NonceByteSize];
            RandomNumberGenerator.Fill(nonce);

            var ciphertext = new byte[plaintextBytes.Length + TagByteSize];
            var ctSpan = new Span<byte>(ciphertext, 0, plaintextBytes.Length);
            var tagSpan = new Span<byte>(ciphertext, plaintextBytes.Length, TagByteSize);

            using var gcm = new AesGcm(dek, TagByteSize);
            gcm.Encrypt(nonce, plaintextBytes, ctSpan, tagSpan);

            return (ciphertext, nonce, dekVersion);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    public async Task<string> DecryptForWorkspaceAsync(
        Guid workspaceId,
        byte[] ciphertext,
        byte[] nonce,
        int dekVersion,
        CancellationToken ct)
    {
        if (ciphertext is null) throw new ArgumentNullException(nameof(ciphertext));
        if (nonce is null) throw new ArgumentNullException(nameof(nonce));
        if (ciphertext.Length < TagByteSize)
        {
            throw new CryptographicException(
                $"Ciphertext is too short ({ciphertext.Length} bytes) — must be at least {TagByteSize} bytes for the AEAD tag.");
        }

        var (dek, currentDekVersion) = await EnsureWorkspaceDekAsync(workspaceId, ct);
        try
        {
            if (dekVersion != currentDekVersion)
            {
                throw new CryptographicException(
                    $"DEK version mismatch for workspace {workspaceId}: ciphertext was produced under v{dekVersion} but the live DEK is v{currentDekVersion}.");
            }

            var payloadLen = ciphertext.Length - TagByteSize;
            var payload = new ReadOnlySpan<byte>(ciphertext, 0, payloadLen);
            var tag = new ReadOnlySpan<byte>(ciphertext, payloadLen, TagByteSize);

            var plaintextBytes = new byte[payloadLen];
            try
            {
                using var gcm = new AesGcm(dek, TagByteSize);
                gcm.Decrypt(nonce, payload, tag, plaintextBytes);
                return Encoding.UTF8.GetString(plaintextBytes);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plaintextBytes);
            }
        }
        finally
        {
            CryptographicOperations.ZeroMemory(dek);
        }
    }

    /// <summary>
    /// Look up (or lazily create) the project's <see cref="ProjectKeyMaterial"/>
    /// row, unwrap the DEK, and hand back the plaintext key + its version.
    /// Caller is responsible for <see cref="CryptographicOperations.ZeroMemory"/>
    /// on the returned DEK after use; this service's own callers above already
    /// do so in a <c>finally</c> block.
    /// </summary>
    private async Task<(byte[] Dek, int DekVersion)> EnsureDekAsync(Guid projectId, CancellationToken ct)
    {
        var (master, masterVersion) = LoadOrSeedMasterKey();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var row = await db.ProjectKeyMaterials
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.ProjectId == projectId, ct);

        if (row is null)
        {
            // Lazy create. Generate a fresh 32-byte DEK, wrap with master, insert.
            // Race: if another concurrent caller wins the unique-(ProjectId) index,
            // we swallow the DbUpdateException and re-read the winning row.
            var dek = RandomNumberGenerator.GetBytes(KeyByteSize);
            var wrapped = WrapDek(dek, master, masterVersion);
            var entity = new ProjectKeyMaterial
            {
                Id = Guid.NewGuid(),
                ProjectId = projectId,
                WrappedDek = wrapped,
                MasterKeyVersion = masterVersion,
            };
            db.ProjectKeyMaterials.Add(entity);

            try
            {
                await db.SaveChangesAsync(ct);
                // Caller is responsible for ZeroMemory on the returned DEK after use.
                return (dek, entity.MasterKeyVersion);
            }
            catch (DbUpdateException)
            {
                // Lost the race. Drop our generated DEK; the winner's row will be
                // re-read below and we'll unwrap that DEK instead.
                CryptographicOperations.ZeroMemory(dek);

                // Detach the failed insert so the next read against the same context
                // doesn't trip over a duplicate tracked entity.
                db.Entry(entity).State = EntityState.Detached;

                row = await db.ProjectKeyMaterials
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.ProjectId == projectId, ct)
                    ?? throw new InvalidOperationException(
                        $"DbUpdateException on ProjectKeyMaterial insert for project {projectId} but no winning row found on re-read.");
                // Fall through to the unwrap path below.
            }
        }

        // Existing or just-lost-the-race row.
        var unwrapped = UnwrapDek(row!.WrappedDek, master, row.MasterKeyVersion);
        return (unwrapped, row.MasterKeyVersion);
    }

    private async Task<(byte[] Dek, int DekVersion)> EnsureWorkspaceDekAsync(Guid workspaceId, CancellationToken ct)
    {
        var (master, masterVersion) = LoadOrSeedMasterKey();

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var row = await db.WorkspaceKeyMaterials
            .AsNoTracking()
            .FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId, ct);

        if (row is null)
        {
            var dek = RandomNumberGenerator.GetBytes(KeyByteSize);
            var wrapped = WrapDek(dek, master, masterVersion);
            var entity = new WorkspaceKeyMaterial
            {
                Id = Guid.NewGuid(),
                WorkspaceId = workspaceId,
                WrappedDek = wrapped,
                MasterKeyVersion = masterVersion,
            };
            db.WorkspaceKeyMaterials.Add(entity);

            try
            {
                await db.SaveChangesAsync(ct);
                return (dek, entity.MasterKeyVersion);
            }
            catch (DbUpdateException)
            {
                CryptographicOperations.ZeroMemory(dek);
                db.Entry(entity).State = EntityState.Detached;

                row = await db.WorkspaceKeyMaterials
                    .AsNoTracking()
                    .FirstOrDefaultAsync(x => x.WorkspaceId == workspaceId, ct)
                    ?? throw new InvalidOperationException(
                        $"DbUpdateException on WorkspaceKeyMaterial insert for workspace {workspaceId} but no winning row found on re-read.");
            }
        }

        var unwrapped = UnwrapDek(row!.WrappedDek, master, row.MasterKeyVersion);
        return (unwrapped, row.MasterKeyVersion);
    }

    /// <summary>
    /// Wrap a 32-byte DEK with the master key. AES-GCM with a deterministic 12-byte
    /// nonce derived from SHA-256("dek-wrap-v1" || MasterKeyVersion-BE-4); see class
    /// docs for the safety argument. Output layout: <c>ciphertext(32) || tag(16)</c>.
    /// </summary>
    private static byte[] WrapDek(byte[] dek, byte[] master, int masterVersion)
    {
        var nonce = DeriveDekWrapNonce(masterVersion);
        var output = new byte[dek.Length + TagByteSize];
        var ct = new Span<byte>(output, 0, dek.Length);
        var tag = new Span<byte>(output, dek.Length, TagByteSize);

        using var gcm = new AesGcm(master, TagByteSize);
        gcm.Encrypt(nonce, dek, ct, tag);
        return output;
    }

    private static byte[] UnwrapDek(byte[] wrapped, byte[] master, int masterVersion)
    {
        if (wrapped.Length < TagByteSize)
        {
            throw new CryptographicException(
                $"WrappedDek is too short ({wrapped.Length} bytes) — must be at least {TagByteSize} for the AEAD tag.");
        }

        var nonce = DeriveDekWrapNonce(masterVersion);
        var payloadLen = wrapped.Length - TagByteSize;
        var payload = new ReadOnlySpan<byte>(wrapped, 0, payloadLen);
        var tag = new ReadOnlySpan<byte>(wrapped, payloadLen, TagByteSize);

        var dek = new byte[payloadLen];
        using var gcm = new AesGcm(master, TagByteSize);
        gcm.Decrypt(nonce, payload, tag, dek);
        return dek;
    }

    private static byte[] DeriveDekWrapNonce(int masterVersion)
    {
        // SHA-256("dek-wrap-v1" || MasterKeyVersion-BE-4), first 12 bytes.
        var personalization = Encoding.ASCII.GetBytes(DekWrapPersonalization);
        var versionBytes = new byte[4];
        versionBytes[0] = (byte)((masterVersion >> 24) & 0xFF);
        versionBytes[1] = (byte)((masterVersion >> 16) & 0xFF);
        versionBytes[2] = (byte)((masterVersion >> 8) & 0xFF);
        versionBytes[3] = (byte)(masterVersion & 0xFF);

        var input = new byte[personalization.Length + versionBytes.Length];
        Buffer.BlockCopy(personalization, 0, input, 0, personalization.Length);
        Buffer.BlockCopy(versionBytes, 0, input, personalization.Length, versionBytes.Length);

        var hash = SHA256.HashData(input);
        var nonce = new byte[NonceByteSize];
        Buffer.BlockCopy(hash, 0, nonce, 0, NonceByteSize);
        return nonce;
    }

    private (byte[] Master, int Version) LoadOrSeedMasterKey()
    {
        // Fast path — already cached.
        var cached = _masterKey;
        if (cached is not null) return (cached, _masterKeyVersion);

        lock (_masterGate)
        {
            // Double-check under the lock so concurrent first-readers don't both seed.
            if (_masterKey is not null) return (_masterKey, _masterKeyVersion);

            using var scope = _scopeFactory.CreateScope();
            var settings = scope.ServiceProvider.GetRequiredService<ISystemSettingsService>();

            var existingB64 = settings.Get(MasterKeyName);
            if (string.IsNullOrWhiteSpace(existingB64))
            {
                // Lazy generate. Persist via the SystemSettings API; the unique
                // constraint on SystemSetting.Key is the tiebreaker if two
                // singletons in different processes ever raced (we're a singleton
                // per process so within-process the lock above already serializes us).
                var generated = RandomNumberGenerator.GetBytes(KeyByteSize);
                var generatedB64 = Convert.ToBase64String(generated);
                try
                {
                    settings.SetAsync(MasterKeyName, generatedB64, isSecret: true, updatedBy: AutoSeedAuthor)
                        .GetAwaiter().GetResult();
                    existingB64 = generatedB64;
                    _logger.LogInformation(
                        "Auto-seeded a new project-secrets master key (v1). Existing wrapped DEKs — none should exist on first boot — would be unrecoverable.");
                }
                catch (DbUpdateException)
                {
                    // Cross-process race (rare): another instance won. Re-read.
                    settings.InvalidateCategory(MasterKeyCategory);
                    existingB64 = settings.Get(MasterKeyName);
                    if (string.IsNullOrWhiteSpace(existingB64))
                    {
                        throw new InvalidOperationException(
                            "DbUpdateException on master-key insert but no winning row found on re-read.");
                    }
                }
            }

            byte[] bytes;
            try
            {
                bytes = Convert.FromBase64String(existingB64!);
            }
            catch (FormatException ex)
            {
                throw new InvalidOperationException(
                    $"{MasterKeyName} is not valid base64. Re-seed via the SystemSettings admin UI.", ex);
            }
            if (bytes.Length != KeyByteSize)
            {
                throw new InvalidOperationException(
                    $"{MasterKeyName} must decode to exactly {KeyByteSize} bytes (got {bytes.Length}).");
            }

            _masterKey = bytes;
            // Version is implicit "v1" from the key name today; future master-key
            // rotation introduces a v2 key with a separate setting key and the
            // ProjectKeyMaterial.MasterKeyVersion column tracks which one wrapped
            // each row's DEK.
            _masterKeyVersion = 1;
            return (_masterKey, _masterKeyVersion);
        }
    }
}

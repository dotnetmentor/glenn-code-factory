# Security Policy

## Reporting a vulnerability

**Please do not report security vulnerabilities through public GitHub issues,
discussions, or pull requests.**

Instead, use GitHub's private vulnerability reporting:

1. Go to the **Security** tab of this repository.
2. Click **Report a vulnerability**.
3. Provide as much detail as you can — affected component, reproduction steps,
   and impact.

We aim to acknowledge reports within a few business days and will keep you
updated on remediation progress.

## Scope

This project is security-sensitive: it provisions agent runtimes, stores
encrypted secrets, and integrates with GitHub Apps, Fly.io, and Cloudflare.
Reports we especially care about:

- Authentication/authorization bypass (JWT, runtime tokens, CI-publish key, Hangfire dashboard).
- Secret exposure or weaknesses in the envelope encryption of project/workspace secrets.
- Server-side request forgery, injection, or privilege escalation in the orchestrator API or daemon.
- Sandbox escape or unintended host access from the agent runtime.

## Hardening reminders for operators

- `SystemSettings__EncryptionKey` and `Jwt__Key` must be strong and **backed up** — losing the encryption key makes DB-encrypted secrets unrecoverable.
- Store `CiPublish__ApiKey` only in protected GitHub environments; rotate it together with `Fly:ApiToken` if it leaks.
- SignalR uses an in-memory backplane — run a single instance unless you add Redis.
- "YOLO mode" (auto-approve agent tool calls) is enabled by default per conversation; review whether that default suits your deployment.

Thank you for helping keep GlennCode Factory and its users safe.

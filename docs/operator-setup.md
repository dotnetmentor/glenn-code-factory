# Operator setup guide (self-hosted / production)

**Canonical end-to-end setup for this repo.** The [README](../README.md) summarizes paths and local dev; follow this document for Box, GitHub, Cloudflare, Render, publish, and smoke-test steps.

Step-by-step checklist for running GlennCode Factory: control plane, GitHub App, Box runtimes, Cloudflare preview tunnels, and publish pipelines.

**Audience:** Humans deploying a new environment (local or production).  
**Style:** Prefer CLI where it exists; call out UI-only steps honestly.

**Forks:** Runtimes share one Box account; the golden template box is created by `scripts/build-box-template.sh` and registered in Super Admin → Runtime Templates.

**Related docs:**

| Doc | Use when |
|-----|----------|
| [README — How to set up end-to-end](../README.md#how-to-set-up-end-to-end) | Short overview + dev tunnel behavior |
| [`.env.example`](../.env.example) | Every `Section__Key` env var |
| [`render.yaml`](../render.yaml) | Render blueprint |
| [runtime-volume-layout.md](./runtime-volume-layout.md) | Runtime box disk layout |

---

## Mental model (read once)

```text
Browser ──► Orchestrator API (Render or local) ──► PostgreSQL
                │  SignalR / REST
                ▼
         Box VM (forked from golden template) ──► GitHub (clone/push)
                │  cloudflared preview
                ▼
         Cloudflare (*.your-base-domain)

Golden template box (stopped/snapshotted) → forked per runtime
                                         (base image build only)
```

| Name | What it is | Where configured |
|------|------------|------------------|
| **Control plane** | .NET API + React UI | Render / `npm run dev` |
| **Daemon bundle** | `daemon.js` tarball in R2/local storage | `./scripts/publish-daemon.sh` |
| **Runtime image row** | Active row in Runtime Images catalog | `./scripts/publish-runtime-image-remote.sh` or CI |

**Common mistake:** Setting **App Name** to `glenn-runtime-base`. Machines must use **`glenn-runtimes`** (or your chosen machines app). The base image app is only for builds.

---

## 0. Install CLI tools

```bash
# Box CLI (optional — the platform talks to the HTTP API directly)
# see https://docs.ascii.dev/ for install instructions
# Add to ~/.zshrc:

# GitHub CLI (optional but useful)
brew install gh          # macOS; Linux: https://github.com/cli/cli#installation
gh auth login

# Cloudflare tunnel client (local dev only — npm run dev can install it for you)
brew install cloudflared   # macOS; Linux: https://developers.cloudflare.com/cloudflare-one/connections/connect-networks/downloads/

# OpenSSL (usually preinstalled)
openssl version
```

Verify:

```bash
box --version   # if you installed the CLI
```

---

## 1. Control plane bootstrap

### 1a. Local (development)

```bash
git clone <your-repo-url>
cd glenn-code-factory
cp .env.example .env
```

Generate secrets:

```bash
openssl rand -base64 32   # → SystemSettings__EncryptionKey
openssl rand -base64 48   # → Jwt__Key
```

Edit `.env`: set `SystemSettings__EncryptionKey`, `Jwt__Key`, `Bootstrap__SuperAdminEmail`.

```bash
npm run setup    # Docker Postgres + migrations
npm run dev      # API :5338, UI :5173, Cloudflare quick tunnel for runtime callback
```

Login: OTP prints in the **API terminal** when `Email__Provider=Console`.

**Local API base URL** (use in curl examples and publish scripts on this machine):

```bash
export API=http://localhost:5338
```

### 1b. Production (Render)

1. Render Dashboard → **New → Blueprint** → point at this repo ([`render.yaml`](../render.yaml)).
   - Blueprint deploys **Postgres + API in `frankfurt`** — Box runtimes live in EU regions (DE/FI/FR), so daemon ↔ API hops stay in EU.
   - **Already on `oregon`?** Render cannot change region in place — create a new `frankfurt` database + web service (or new Blueprint), dump/restore Postgres, update DNS/`factory.glenncode.ai`, then retire the old stack.
2. After deploy, set secrets on **`orchestrator-api`** (not the database):

```bash
# Generate locally, paste into Render dashboard
openssl rand -base64 32   # SystemSettings__EncryptionKey  (back up!)
openssl rand -base64 48   # Jwt__Key
openssl rand -base64 48   # CiPublish__ApiKey  (CI only; see §8)
```

| Render env var | Purpose |
|----------------|---------|
| `SystemSettings__EncryptionKey` | Encrypts System Settings secrets in DB |
| `Jwt__Key` | User/session JWT signing |
| `Bootstrap__SuperAdminEmail` | First SuperAdmin login email |
| `Email__Resend__ApiToken` / `Email__Resend__FromEmail` | Login OTP email |
| `FileStorage__R2__*` | Daemon bundle + uploads (see §6) |
| `Runtime__PublicApiUrl` | `https://<your-orchestrator-api>.onrender.com` (no trailing slash) |
| `CiPublish__ApiKey` | GitHub Actions publish auth (§8) |

Set `FileStorage__Provider=R2` in Render if using R2 (not `Local`).

### 1c. Backup Oregon Postgres before EU migration (`pg_dump`)

Render cannot move a database to another region in place ([Regions](https://render.com/docs/regions)). Take a logical backup first:

1. **Render Dashboard** → **`orchestrator-db`** → **Connect** → copy **External Database URL** (from your laptop; not the internal URL).
2. If Render offers **direct** vs **pooled**, use **direct** for `pg_dump`.
3. From repo root:

```bash
export DATABASE_URL='postgresql://…'   # paste External URL; do not commit
chmod +x scripts/render-pg-dump.sh
./scripts/render-pg-dump.sh
```

Output: `.render-backups/render-YYYYMMDD-HHMMSS.dump` (gitignored). Custom format (`-Fc`) for `pg_restore` into a new Frankfurt instance.

**Dashboard alternative (paid plans):** database **Recovery** → trigger/download export, then `pg_restore` per [Render backups](https://render.com/docs/postgresql-backups).

**Dry run:** create empty Frankfurt DB, restore this dump, verify row counts — before switching `factory.glenncode.ai`.

### 1d. Restore into Frankfurt (`pg_restore`)

Prerequisites: new **`orchestrator-db`** in **`frankfurt`** (Blueprint or dashboard), empty or disposable. Oregon dump from §1c.

1. **Render Dashboard** → **new** `orchestrator-db` (Frankfurt) → **Connect** → **External Database URL**.
2. Pause writes if you need a clean cutover (stop Oregon `orchestrator-api` or accept drift between dump and restore).
3. From repo root:

```bash
export DATABASE_URL='postgresql://…'   # Frankfurt external URL

# explicit dump (recommended)
./scripts/render-pg-restore.sh .render-backups/render-20260602-120000.dump

# or latest .render-backups/render-*.dump
./scripts/render-pg-restore.sh
```

4. Quick sanity check:

```bash
psql "$DATABASE_URL" -c "SELECT COUNT(*) FROM \"Projects\";"
psql "$DATABASE_URL" -c "SELECT COUNT(*) FROM \"DaemonVersions\";"
```

5. Deploy **Frankfurt** `orchestrator-api`, copy secrets from Oregon, set `Runtime__PublicApiUrl` / R2 / Box settings, smoke-test.
6. Point **Cloudflare** / DNS at the new service URL, then decommission Oregon.

`pg_restore` sometimes exits non-zero with benign warnings (extensions, missing roles). Read the log; if tables are populated, proceed.

---

## 2. Box (box.ascii.dev)

Runtimes are Box VMs forked from a golden template box. One Box account hosts
everything; no app namespaces or registries.

### 2a. Account + API key (CLI)

```bash
# Install the box CLI (see https://docs.ascii.dev/), sign up ($20/month account
# minimum — this pre-buys ~555 hours of default-size compute), then:
box api-key create
```

### 2b. Pin the wire assumptions (first run on any account)

```bash
BOX_API_KEY='...' ./scripts/box-smoke-test.sh
```

This exercises every Box API verb the platform's `BoxClient` uses (fork, per-fork
env delivery, stop/resume, TTL patch, delete-confirmation header, ...) against a
disposable box and flags any drift between assumptions and the live API. Fix
flagged items in `BoxClient.cs` / `build-box-template.sh` before continuing.

### 2c. System Settings → Box

| Key | Example | Notes |
|-----|---------|--------|
| **API Key** | `box_...` | From §2a |
| **API Base URL** | `https://api.ascii.dev/v1` | Only change if Box moves hosts |
| **Default TTL (Seconds)** | `21600` | The orphan-cost guardrail — a box whose TTL lapses archives itself and billing stops. Never 0 in production. |
| **Default Size** | `small` | 2 vCPU / 4 GB; per-project cpu/mem specs round up to a tier |

Test in UI: **System Settings → Box → Test connection** (or fix config until valid).

### 2d. Build + register the golden template box (CLI)

```bash
export BOX_API_KEY='...'
# optional: auto-register with the platform
export REGISTER_URL='https://your-api.example' CI_PUBLISH_KEY='...'
./scripts/build-box-template.sh
```

Provisions a fresh box with the full runtime stack (Node 20, postgres,
supervisord under a systemd unit, mise, Playwright, cloudflared, the daemon
bootstrap), stops it — the stop snapshot IS the template — and registers it as
the Active `RuntimeTemplate`. Without auto-registration, register the printed
box id in **Super Admin → Runtime Templates**.

Confirm **Super Admin → Runtime Templates** shows one **Active** row.

**Start budget note:** Box caps machine starts account-wide (~600/hr, 1,500/day);
create/fork/resume each count as one. The platform is designed around this
(wake per session, provisioner batching), but keep it in mind for bulk operations.

---

## 3. GitHub App (mostly UI)

GitHub does **not** offer a supported CLI to create a new GitHub App. Use the UI once, then paste values into System Settings.

### 3a. Create the app

1. GitHub → **Settings → Developer settings → GitHub Apps → New GitHub App**
2. Or org: `https://github.com/organizations/<org>/settings/apps`

**URLs** (replace host with your API base):

| GitHub App field | Value |
|------------------|--------|
| Homepage URL | `https://<api-host>/` |
| Callback URL (user OAuth) | `https://<api-host>/api/github/login/callback` |
| Setup URL (install) | `https://<api-host>/api/github/install/callback` |
| Webhook URL | `https://<api-host>/api/github/webhooks` |

Local dev defaults (if API on localhost): see catalog defaults `http://localhost:5338/api/github/...`

Enable **Request user authorization (OAuth) during installation** if users must create repos under their personal account (blank repos / starters on a user-owned installation).

**Repository permissions**

| Permission | Access | Why |
|------------|--------|-----|
| **Contents** | Read & write | Clone, push, branch/file APIs (`contents:write` on scoped tokens) |
| **Metadata** | Read only | List repos, resolve refs |
| **Administration** | Read & write | Create repos on org/user installations |

**Organization permissions** (when installing on an org)

| Permission | Access | Why |
|------------|--------|-----|
| **Administration** | Read & write | `POST /orgs/{org}/repos` for “new blank repo” flows |

**Subscribe to events:** `installation`, `installation_repositories` (required). `push` and `pull_request` are optional (handlers are placeholders today).

User login uses the App’s **OAuth** flow (`read:user` + `user:email` scopes) when “Request user authorization during installation” is enabled — separate from the repository permission table above.

### 3b. Paste into System Settings → GitHub

| System Settings key | Source on GitHub App page |
|---------------------|---------------------------|
| `GitHub:AppId` | App ID (numeric) |
| `GitHub:ClientId` | Client ID |
| `GitHub:ClientSecret` | Client secret |
| `GitHub:PrivateKeyPem` | Generate private key → paste PEM |
| `GitHub:WebhookSecret` | Webhook → Secret |
| `GitHub:AppSlug` | `https://github.com/apps/<slug>` |
| `GitHub:OAuthRedirectUri` | Must match callback URL exactly |
| `GitHub:AppInstallRedirectUri` | Must match setup URL exactly |

### 3c. Install the app

Use the workspace UI (**Install GitHub App**) or:

```bash
# Open install page (after AppSlug is configured)
open "https://github.com/apps/<your-app-slug>/installations/new"
```

---

## 4. Cloudflare (preview subdomain pool)

Used for per-branch preview URLs (`{random}.{base-domain}`). Configure in **System Settings → Cloudflare**.

### 4a. API token (dashboard)

[Create token](https://dash.cloudflare.com/profile/api-tokens) with at least:

- Account → **Cloudflare Tunnel** → Edit  
- Zone → **DNS** → Edit  

Paste into **Cloudflare:ApiToken**.

### 4b. Account ID and Zone ID (CLI)

```bash
export CF_API_TOKEN='your-token'

# Account ID
curl -fsS -H "Authorization: Bearer $CF_API_TOKEN" \
  https://api.cloudflare.com/client/v4/accounts \
  | jq '.result[] | {name, id}'

# Zone ID (for your apex domain)
curl -fsS -H "Authorization: Bearer $CF_API_TOKEN" \
  "https://api.cloudflare.com/client/v4/zones?name=example.com" \
  | jq '.result[] | {name, id}'
```

Set in System Settings:

| Key | Example |
|-----|---------|
| `Cloudflare:AccountId` | 32-char hex |
| `Cloudflare:ZoneId` | 32-char hex |
| `Cloudflare:BaseDomain` | `example.com` (apex, no `https://`) |

### 4c. Fill the pool

**Production (recommended):** **Super Admin → Subdomains** → batch-create rows (no local DB required).

**Local dev (CLI)** — API running on this machine with Postgres + `.env` (see §1a):

```bash
export API=http://localhost:5338
export JWT=$(node scripts/lib/platform-auth.mjs jwt)

curl -fsS -X POST "$API/api/cloudflare/subdomains/batch" \
  -H "Authorization: Bearer $JWT" \
  -H "Content-Type: application/json" \
  -d '{"count":10}' | jq .

curl -fsS "$API/api/cloudflare/subdomains" \
  -H "Authorization: Bearer $JWT" | jq .
```

---

## 5. Runtime public URL

| Environment | `Runtime:PublicApiUrl` |
|-------------|-------------------------|
| **Local + Box runtimes** | Set automatically by `npm run dev` (Cloudflare quick tunnel). Respawn runtimes when URL changes. |
| **Production** | `https://<orchestrator-api-host>` — stable hostname, **not** a quick tunnel |

Runtime boxes dial this URL for HTTP + SignalR (`/hubs/runtime`). If unreachable, runtimes stay stuck or chat fails.

```bash
# Render: set once you know the service URL
# Runtime__PublicApiUrl=https://orchestrator-api-xxxx.onrender.com
```

---

## 6. File storage (daemon bundles)

| Mode | When | Config |
|------|------|--------|
| **Local** | Dev | `FileStorage__Provider=Local` in `.env` |
| **R2** | Production | `FileStorage__Provider=R2` + R2 keys on Render |

### R2 via Wrangler (CLI)

```bash
npm install -g wrangler
wrangler login

# Create bucket (name → FileStorage__R2__BucketName)
wrangler r2 bucket create glenn-daemon-bundles

# Create API token in Cloudflare dashboard with R2 read/write for that bucket
```

Set on Render: `FileStorage__R2__AccountId`, `AccessKey`, `SecretKey`, `BucketName`, `PublicUrl` (if using public bucket or custom domain).

---

## 7. Publish daemon bundle (CLI)

```bash
# API must be running; uses .env + System Settings storage
./scripts/publish-daemon.sh
```

Verify:

```bash
curl -fsS "$API/api/daemon-versions/resolve?channel=stable" | jq .
```

After **SignalR hub contract** changes:

```bash
./scripts/generate-signalr.sh
./scripts/publish-daemon.sh
```

---

## 8. GitHub Actions (CI publish)

**On Render (`orchestrator-api`):**

```bash
openssl rand -base64 48   # CiPublish__ApiKey
```

**On GitHub repo → Settings → Secrets:**

| Secret | Value |
|--------|--------|
| `CONTROL_PLANE_API` | `https://<orchestrator-api-host>` |
| `CONTROL_PLANE_PUBLISH_API_KEY` | Same as `CiPublish__ApiKey` |

Workflows: [`.github/workflows/publish-daemon.yml`](../.github/workflows/publish-daemon.yml), [`.github/workflows/runtime-base-image.yml`](../.github/workflows/runtime-base-image.yml).

Manual republish:

```bash
# GitHub → Actions → workflow → Run workflow → check "force"
```

Template builds are an operator action (`scripts/build-box-template.sh` against a live Box account) — no CI workflow.

---

## 9. First project smoke-test

Checklist (in order):

- [ ] System Settings: GitHub, Box, Cloudflare, Runtime URL filled
- [ ] `./scripts/box-smoke-test.sh` passes; Super Admin → Runtime Templates shows one Active row
- [ ] `./scripts/publish-daemon.sh` succeeded
- [ ] `./scripts/publish-runtime-image-remote.sh` (or CI) — Active **Runtime Image** in Super Admin
- [ ] `curl -fsS "$API/api/daemon-versions/resolve?channel=stable" | jq .` returns a bundle (`API` from §1a locally)
- [ ] Subdomain pool has free rows (UI or §4c)
- [ ] Workspace created; GitHub App installed on org/repos
- [ ] Project created (GitHub-backed repo)
- [ ] Runtime Monitor: `Pending → … → Online` (~90s)
- [ ] Project chat: send a prompt (workspace or project **CURSOR_API_KEY** / BYOK)

---

## 10. Troubleshooting

| Symptom | Likely cause | Fix |
|---------|--------------|-----|
| `Box rejected the runtime request (...)` | Bad API key or wire drift | System Settings → Box → Test connection; re-run `box-smoke-test.sh` |
| `provisioner:no_active_image` | No Active runtime image | Run `publish-runtime-image-remote.sh` or CI |
| `provisioner:incomplete_box_config` | Missing Box settings | Fill Box:ApiKey |
| `provisioner:no_active_template` | No template registered | §2d |
| `pool_empty` on project create | No Cloudflare pool rows | §4c batch create |
| Daemon never connects | Bad `Runtime:PublicApiUrl` | Stable URL; respawn runtime |
| CI image build fails Trivy | OS CVEs in base image | Rebuild after Dockerfile security updates |

Stuck runtimes: [runtime-debug skill](../.claude/skills/runtime-debug/SKILL.md).

---

## 11. Environment backup (optional)

Export/import via **Super Admin → Environment Backup** — restores System Settings, workspaces, projects, etc.

**Still required on target:** publish daemon + golden template, subdomain pool, fresh box forks on respawn. See [README Path B](../README.md#path-b--environment-backup).

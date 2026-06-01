# `/opt/agent` — runtime daemon slot

The runtime base image (`Dockerfile.runtime-base`) reserves `/opt/agent/` for the
**daemon bundle**. The image ships with this directory empty (or with this README
only in source builds).

## How the daemon gets here

At machine boot, supervisord runs `docker/bootstrap-daemon.sh`, which:

1. Resolves the active daemon version from the API (`GET /api/daemon-versions/resolve`)
2. Downloads the tarball from object storage (`DAEMON_BUNDLE_URL`)
3. Verifies `DAEMON_BUNDLE_SHA256`
4. Extracts into `/opt/agent/`
5. `exec node /opt/agent/daemon.js`

The daemon is **not baked into the base image**. Publish a new bundle with
`./scripts/publish-daemon.sh` — no image rebuild required.

## Local development

For local daemon iteration, run the daemon directly from `packages/daemon`
(`npm run dev` / `npm run build`) against a local API — you do not need this
placeholder path unless you are testing the full bootstrap script.

See `.claude/skills/runtime-environment/SKILL.md` for the full runtime architecture.

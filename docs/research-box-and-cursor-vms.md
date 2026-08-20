# Research: Box (ascii.dev) and Cursor VMs as project-runtime infrastructure

*Researched 2026-08-20. Prices verified against vendor pages and third-party comparisons on that date; sandbox pricing moves fast — re-verify before committing.*

## What our use case actually requires

Every project on the platform gets a **Runtime**: today a Fly.io Machine (default `shared` CPU, 1 vCPU, 2048 MB — see `CreateMachineRequest.cs`) with a persistent `/data` volume. The runtime must:

1. **Persist state per project, forever** — repo clone, Postgres data, env files, mise toolchains, install hashes survive suspend/resume and machine respawn (Fly volume + `persist_rootfs="always"` today).
2. **Run long-lived user services** under supervisord: Postgres, .NET API (:5338), Vite dev server (:5173), Cloudflare tunnel exposing the frontend to the user's browser.
3. **Run the agent in-VM** — the daemon drives `@cursor/sdk` in *local* runtime mode against the working tree, so edits hot-reload instantly into the user's preview. This is why the 2 GiB RAM floor exists.
4. **Be orchestrated by our control plane** — create / suspend / wake / respawn / delete via HTTP API (`RuntimeProvisionerJob`, `RuntimeReconcilerJob`), daemon phones home over SignalR with a JWT.
5. **Cost ~nothing when idle** — runtimes suspend; a stopped Fly machine costs only rootfs storage ($0.15/GB per 30 days).
6. **Support branch-fork** — copy-branch fork provisions a runtime from an existing one (volume copy today).

Any candidate has to be judged against all six, not just price.

---

## Option A — Box (box.ascii.dev, by ASCII)

### What it is

"The cheapest, most powerful sandbox for agents — built for agent factories." Persistent **full Ubuntu VMs** (not gVisor/Firecracker code-exec sandboxes): SSH/SCP, Docker-in-VM, dedicated IPv4 per machine, public HTTPS hosting, a 60fps virtual desktop, and disk-level snapshot/fork. Pre-installed: Docker, VS Code, Chrome, GitHub CLI, Node, Bun, Rust; `box prompt` can even run Claude/Codex harnesses out of the box. CLI + HTTP API + TypeScript/Python SDKs.

### Specs & pricing

| Size | Specs | Rate | 24/7 month |
|------|-------|------|-----------|
| Small | 2 vCPU / 4 GB | $0.018/hr (0.5×) | ~$13.3 |
| Default | 4 vCPU / 8 GB | $0.036/hr ($0.00001/s) | ~$26.6 |
| Large | 8 vCPU / 16 GB | $0.072/hr (2×) | ~$53 |

- **$20/month account minimum** = 2,000,000 VM-seconds (~555 hrs of default-size), billed per second, pooled across all boxes.
- Storage (>50 GB), snapshots, IPv4, desktop, HTTPS hosting **included** — no separate volume/bandwidth line items.
- **Stop pauses billing**; files, apt packages, and enabled systemd services persist through stop/resume; resume "in a few seconds".
- Self-serve concurrency 100–1,200 boxes; default TTL 1 hour but overrideable ("no session cap / runs 24/7").
- **EU regions only** (Germany, Finland, France) — fine, arguably good, for our Swedish user base.

### Fit against our requirements

| Requirement | Verdict |
|---|---|
| Persistent per-project state | ✅ Whole-disk persistence via snapshot on stop. Simpler model than Fly's volume + rootfs split — **the entire `persist_rootfs` / install-hash / `installVerify` gotcha class disappears** because apt installs live on the same persisted disk as `/data`. |
| Long-lived services | ✅ Full VM; systemd services explicitly persist. Supervisord layout ports as-is. |
| In-VM agent | ✅ Full Ubuntu; smallest size (2 vCPU/4 GB) already doubles our current 1 vCPU/2 GB spec. |
| Control-plane API | ⚠️ HTTP API + SDKs exist (new/stop/resume/fork/list), but far younger and thinner than Fly's Machines API. `RuntimeProvisionerJob`, `RespawnRuntimeJob`, `RuntimeReconcilerJob`, and all of `FlyManagement/` would need a Box driver. No stated SLA. |
| Cheap when idle | ✅ Billing pauses on stop, snapshots free. Comparable to suspended Fly machines. |
| Branch-fork | ✅✅ **Disk-level `box fork` is a native primitive** — a better fit for our copy-branch fork than Fly volume copies. |

### Price vs. our current Fly setup

Current runtime (Fly `shared-cpu-1x`, 2 GB, Amsterdam): **$0.0154/hr ≈ $11.11/mo** running, plus volume ($0.15/GB/mo, e.g. ~$1.50 for 10 GB) and $2/mo if a dedicated IPv4 were needed. Suspended: near zero.

- Box Small (2 vCPU/4 GB, storage + IPv4 included) ≈ **$13.3/mo** running — roughly the same monthly cost as today for **2× the CPU and RAM**, which would comfortably clear the `@cursor/sdk` OOM floor.
- At matched specs Box is ~40–45% cheaper than Fly: Box default 4/8 = $0.036/hr vs Fly `shared-4x`/8GB = $0.0617/hr.
- Against the sandbox-specialist crowd it's not close: 2 vCPU/4 GB ≈ $0.166/hr on E2B/Daytona (~9× Box), more on Modal/Vercel/Cloudflare. Box's own compare page (50 VMs × 240 hrs of 4/8): Box $432/mo vs Daytona $3,974, E2B $4,124, Modal $7,160.

### Risks

- **Vendor maturity is the big one.** Box launched ~2026 from a small founder-led team (ascii.dev); no published SLA, no track record comparable to Fly. Our entire product dies if the runtime host dies.
- Cold start is slower than the micro-sandbox players (they optimize for full-VM boot, not <500 ms) — matters for wake-on-first-message latency; needs measuring.
- EU-only is fine for us today but caps geographic expansion.
- Migration cost: a `BoxManagement` driver + provisioner/reconciler changes + rethinking the volume-vs-disk persistence model and machine env stamping. Days-to-weeks, not hours.

---

## Option B — Cursor VMs (Cloud Agents / `@cursor/sdk` cloud runtime)

### What they are

Cursor provisions an **ephemeral, agent-run-scoped VM**: it clones a **GitHub repo** at a `startingRef`, runs the agent (terminal, browser, desktop available *to the agent*), pushes a branch, optionally opens a PR, and archives. Environment is configured via `.cursor/environment.json` / saved snapshots. Resumable by `agentId`, manageable via API/SDK (`bc-` agents).

**Pricing:** there is no VM-hour price — Cloud Agents are charged at **API/model token pricing** drawn from your Cursor plan's credit pool (Pro $20, Pro+ $60, Ultra $200, Teams $40/seat; on-demand usage must be enabled). Compute is bundled into token billing.

### Fit against our requirements

| Requirement | Verdict |
|---|---|
| Persistent per-project state | ❌ VM lives for the agent run. Snapshots persist *environment setup* (packages), not project state/databases. |
| Long-lived user services | ❌ No SSH, no user-controlled services, nothing for a user's browser to hit. Cannot host Postgres/.NET/Vite/tunnel. |
| In-VM agent | ✅ That's the product — but it works via GitHub clone → push, not against a live working tree. |
| Control-plane API | ⚠️ Good API for *agents*, none for *machines*. |
| Cheap when idle | n/a — nothing persists to idle. |
| Branch-fork | ❌ Not applicable. |

**Cursor VMs are not runtime infrastructure and cannot replace Fly.** They solve a different problem: fire-and-forget PR-producing agents.

The only plausible role: switching the daemon's `@cursor/sdk` from `local:` to `cloud:` runtime to offload agent compute (letting runtimes shrink below 2 GB). But per our own `cursor-sdk` skill, cloud runtime **requires a GitHub repo URL, clones fresh, and can't see uncommitted changes** — it would break the core product loop (agent edits → hot reload → user sees it live through the tunnel) and replace it with commit/push/pull round-trips. Not worth it; we already pay Cursor token pricing either way, so cloud mode saves ~$5–8/mo of RAM per runtime while destroying the live-preview UX.

---

## Landscape context (for calibration)

Per-hour cost of a 2 vCPU / 4 GB sandbox, cheapest first:

| Provider | ~$/hr (2 vCPU/4 GB) | Notes |
|---|---|---|
| **Box** | **$0.018** | Full VM, storage/IPv4/desktop included, EU only |
| Fly Machines (current) | $0.031 (`shared-2x`/4GB) | + volume $0.15/GB/mo; suspended ≈ free |
| Northflank | $0.067 | PaaS-style, BYOC, GPUs |
| Fly Sprites | $0.315 (2 CPU + 4 GB) | Fly's new sandbox product; free when idle — interesting only for bursty exec, not persistent runtimes |
| E2B / Daytona | $0.166 | <500 ms cold start, code-exec focused |
| Modal | ~$0.19 | GPU support |
| Vercel / Cloudflare Sandbox | $0.2–0.6 effective | Active-CPU billing models, layered costs |

Our workload (long-running, stateful, mostly-idle VMs) is the opposite of what E2B-style micro-sandboxes price for — which is why only Box and Fly are genuinely in the running.

## Recommendation

1. **Cursor VMs: no.** Wrong shape — ephemeral, GitHub-clone-based, no hosting. Keep using `@cursor/sdk` local mode on our own runtimes.
2. **Box: genuinely interesting, not urgent.** At today's scale Fly costs ~$11–13/runtime-month and suspends to near-zero; switching saves little in absolute terms. Box wins if/when (a) fleet compute cost matters, (b) we want 2×–4× beefier runtimes at today's price, or (c) we lean on disk-fork for instant branch runtimes. The counterweight is betting the whole product on a young vendor with no SLA.
3. **Cheap next step:** a ~1-day spike — create a box via API, restore a runtime snapshot of our supervisord stack (Postgres + .NET + Vite + tunnel + daemon), measure stop→resume latency and fork time, and confirm TTL override + API completeness. That converts this from paper research into a real go/no-go, without touching `FlyManagement`.

## Sources

- [Box product page](https://box.ascii.dev/) · [Box compare page](https://box.ascii.dev/compare) · [Axentia review of Box](https://axentia.in/blog/box-a-cloud-sandbox-built-for-ai-agents) · [launch post](https://x.com/AniC_dev/status/2081101746051367307)
- [Cursor Cloud Agent docs](https://cursor.com/docs/cloud-agent) · [Cursor Models & Pricing](https://cursor.com/docs/models-and-pricing) · [Cursor pricing explained (Vantage)](https://www.vantage.sh/blog/cursor-pricing-explained)
- [Northflank AI sandbox pricing comparison](https://northflank.com/blog/ai-sandbox-pricing) · [Fly.io pricing](https://fly.io/docs/about/pricing/)
- Internal: `.claude/skills/runtime-environment/SKILL.md`, `.claude/skills/cursor-sdk/references/runtime-choice.md`, `CreateMachineRequest.cs`

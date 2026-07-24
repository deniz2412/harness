# Harness Platform — Option B Design Specification

**Bank-owned AI coding harness on Microsoft Agent Framework (.NET), running on Docker Desktop**
**Companion to:** AI-Harness-Analysis-and-Plan.md, Internal-Harness-Build-Analysis.md,
Harness-Product-Vision-Roadmap.md (long-term horizons beyond M4)
**Date:** 24 Jul 2026 · **Status:** v2.10 — local MVP scope; **M0–M3 ✅, F1 ✅, M5 ✅, M6 ✅, M7 ✅, M7b ✅
complete and verified** (exit checks in `docs/m0-exit-check.md` … `docs/f1-exit-check.md`, `docs/m5-exit-check.md`,
`docs/m6-exit-check.md`, `docs/m7-exit-check.md`, `docs/m7b-exit-check.md`; reviews in `REVIEW.md`). Highlights: M2's write path
demonstrated live (human-gated PR test-repo-harness#2); M3 per-run repos behind a fail-closed
allowlist; F1 a Blazor Server ops console; M5 the QA workflow pack; M6 the security pack
(dependency-audit found a real High CVE, secrets-sweep clean — both live; threat-model-draft's
gated-PR demo deferred on credits, having failed fail-closed with no PR leaked; `sast-triage`
descoped, 3 of 4); M7 team namespaces + an org `policy.yaml` floor validated at load time (team
override + floor validation demonstrated live without the gateway; full pr-review-completion
regression deferred on credits); M7b a named agent registry (`agent_ref` resolution, agent-pinned run
sha, team agent overrides — demonstrated live without the gateway). **M4 (graduation to real infra) is
deferred by the human;** next is **M7c (MCP connector layer)**. Copies of this doc live in the repo at
`harness/docs/` — keep both in sync.

---

## 0. Decisions this spec is built on

| Decision | Value |
|---|---|
| Path | **Option B** — greenfield, bank-owned platform on Microsoft Agent Framework 1.0 (.NET) |
| Sequencing | **Archon pilot runs in parallel**; findings feed this design |
| Workflows | **Multiple from the start** — declarative YAML definitions, one engine |
| Model access | **Existing Anthropic API key behind a local gateway**; provider swappable later |
| Capacity | **Solo / very small** — walking skeleton: one vertical slice through every layer, then breadth |
| Runtime (v2) | **Docker Desktop** (docker compose) — no OpenShift, no cluster deployment for the MVP |
| Scope ceiling (v2) | Workflow ends at **opening a PR** — nothing beyond PR creation; merge stays impossible by design |
| Tracking (v2) | **GitHub Projects/Issues** instead of Jira |
| State (v2.1) | **Postgres** (compose service) via EF Core — same store locally as at scale, no migration later |

**Why Postgres from the start:** running it as a compose container costs nothing extra locally and
means the state/audit store is identical in dev and at graduation — concurrent runs, resumable
state, and an independently queryable tamper-evident audit store from day one, with zero
migration step later. EF Core still keeps the provider swappable in principle.

---

## 1. System overview

```
                     Developer (local, CLI)          GitHub webhook*
                            │                        (*optional in MVP —
                            ▼                          polling/manual OK)
┌──────────────────────────────────────────────────────────────────┐
│        docker compose: HARNESS PLATFORM (.NET 8, MAF 1.0)        │
│                                                                  │
│  ┌────────────┐   ┌───────────────┐   ┌───────────────────────┐  │
│  │ API / CLI  │──▶│ Workflow      │──▶│ Agent Runner (MAF)    │  │
│  │ Minimal API│   │ Engine        │   │ AIAgent + tools,      │  │
│  │            │   │ (YAML DAG,    │   │ fresh/continued ctx   │  │
│  │            │   │  gates, loops)│   │ per node              │  │
│  └────────────┘   └──────┬────────┘   └──────────┬────────────┘  │
│                          │                       │               │
│  ┌────────────┐   ┌──────▼────────┐   ┌──────────▼────────────┐  │
│  │ Policy &   │◀──│ Gate          │   │ Tool Layer (MCP)      │  │
│  │ Guardrails │   │ Evaluator     │   │ github · repo ·       │  │
│  │ (allowlists│   │ (human/auto)  │   │ codesearch · bash*    │  │
│  │  PII/secret│   └───────────────┘   │ (*sandboxed container)│  │
│  │  scans)    │                       └───────────────────────┘  │
│  └────────────┘                                                  │
│  ┌────────────────────────────────────────────────────────────┐  │
│  │ Audit Emitter — hash-chained events → Postgres + vol files │  │
│  └────────────────────────────────────────────────────────────┘  │
└───────────┬───────────────┬────────────────┬─────────────────────┘
            ▼               ▼                ▼
     Model Gateway     Postgres          .env / user-secrets
     (LiteLLM-class    container         (Anthropic key; Vault
      → Anthropic now, (runs · events ·   comes with real infra)
      Bedrock/Foundry   approvals)
      later)
```

Compose services: `harness` (API + engine + agents), `gateway` (LiteLLM-class proxy), `postgres`,
and per-run **ephemeral runner containers** for write-capable nodes. Audit payloads on a named
volume. That's the whole footprint.

---

## 2. Component design

### 2.1 API & triggers
ASP.NET Core Minimal API. MVP entry points: `POST /runs` from the CLI (`harness run pr-review
--repo org/test --pr 42`) and an optional GitHub webhook (a `smee`-style tunnel or manual trigger is
fine locally — webhook plumbing is not a milestone gate). Runs are bound to the initiating
developer's GitHub identity; SSO/OIDC arrives with real infra, not before.

### 2.2 Workflow engine (the core we own)
Executes declarative YAML DAGs. Node kinds stay deliberately few: `agent`, `agent-loop`, `bash`,
`gate`.

```yaml
# workflows/pr-review.yaml
name: pr-review
description: Review a pull request against bank standards
permissions: { repo: read, github: comment }    # ceiling for every node
nodes:
  - id: gather
    kind: agent
    prompt_ref: prompts/pr-review/gather.md      # prompts versioned in git
    tools: [repo.read, github.pr_diff, codesearch.query]

  - id: review
    kind: agent
    depends_on: [gather]
    prompt_ref: prompts/pr-review/review.md
    tools: [repo.read]
    output_schema: schemas/review-findings.json  # structured output enforced

  - id: post
    kind: agent
    depends_on: [review]
    gate: auto             # low-risk write (comment) → auto-gate w/ policy scan
    tools: [github.pr_comment]
```

```yaml
# workflows/issue-to-pr.yaml (the write-path shape — ends at the PR)
permissions: { repo: write-worktree, github: open_pr+issues }
nodes:
  - id: plan
    kind: agent
    tools: [github.get_issue, repo.read, codesearch.query]
  - id: implement
    kind: agent-loop                  # iterate until validation passes
    depends_on: [plan]
    until: validation_pass
    max_iterations: 5
    fresh_context: true
    tools: [repo.read, repo.write_worktree, bash.sandboxed]
  - id: validate
    kind: bash                        # deterministic, no AI
    run: "dotnet test"
  - id: approve
    kind: gate
    gate: human                       # hard human gate before any push
    approvers: [initiator]
  - id: open-pr
    kind: agent
    depends_on: [approve]
    tools: [github.push_branch, github.open_pr]  # ← workflow ENDS here.
                                                 #    No merge op exists anywhere.
```

Engine semantics: topological execution; `agent-loop` re-invokes with fresh context per iteration
(bounded); failures halt in a resumable state. Workflows and prompts live in a protected branch —
**changing a workflow is a reviewed PR**, which is the MRM change-control story for free.

### 2.3 Agent runner (MAF)
Each `agent` node materializes a MAF `AIAgent`: the node's versioned prompt, only the tools it
lists (∩ workflow ceiling), and an `IChatClient` pointed at the **gateway** container
(OpenAI-compatible endpoint → Anthropic behind it). MAF middleware wraps every model and tool call
for OpenTelemetry tracing and policy interception. We build no model plumbing.

Isolation: write-capable nodes run in an **ephemeral runner container** (spawned via the Docker
API) with a cloned worktree, no host mounts beyond its workdir, egress limited to gateway + GitHub.
Container dies after the node; the branch/diff are the only survivors. This is the Docker Desktop
stand-in for what runner pods do on a cluster later.

### 2.4 Tool layer (MCP)
Narrow, typed contracts; every invocation → audit event. Jira is out; GitHub covers tracking via
Projects/Issues:

| Tool | Ops (MVP) | Notes |
|---|---|---|
| `repo` | `read_file`, `list`, `write_worktree` | write only inside the run's worktree |
| `github` | `pr_diff`, `pr_comment`, `push_branch`, `open_pr`, `get_issue`, `issue_comment`, `project_item_status` | **no merge op exists** |
| `codesearch` | `query` | ripgrep over the worktree; embeddings only if it provably fails |
| `bash` | `run` (sandboxed) | runner container only; command allowlist |

Credentials: a GitHub fine-grained PAT or GitHub App key in `.env`/user-secrets for the MVP
(gitignored); Vault when real infra arrives. Never in prompts or logs.

### 2.5 Policy & guardrails
Same pipeline, unchanged by descoping — this layer is the point of Option B:
**pre-model** — prompts assembled only from allowlisted sources; secret scan (gitleaks-style) on
everything model-bound; PII redaction at the gateway.
**pre-tool** — tool ∈ node list ∩ workflow ceiling ∩ repo allowlist; args schema-validated.
**pre-write** — any externally visible write (comment, push, PR) passes secret + policy scans;
`gate: human` blocks until the initiator approves via CLI/API.
Fail-closed: scanner unavailable ⇒ run pauses, never proceeds.

### 2.6 Audit (the crown jewel)
Append-only `run_events`, hash-chained, in **Postgres**; full payloads as files on the audit volume:

```json
{
  "event_id": "uuid", "run_id": "uuid", "seq": 41,
  "ts": "2026-07-22T14:03:11Z",
  "type": "tool_call | model_call | gate_decision | node_start | node_end | policy_block",
  "initiator": "deniz", "workflow": "pr-review@sha:abc123",
  "node": "review", "payload_hash": "sha256:...",
  "payload_ref": "file://audit/run/.../41.json",
  "tokens": {"in": 12034, "out": 1822}, "cost_usd": 0.31
}
```

Chain integrity (`seq` + payload hash) gives tamper evidence; events pin
the workflow **git SHA** and prompt versions ⇒ every run reproducible and attributable. SIEM
shipping is a later sink on the same emitter interface — the schema doesn't change when infra does.

### 2.7 Model gateway
LiteLLM-class proxy as a compose service: Anthropic key from `.env`, per-workflow budgets and rate
limits, redacted request/response logging, model-tier routing (cheap model for `gather`-type
nodes, strong model for `implement`/`review`). Later hosting moves (Bedrock/Foundry/on-prem) are
gateway config, zero platform code change. Adopted, not authored.

---

## 3. Repository & solution layout

```
harness/
├── src/
│   ├── Harness.Api/            # Minimal API, CLI endpoint, (optional) webhook
│   ├── Harness.Engine/         # workflow parser, DAG executor, gates
│   ├── Harness.Agents/         # MAF agent factory, middleware (tracing, policy)
│   ├── Harness.Tools/          # MCP tool implementations (github, repo, codesearch, bash)
│   ├── Harness.Policy/         # scanners, allowlists, gate evaluator
│   ├── Harness.Audit/          # event emitter, hash chain (SQLite + files)
│   └── Harness.Contracts/      # shared types, event & workflow schemas
├── workflows/                  # YAML definitions   (protected branch)
├── prompts/                    # versioned prompts  (protected branch)
├── docker/                     # compose.yaml, Dockerfiles (harness, runner), gateway config
└── tests/                      # engine unit tests + golden-run eval harness
```

Modular monolith; EF Core with the Npgsql provider — the same store locally and at graduation, so
there is no migration story at all. Interfaces between `Engine`/`Agents`/`Policy`/`Audit` are the future
split-points — same seams as v1, cheaper shell.

---

## 4. Local runtime (Docker Desktop)

`docker compose up` brings up:

- **`harness`** — the .NET service (API, engine, agents). Mounts: workflow/prompt repo (ro),
  SQLite + audit volume (rw).
- **`gateway`** — LiteLLM-class proxy with the Anthropic key; the only service with the key.
- **`postgres`** — state and audit events (official image, local volume).
- **runner containers** — created per write-node via the Docker socket (mounted to `harness`
  only), locked-down image (non-root, no extra mounts), TTL'd.
- **volumes** — `pg-data`, `audit-payloads`, `worktrees` (per-run clones).

Notes: the Docker socket mount is a local-dev convenience with real security weight — acceptable on
a developer workstation for the MVP, and exactly what runner pods replace on a cluster. OTel traces
go to console/file locally; the exporter is config.

---

## 5. Build roadmap (solo-shaped: skeleton first, then breadth)

**M0 — Walking skeleton (≈1–2 wks, faster than v1 — no cluster work).**
`docker compose up` → CLI triggers `pr-review.yaml` on the test repo → MAF agent via gateway →
PR comment posted → hash-chained audit events in Postgres.
*Exit: a real PR gets a real AI review comment, fully audited, from a laptop.*

**M1 — Governance hardening (≈2 wks). ✅ Complete (23 Jul 2026).**
Policy pipeline (secret/PII scans, allowlists), human gate mechanics, audit chain verification
command (`harness audit verify <run>`), budget limits at the gateway. **STRIDE threat model** run
against this design (project skill); fixes folded in.
*Exit: a compliance colleague can be walked through a run end-to-end and verify the chain.*
Delivered: data-driven secret ruleset + permission-ceiling enforcement; gate pause/approve/reject/
resume on the `gate:` attribute; workflow+prompt content-hash pinning; every tool call audited and
policed at one fail-closed seam; EF migrations; `harness-audit` CLI; tamper-evident hash chain
(metadata-bound + per-run head anchor, `Events` append-only at the DB); gateway budgets; threat
model (`docs/threat-model.md`). Verified on a cold rebuild — see `docs/m1-exit-check.md`, `REVIEW.md`.
PII redaction at the gateway and a least-privilege DB role are carried as tracked residuals (M2/graduation).

**M2 — Write path + multi-workflow (≈3 wks). ✅ Complete (23 Jul 2026).**
`agent-loop`/`bash`/`gate` node kinds; runner-container isolation; `test-generation.yaml` and
`issue-to-pr.yaml` live behind human gates, ending at PR creation. GitHub Issues/Projects tools.
Golden-run eval harness comparing outputs with the parallel **Archon pilot** on identical tasks.
*Exit: three workflows operational; writes gated; Archon-vs-platform bake-off data in hand.*
Delivered: the three new node kinds; a sandboxed subprocess runner behind `IRunnerFactory` with a
per-run worktree that survives the human gate (the ephemeral **container** is a documented drop-in —
runner isolation is the seam, not yet the container); write tools (`push_branch`/`open_pr`/
`write_worktree`/`issue_comment`) ending at open_pr, no merge; both workflows human-gated; the
golden-run eval harness. **Demonstrated live** — a human-gated `test-generation` run opened a real
PR (test-repo-harness#2), audited, chain intact. See `docs/m2-exit-check.md`, `REVIEW.md`.
*Spec deviation recorded:* the **Archon bake-off is descoped to eval-harness-ready** — the external
Archon pilot is not available in this environment, so the golden-run harness is built and tested but
no Archon-vs-platform comparison data exists yet; feed Archon outputs in when available. Runner
isolation ships as a subprocess sandbox, not a container (documented drop-in); egress control (F11)
and a least-privilege DB role (F4) remain graduation-time residuals.

**M3 — Multi-repo & search (≈1–2 wks). ✅ Complete (23 Jul 2026).**
Delivered: `GitHubToolsetFactory.ForRepo(run.Repo)` (per-run, closing the M2 split-source-of-truth);
a fail-closed config `RepoAllowlist` (exact or `owner/*`) enforced at `POST /runs` and resume; and
read-only `github.search_code`/`search_repos` confined to the allowlist (request qualifier + exact
post-filter, double-bounded, fail-closed on empty scope). No repo create/delete/fork; the catalog
guard now also rejects `fork`. Demonstrated live — allowlist refuses a non-allowlisted/malformed
repo, the allowlisted repo runs via the per-run factory. The PAT stays (GitHub App is the
"when more repos join" item); a second live repo is confirmatory, not required. See
`docs/m3-exit-check.md`, `REVIEW.md`.

**M3 — Multi-repo & search (original scope).**
Un-bind GitHub tooling from startup config: `GitHubToolset` becomes **per-run** (factory from
`run.Repo`), validated against a **repo allowlist** in config — the allowlist is a policy control,
not plumbing. Add read-only cross-repo search tools (`github.search_code`, `github.search_repos`).
Deliberately excluded, same philosophy as no-merge: **no repo creation or deletion tools** — repo
lifecycle stays human. Token graduates from single-repo PAT to a GitHub App with
selected-repository installation when more repos join.
*Exit: any workflow runs against any allowlisted repo; agents can search but never create repos.*

**F1 — Operations console. ✅ Complete (23 Jul 2026), pulled forward of M4.**
The "thin operations page" this M4 note anticipated shipped early (product-vision §5 approved the
pull-forward once M2's gates existed): a Blazor Server console hosted in `Harness.Api` — run list +
live status, run detail with the node/event timeline and payload viewer, audit-chain viewer with
verify, the gate approval screen, token/cost per run. A strict client of `IRunQueries`/
`IRunCoordinator`; the run-start/gate logic was extracted so the API and UI share one fail-closed
path. See `docs/f1-exit-check.md`.

**M4 — Decide & (maybe) graduate (later).**
With bake-off evidence: retire Archon and grow the platform, keep both, or lift to real infra —
OpenShift manifests, Vault, SIEM, SSO all slot into seams already present (this is v1 §4 of this
spec, deferred, not deleted). The operations UI already landed as F1 above, so M4 is now purely the
graduation/decision milestone — real infra, accounts and spend, a hard human checkpoint.
*Exit: evidence-based decision on scaling and hosting.*

Solo-capacity guardrails, unchanged: no web UI before M4 (CLI + PR comments are the UI); no visual builder;
no embeddings until ripgrep fails; gateway and scanners adopted, not authored.

---

## 5c. Upstream SDLC & cascade orchestration (P-series — engine contract)

Deferred until after M7/F3 (see `product-vision.md` §5c for the product narrative). Captured here as
the **implementation contract** so the work is well-defined when it starts. Extends the harness
upstream of code: analysis → stories → interactive spec → handoff to implementation, as a bounded,
audited cascade of runs. Three additions to the engine, all built on existing seams.

**(a) `T1` — inline line comments (small, independent, do anytime).**
New catalog tool `github.pr_review_comment(file, line|position, body)` on top of the PR review API,
optionally batched into a single `github.create_review(comments[])` submission. Draft-shaped,
gate-eligible, curated addition (one method + registry entry + gate rule). `review-findings.json`
already carries `file`/`line`, so `pr-review`'s `post` node maps its findings straight onto it.
No engine change — tool layer only.

**(b) `P0` — new engine primitives.**
- **Interactive refinement loop.** Extend the gate/loop model with a third human path beyond
  approve/reject: **revise**. Realized as a node mode `interactive: true` with `until: approved`,
  where each iteration presents the current artifact, accepts free-text human feedback, and
  re-runs the agent with that feedback appended — bounded by `max_iterations` and node budget.
  On approval, the artifact's content hash is pinned into the audit trail (same mechanism as
  workflow/prompt pinning). This is how `story-to-spec` converges.
- **`handoff` node kind.** Upon reaching it, an *approved* run may spawn N downstream runs
  (workflow + params per spawned run, produced by an upstream agent node as structured output
  validated against a schema). New `Run` fields: `cascade_root_id`, `parent_run_id`, `depth`.
  A spawned run is an ordinary run — no new persistence, no new trust boundary — that happens to
  have a lineage.

**(c) Cascade budget — the safety contract (enforced in `Harness.Engine`, configured in
`policy.yaml`).**
Caps carried on the cascade root and inherited by all descendants; a workflow may tighten but never
loosen the org ceiling. Checked at every `handoff` **before** spawning; exceeding any cap halts
fail-closed (invariant 2) and creates a human-continuation point.

| Cap | Meaning | Suggested default |
|---|---|---|
| `max_depth` | handoff hops allowed in the chain | 2 |
| `max_fanout` | downstream runs a single handoff may spawn | 5 |
| `max_total_runs` | hard cap on runs in the whole cascade tree | 25 |
| `cascade_budget_usd` | total spend across the tree; halt fail-closed when hit | small, tenant-set |
| `gate_policy` | per boundary: `auto` \| `human` \| `human_over(N)` | tenant-set |

Every spawn emits an audit event carrying `cascade_root_id`, `depth`, and remaining budget, so the
whole tree is one traceable lineage. Default posture is conservative (tight caps, raise within the
org ceiling); a bank tenant may pin `gate_policy: human` everywhere to reduce to the fully-gated
model, a PoC tenant may run fully automatic within budget.

**(d) `P1`–`P3` — the workflows/UI that ride on the above** (pure data + frontend, no further engine
work): `epic-to-stories`/`story-refinement` (BA/SA → gated issues via `github.create_issue`),
`story-to-spec` (interactive loop → docs-repo PR), and the flagship `story → spec → implementation`
handoff with a UI intake + pipeline view. Write ceilings unchanged: analysis drafts issues, spec
authoring drafts a docs PR, implementation ends at a PR — nothing files, assigns, or merges.

**Invariant addendum (applies once P-series lands):** *Cascades are finite, budgeted, audited
trees — never daemons.* Any handoff that cannot prove it is within the cascade budget must
fail-closed rather than spawn.

---

## 5d. Knowledge base & multi-repo workspaces (K1/W1 — engine contract)

Deferred (K1 pairs with M7; W1 needs M3 + P0). Product narrative in `product-vision.md` §5d.

**K1 — knowledge sets (trusted context).**
- `knowledge/<set>/*.md`, versioned + PR-reviewed. A `KnowledgeResolver` loads referenced sets and
  the agent runner injects them into context as a **clearly delimited trusted block**, separate
  from untrusted repo/issue content (invariant 4 stays intact — see new invariant below).
- Referenced via `knowledge: [set, ...]` on a workflow or agent definition; sets are scoped
  org/team/workspace under the M7 ownership model. Content hash pinned into the audit trail like
  prompts.
- Retrieval: ripgrep over the knowledge repo first; embeddings deferred; external corpora via M7c
  connectors (read-only). Guidance only — hard rules live in `policy.yaml`, not here.

**W1 — workspaces (multi-repo).**
- A `Workspace` = named set of allowlisted repos + a bound knowledge set. New: `run.WorkspaceId`
  as an alternative to `run.Repo`; `GitHubToolsetFactory` extends to serve any repo in the
  workspace allowlist (M3's `RepoAllowlist` generalizes to a repo-group allowlist); one worktree
  per repo per run.
- Write ceiling: **one PR per affected repo**, each independently human-gated. No cross-repo atomic
  operation; no merge. Per-repo permission ceilings still apply.
- Cross-repo change = a workspace run that emits a `handoff` (P0) fanning out to per-repo
  implementation runs, bounded by the cascade budget. The workspace knowledge set is what keeps the
  per-repo PRs consistent.

**New invariant (applies once K1 lands):** *Two context classes, never blurred.* Knowledge sets are
a trusted instruction source (they pass the review gate); repo/issue content is untrusted. The
agent runner must keep them in distinct, labeled context regions, and prompts must instruct the
agent to treat only the knowledge block as authoritative guidance.

---

## 6. Risks specific to this build

- **Local runtime ≠ target runtime** → keep the seams honest: EF Core provider, audit sink
  interface, runner abstraction, gateway indirection. Nothing in the code may assume "localhost".
- **Docker socket exposure** → confined to the `harness` service, dev-workstation only, replaced by
  Jobs/pods at graduation; noted in the STRIDE model.
- **Prompt injection via repo/issue content** → all repo and issue text is untrusted; tools are the
  only actuators, allowlisted per node; STRIDE at M1, red-team pass at M2.
- **Anthropic terms for source code** → confirm before M0 sends the test repo to the API (still the
  single blocking open item).
- **Secrets hygiene without Vault** → `.env`/user-secrets gitignored; secret scanner runs on the
  harness repo itself in CI; Vault at graduation.
- **MAF churn** → 1.0 GA, APIs stable; pin versions; MAF wrapped behind `Harness.Agents`.

---

## 7. Open items to start M0

1. **Anthropic data terms** — no sign-off needed for this personal PoC; the [Commercial Terms of
   Service](https://www.anthropic.com/legal/commercial-terms) already cover it (§B: no training on
   Customer Content, you own Inputs/Outputs). *Deferred note:* if/when this graduates to bank use,
   route the terms + [DPA](https://www.anthropic.com/legal/data-processing-addendum) +
   [trust.anthropic.com](https://trust.anthropic.com/) attestations through legal/vendor-risk, and
   switch to an org-owned key at the gateway.
2. **Test repo** — you're creating it; a fine-grained PAT or GitHub App with `contents:read`,
   `pull_requests:write`, `issues:read` scoped to it.
3. **Model choice** — which Claude model(s) the gateway routes to for cheap vs strong tiers.

That's the whole list now. With 1–2 in hand, M0 starts immediately.

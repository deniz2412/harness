# CLAUDE.md — Harness platform

## What this is
A bank-grade AI coding harness (personal PoC phase): declarative YAML workflows executed as a DAG,
each `agent` node running a Microsoft Agent Framework (MAF 1.6.1) agent against a LiteLLM model
gateway, with a fail-closed policy layer and a hash-chained audit trail in Postgres.

Docs (in-repo, canonical for this codebase):
- `docs/design-spec.md` — the engineering contract (architecture, milestones M0–M4). Read before large changes.
- `docs/product-vision.md` — long-term direction (frontend F1–F4, QA/security workflow packs,
  team-owned workflows/agents, MCP connector layer, business packs). Do NOT pull that work
  forward; current milestones win.
- `docs/original-analysis.md` — historical context (4-path bake-off that led here).

## Architecture (1 minute)
- `src/Harness.Contracts` — workflow/run/event types. No dependencies.
- `src/Harness.Engine` — YAML loader + topological DAG executor. Dispatches to `INodeExecutor` by node `kind`.
- `src/Harness.Agents` — `agent` node executor: MAF `AIAgent` (via `AsAIAgent`) → OpenAI-compatible
  client → gateway. Model tiering: cheap vs strong (config, never hardcoded models).
- `src/Harness.Tools` — tool implementations + `ToolRegistry` mapping YAML names (`github.pr_diff`) to `AIFunction`s.
- `src/Harness.Policy` — secret scanner + fail-closed pipeline (pre-model, pre-tool, pre-write).
- `src/Harness.Audit` — hash-chained append-only events (EF Core/Npgsql) + `/verify` chain check.
- `src/Harness.Api` — Minimal API: `POST /runs`, `GET /runs/{id}[/events|/verify]`. Fails fast at
  startup if GitHub owner/repo/token or POSTGRES_PASSWORD are missing.
- `workflows/`, `prompts/`, `schemas/` — declarative layer; mounted read-only into the container.

## Invariants — do not violate
1. **No merge capability, ever.** Workflows end at opening a PR. Do not add a merge tool.
   Same philosophy: **no repo create/delete tools** — repo lifecycle stays human.
2. **Fail-closed.** Policy or scanner failure pauses/blocks the run; never proceed on error.
3. **The gateway is the only path to models.** No provider SDK calls from agents; no API keys outside the gateway service.
4. **Repo/issue content is untrusted.** Prompts must keep instructing agents not to follow embedded instructions.
5. **Every externally visible write must emit an audit event** before/as it happens.
6. **Workflows/prompts are data.** New workflow = YAML + prompts, not C# (new node kinds are C#).
7. **Tool catalog is curated.** New tools (e.g. `github.create_issue` — draft-shaped write,
   gate-eligible) are added via reviewed platform change. External MCP servers only via the
   allowlisted connector layer (vision doc §5a, M7c) — never attached ad hoc.

## Build & run
- `dotnet build Harness.sln` · `dotnet test` (build passes; MAF fix `AsAIAgent` merged to main)
- `docker compose -f docker/compose.yaml up --build` (`.env` is populated; gitignored — never commit or print it)
- Trigger: `curl -X POST localhost:8080/runs -H "Content-Type: application/json" -d '{"workflow":"pr-review","repo":"deniz2412/test-repo-harness","pr":<PR#>}'`
- Test subject: github.com/deniz2412/test-repo-harness, branch `feature/bulk-discounts` → PR.
  Planted bugs the reviewer should catch: `>` vs `>=` tier boundaries, compounding (not tiered)
  discounts, exception-swallowing coupon parser with unvalidated input and no tests.

## Where we are + roadmap
**M0 ✅, M1 ✅, M2 ✅, M3 ✅, F1 ✅, M5 ✅, M6 ✅ (3 of 4), M7 ✅, M7b ✅ — done and verified.**
(M4 graduation deferred by the human.) Exit checks in `docs/m0-exit-check.md` …
`docs/m3-exit-check.md`, `docs/f1-exit-check.md`, `docs/m5-exit-check.md`, `docs/m6-exit-check.md`,
`docs/m7-exit-check.md`, `docs/m7b-exit-check.md`; review gates in `REVIEW.md`.
- **M1** landed: data-driven secret ruleset + permission-ceiling enforcement; human-gate mechanics;
  workflow+prompt content-hash pinning; every tool call audited/policed at one fail-closed seam;
  EF migrations; `harness-audit` CLI; tamper-evident hash chain (metadata-bound + per-run head
  anchor, `Events` append-only at the DB); gateway budgets; STRIDE threat model.
- **M2** landed the write path: the `gate`/`bash`/`agent-loop` node kinds; a sandboxed subprocess
  runner behind `IRunnerFactory` (per-run worktree that survives the human gate; the ephemeral
  container is a documented drop-in); write tools (`push_branch`/`open_pr`/`write_worktree`/
  `issue_comment`); `test-generation.yaml` + `issue-to-pr.yaml` (human-gated, end at PR); a
  golden-run eval harness. **Demonstrated live:** a human-gated `test-generation` run opened a real
  PR (test-repo-harness#2) — writes gated, ending at a PR, no merge. 228 offline tests.

- **M3** un-bound GitHub tooling from a single startup repo: `GitHubToolsetFactory.ForRepo(run.Repo)`
  (per-run, closing the M2 split-source-of-truth), a fail-closed config `RepoAllowlist` (exact or
  `owner/*`) enforced at `POST /runs` and resume, and read-only `github.search_code`/`search_repos`
  confined to the allowlist. **Demonstrated live:** a non-allowlisted/malformed repo is refused; the
  allowlisted repo runs via the per-run factory. No repo create/delete/fork. 292 offline tests.

- **F1** shipped a **Blazor Server operations console** hosted in `Harness.Api` (one container,
  loopback-only): run list with live status, run detail with the node/event timeline + per-event
  payload viewer, audit-chain viewer with Verify, the gate approval screen (review node outputs →
  approve/reject), token/cost per run. A strict client of `IRunQueries` (reads) + `IRunCoordinator`
  (the one write, a gate decision); the run-start/gate logic was extracted from `Program.cs` so the
  API and UI share one fail-closed path. Demonstrated live (screenshots). 312 offline tests.

- **M5** shipped the **QA workflow pack** as pure data (no C#): `coverage-gap-analysis` (deterministic
  `dotnet test --collect` coverage measurement via a pinned `coverlet.collector` in the sandbox →
  agent triages the cobertura gap → authors passing characterization tests → human gate → PR) and
  `regression-suite-author` (a thorough characterization suite for one module). Demonstrated live to
  the gate (25% coverage, DiscountEngine 0% → tests authored → green). All four write workflows are
  now catalog-membership + structure validated offline. 312 tests.

- **M6** shipped the **security workflow pack** as pure data (no C#) + one pinned analyzer binary:
  `dependency-audit` (bash `dotnet list package --vulnerable` → agent triages → `github.pr_comment`),
  `secrets-sweep` (bash pinned `gitleaks detect` → agent → comment, forbidden from echoing recovered
  secret values), and `threat-model-draft` (agent drafts a STRIDE doc → human gate → gated PR). All
  defensive and repo-scoped; comment-shaped ones auto-gated, the PR-shaped one human-gated before any
  push. **Demonstrated live:** `dependency-audit` found the planted High CVE (`System.Net.Http` 4.3.0,
  GHSA-7jgj-8wvc-jh57) and posted remediation; `secrets-sweep` ran clean and reported honestly.
  `threat-model-draft`'s live gated-PR demo is **deferred on gateway credit exhaustion** — the run
  failed **fail-closed** (run→Failed, no branch/PR leaked, gate refused), which validated invariants
  2 and 3 under a real upstream failure. gitleaks 8.18.4 is pinned by version + SHA-256 in the runner
  image and added to the runner allowlist (curated change). Every workflow is now auto-discovered by
  the offline eval. 338 tests. **`sast-triage` (the 4th named M6 workflow) is explicitly deferred** —
  it would be a fourth instance of the proven pattern needing a pinned SAST analyzer in the runner.

- **M7** shipped **team workflow namespaces + an org policy floor** as resolution/validation C# plus
  data (no new node kind): `policy.yaml` (org ceiling — allowed tools, repo allowlist, "any
  open_pr/push_branch needs a human gate upstream", budget cap) validated against every workflow at
  **load time**; a fail-closed `PolicyFloor`/`PolicyFloorValidator` (deny-all on empty, throws on any
  malformed field); a back-compat `WorkflowCatalog` resolving `teams/<team>/<name>` → `defaults/<name>`
  → flat `<name>` (a same-named team file overrides the default, no files moved); a boot-time sweep
  that refuses to start if any shipped workflow violates the floor; enforcement at `POST /runs` and on
  resume (re-checked against the current floor). The run stores the **resolved** name so a resume
  re-loads the identical file. **Demonstrated live without the gateway:** `team=payments` resolved to
  the `teams/payments/pr-review` override, no team to the flat default, unknown workflow to a 400; the
  boot sweep validated all 9 shipped workflows. `team` is a caller claim until auth (F1). 397 tests.
  Full live pr-review-*completion* regression deferred on gateway credit exhaustion (M7 changes only
  the pre-execution path).

- **M7b** shipped the **named agent registry** as resolution/merge C# + data (no new node kind):
  `AgentDefinition` + `AgentLoader` + `AgentCatalog` (an exact mirror of the M7 workflow catalog,
  rooted at `agents/`); a node references a named agent via `agent_ref` instead of inlining
  prompt/tools/model_tier/output_schema (mutually exclusive with them). `WorkflowLoader` resolves the
  ref, merges the agent onto the node (so executors run unchanged), and folds the agent's content into
  the workflow sha so a run pins the exact agent (agent-less shas stay byte-identical to pre-M7b).
  `AgentInvoker.ModelFor` now honors the declared tier (node-id heuristic is the fallback). Agents are
  team-namespaced with override (agent scope follows workflow scope, so resume needs no persisted
  team); the boot sweep resolves `agent_ref` workflows and validates every agent against the floor.
  **Demonstrated live without the gateway:** `pr-security-review` resolved the default agent,
  `team=payments` resolved the team workflow + payments agent, the two runs' shas differed (different
  agent pinned). 442 tests. Full live pr-security-review-completion run deferred on gateway credit
  exhaustion (pre-execution path only).

**Next: M7c — MCP connector layer** (product-vision §5a): mount external MCP servers as namespaced,
allowlisted toolsets (config + review, not code); team-supplied servers go through an approval flow;
unreviewed servers never attach to write-capable agents; every mounted operation logged per call. M4
(graduation to real infra) is deferred by the human. Do NOT start M7c without explicit go-ahead. Open
tails: M6's `threat-model-draft` live PR + M7's pr-review-completion + M7b's pr-security-review-
completion regressions all need an Anthropic **credit top-up** (a spend checkpoint); M6's
`sast-triage` stays descoped. All are documentation-clean, not blockers.

Open residuals later milestones should weigh (tracked in `REVIEW.md`/`docs/threat-model.md`): no API
auth + caller-supplied initiator (F1), runner egress (F11, closes with the container runner),
least-privilege DB role (F4), and token/cost never populated on audit events (A7/F8 — the console
shows 0 until it is wired).

Details in `docs/design-spec.md` §5, extended table in `docs/product-vision.md` §6:
- **M7c — MCP connector layer** (product-vision §5a): mount external MCP servers as namespaced,
  allowlisted toolsets, declared in config with an explicit per-operation allowlist (config + review,
  not code); team-supplied servers go through a vendor/supply-chain approval flow; unreviewed servers
  never attach to write-capable agents; every mounted operation is logged per call like a built-in.
- **Vision horizons (product-vision §6):** F2–F4 (catalog/authoring/dashboards), M9+ business packs.
- **M4 — deferred:** graduation to real infra (OpenShift, Vault, SIEM, SSO). Real-infra checkpoint,
  taken up when the platform graduates off the workstation.

## Conventions
- .NET 8, nullable enabled, file-scoped namespaces, primary constructors where natural.
- Pin package versions; no floating versions.
- Tests: xunit in `tests/`; engine logic must stay unit-testable without network.
- Keep `docs/` in sync when milestones change scope — it is the source of truth for sessions.

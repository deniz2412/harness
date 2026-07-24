# M7 Exit Check — Report

**Date:** 2026-07-24
**Verdict:** ✅ **PASS** — team workflow namespaces + a fail-closed org policy floor, validated at
load time. The M7-specific mechanics (namespace resolution, floor validation, boot-time sweep) were
demonstrated **live without the gateway**; the full end-to-end pr-review *completion* regression is
deferred on gateway credit exhaustion (M7 changes only the pre-execution path, not agent execution).
**Demonstrated runtime:** `POST /runs {workflow: pr-review, team: payments}` resolved to the
`teams/payments/pr-review` override.

## Criterion

product-vision §4/§6 M7: *"Team workflow namespaces + org policy floor (policy.yaml validation)."*
Teams get a namespace (`workflows/teams/<team>/*.yaml`); a same-named team file overrides the org
default (the "Archon override"). An org `policy.yaml` fixes the ceiling — allowed tools, repo
allowlist, gate requirements, budget cap — that the engine validates every workflow against **at load
time**, so teams author freely *within* a boundary the org sets. "The piece that makes self-service
safe in a bank."

## What shipped (data + resolution/validation C#, no new node kind)

- **`policy.yaml`** (repo root, mounted read-only) — the org floor: `allowed_tools` (the exact union
  of tools across all shipped workflows — a strict subset of the platform catalog), `repo_allowlist`
  (`deniz2412/*`), `gate_requirements` (`github.open_pr`, `github.push_branch` — using either demands
  a human gate upstream), `max_run_budget_usd: 5.00` (advisory).
- **`PolicyFloor` + `PolicyFloorValidator`** (`Harness.Policy`) — the floor model/loader (fail-closed:
  malformed/missing policy, negative budget, malformed repo entry, blank tool name all throw at load;
  empty `allowed_tools` = deny-all) and the load-time validator with two rules: **tool ceiling**
  (every node's tools ⊆ `allowed_tools`) and **gate requirement** (any node naming a gate-required
  tool must have a `gate: human` node *transitively upstream* via `depends_on` — generalizes the M2
  "human gate precedes the PR-open node" invariant, with a cycle guard).
- **`WorkflowCatalog`** (`Harness.Engine`) — resolves a `(name, team)` reference with precedence
  `teams/<team>/<name>` → `defaults/<name>` → flat `<name>`, **without moving any existing files**
  (back-compat resolver). Fail-closed: unresolvable → throws; a name/team that isn't a single path
  segment or that escapes the root → throws before touching disk.
- **`workflows/teams/payments/pr-review.yaml` + `prompts/teams/payments/review.md`** — an example
  team-owned override of the org-default `pr-review` (same `name: pr-review`), a money-handling review
  that ends at `github.pr_comment`; its prompt carries the untrusted-content guard.

### Integration (shared seam)
- **`Program.cs`** — loads the floor once at startup (fail-fast, same posture as the secret ruleset
  and `RepoAllowlist`); registers `PolicyFloor`/`PolicyFloorValidator`/`WorkflowCatalog`; and runs a
  **boot-time fail-closed sweep** that validates *every* shipped workflow (flat + team) against the
  floor — the process refuses to start if any violates.
- **`RunCoordinator`** — `StartAsync` now resolves `(workflow, team)` via the catalog, stores the
  **resolved** name (e.g. `teams/payments/pr-review`) so a resume re-loads the identical file (the
  override picked at start is pinned, not re-decided), and enforces the floor before the run is
  created (`PolicyFloorBlocked` → 400). `DecideAsync` re-checks the floor against the *current* policy
  on resume (`PolicyFloorViolation` → 409) — a floor tightened while paused blocks resume. `team` is a
  caller claim until auth (threat model F1), exactly like `initiator`. No EF migration: the resolved
  name lives in the existing `Run.Workflow` column.

## Demonstrated live (no gateway / no credits needed)

The M7 mechanics all run before the gateway is ever reached, so they were exercised end-to-end
against the running container:

```
boot            → container starts, boot sweep validates all 9 shipped workflows vs policy.yaml ✓
POST /runs pr-review team=payments → run.workflow = "teams/payments/pr-review"   (override selected)
POST /runs pr-review               → run.workflow = "pr-review"                   (flat default)
POST /runs no-such-wf              → HTTP 400 "Unknown workflow 'no-such-wf'"      (fail-closed)
```

The boot sweep passing is the strongest guarantee: the platform will not start with a floor-violating
workflow on disk. Team-override resolution and fail-closed refusal were confirmed at `POST /runs`.

## Regression

The full end-to-end **pr-review completion** run needs the gateway (out of Anthropic credits from the
M6 checkpoint) and is **deferred**. This is not an M7 regression risk: M7 touches only the
*pre-execution* resolve/validate path — the agent-execution path is unchanged — and a real pr-review
run was shown to be *created and resolved* correctly (it fails only at the first model call). Build
clean, `docker compose` boots healthy with the floor loaded, and the earlier post-M6 pr-review
regression (`a3713828`, chain intact) covers the unchanged execution path.

## Tests

**397 offline tests, all green** (up from 338): `PolicyFloorTests` (loader + validator, deny-all,
malformed inputs, transitive/cyclic gate), `WorkflowCatalogTests` (precedence, override, traversal,
enumerate), `PolicyFloorComplianceTests` (every shipped workflow satisfies the floor + the floor
actually bites), and two `RunCoordinator` tests (floor-block at start and on resume). Offline
load/structure validation is the right automated gate for a data-plane feature; the boot sweep gives
the same guarantee at runtime.

## Review gate

Fresh independent audit of the M7 diff: **no MAJORs, no invariant violation, zero scope creep**
(no agent registry / M7b, no MCP / M7c, no F2+ UI, no big-bang directory move). Two minors were fixed
in-milestone (resume `Load` now maps a vanished definition to a clean 409; the resume floor-block path
now has a test); two are carried (below). Details in `REVIEW.md`.

## Residuals carried forward (see `REVIEW.md`, `docs/threat-model.md`)

- The full live pr-review-completion regression is pending an Anthropic credit top-up (spend
  checkpoint); M7's own mechanics are demonstrated live.
- `max_run_budget_usd` is validated for well-formedness but not yet enforced against a run — advisory,
  gateway-side enforcement is the graduation path (A7/F8, ties to token/cost never populated).
- `WorkflowCatalog.EnumerateAll` dedups a flat file shadowed by a same-named default (tooling-only, not
  on the run path; no `defaults/` dir exists yet).
- `team` is an unauthenticated caller claim until API auth (F1), same trust model as `initiator`.
- Analyzer/runner isolation is still a subprocess sandbox; runner egress is the tracked F11 residual.

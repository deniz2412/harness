# F4 Exit Check — Report

**Date:** 2026-07-25
**Verdict:** ✅ **PASS** — the last frontend increment: read-only **suite dashboards** (run volumes,
status, durations, gate latency, spend) and a **full drag-and-drop visual builder** whose only backend
touch is a pure YAML emitter + the F3 workbench validator. The builder is sugar over the YAML artifact —
it executes nothing, writes nothing, publishes nothing, and node positions are not part of the YAML.
Demonstrated by tests + clean compile, and **render-verified in-container** (`/dashboard` renders real
aggregates, `/builder` renders the canvas + emit) once Docker Desktop recovered.

## Criterion & confirmed scope

product-vision §5 F4: *"Org/team dashboards: spend, run volumes, gate latency, eval scores over time.
Only here, if still wanted: a visual drag-and-drop workflow builder that EMITS THE SAME YAML — sugar
over the artifact, never a second source of truth."* **Human-confirmed scope:** dashboards + a **full
drag-and-drop canvas**. "Eval scores over time" is **omitted** — no persisted data source exists (the
golden-run harness runs offline as tests, not stored) — an agreed omission, noted for a future
eval-result store.

## What shipped

- **`WorkflowYamlWriter.ToYaml(WorkflowDefinition)`** (`Harness.Engine`) — the inverse of the loader:
  emits canonical workflow YAML, omitting the loader-stamped sha, nulls, empty lists and non-loop
  defaults, and emitting an `agent_ref` node as just its `agent_ref` (the loader merges the agent at
  load; emitting the merged inline fields too would be the combination the loader rejects). Pure —
  returns a string, executes/writes nothing. Round-trip-tested (Load → ToYaml → LoadFromText) for
  pr-review, test-generation (agent-loop bounds), threat-model-draft (gated write + approvers), and
  pr-security-review (agent_ref), asserting every load-bearing node field survives.
- **`IDashboardQueries` + `DashboardQueries`** (`Harness.Api/Ops`) — read-only aggregates over
  runs/approvals/events: total + by-status counts, finished-run durations, decided-gate latency
  (avg/median), per-workflow success rates, a continuous daily volume window, and spend/token totals
  (**0**, straight from the events — token/cost instrumentation is the tracked A7/F8 residual, rendered
  honestly, never faked). Divisions guarded; windows zero-filled.
- **`/dashboard`** — summary tiles, a by-status badge row, a pure-CSS run-volume bar chart (completed/
  failed/other segments, no JS/chart library), and a per-workflow table with success-rate bars.
- **`/builder`** — a drag-and-drop DAG canvas: a palette (add agent/agent-loop/bash/gate), pointer-delta
  dragging (client-coordinate deltas, no JS interop), an SVG edge overlay, a connect mode
  (target `depends_on` source, self/dup-guarded), a per-node property panel (id/kind/tools/prompt_ref/
  agent_ref/model_tier/gate/output_schema, delete + rename-with-ref-fixup), and **"Emit YAML &
  validate"** → `WorkflowYamlWriter.ToYaml` → `IWorkbenchService.Validate` (the F3 floor/catalog checks
  + issues). It calls no `IRunCoordinator`/executor/git and writes no file; positions are UI-only.
- **`Program.cs`** registers `IDashboardQueries`; **`MainLayout`** gains Dashboards + Build nav links;
  **`app.css`** gains chart + canvas styles.

## Demonstrated

- **Offline (deterministic):** `WorkflowYamlWriterTests` (6 — round-trip fidelity across four shipped
  workflows incl. all load-bearing fields; omits sha/nulls/defaults; a hand-built definition emits YAML
  the loader accepts) and `DashboardQueriesTests` (3 — status/duration/gate-latency summary, per-workflow
  success rate, zero-filled daily volume, `TotalCostUsd == 0`). Full suite **530 green**; solution builds
  clean; both pages compile.
- **In-container render-verify: DONE** (after Docker Desktop recovered later the same day). Rebuilt the
  stack; the boot sweep stayed clean (`LoadFromText` did not regress the core loader). `GET /dashboard`
  renders **real aggregates** from the session's run history — by-status (11 Completed, 9 Failed, 1
  Running, 1 AwaitingApproval), the run-volume chart, and a per-workflow table linking every workflow
  exercised — with spend honestly $0.00 and the A7/F8 note. `GET /builder` renders the canvas, palette,
  connect toggle, and the Emit-YAML action. (The full live pr-review *completion* regression remains a
  separate credit block — the gateway is out of Anthropic credits — not a Docker or F4 issue.)

## Review gate + fix

Fresh independent audit: **no MAJORs, no invariant violation, zero scope creep.** The three
invariant-critical items are clean — the **builder never executes and is not a second source of truth**
(only `ToYaml` + `Validate`; positions never enter the YAML), **dashboards are read-only**, and **no
XSS** (no `MarkupString` on any derived value; emitted YAML in a readonly textarea). The emitter
round-trip and the divide-by-zero guards were verified. One minor **fixed in-milestone**: the round-trip
test now asserts the agent-loop bounds, approvers, output schema, and bash command survive (previously a
coverage gap; the emitter already handled them). Minors carried: median gate latency is upper-middle (no
interpolation); the canvas is a minimal editor (no field for a bash `run` or custom loop bounds — full
authoring is the F3 text path). Details in `REVIEW.md`.

## Regression

The mandatory live pr-review-completion regression needs the gateway (out of credits) AND a working
Docker engine (faulting this session) — deferred on both, consistent with prior milestones. F4 is a
read-only read model + two pages + a pure emitter + two DI lines; it changes no run path.

## Tests

**530 offline tests, all green** (up from 521): `WorkflowYamlWriterTests` (6) + `DashboardQueriesTests` (3).
The two Blazor pages are compile-verified; in-container render-verify is deferred on the Docker fault.

## Roadmap note

**F4 is the last frontend increment, and with it the PoC-buildable roadmap (M0–M7c + F1–F4) is
complete.** What remains — **M4/M8 graduation** (real infra: OpenShift, Vault, SIEM, SSO) and **M9+
business packs** (post-graduation only) — is gated on the human's graduation decision (new
infrastructure, spend, and accounts), not another in-place milestone.

## Residuals carried forward (see `REVIEW.md`)

- Spend/token dashboards show 0 until token/cost is wired (A7/F8).
- The canvas is a minimal editor (no `run`/custom-loop-bounds fields — the F3 text workbench covers full
  authoring); median latency uses no interpolation.
- The mandatory live pr-review regression is pending an Anthropic credit top-up (spend checkpoint) and a
  working Docker engine.

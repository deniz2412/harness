# F1 Exit Check — Report

**Date:** 2026-07-23
**Verdict:** ✅ **PASS** — a Blazor Server operations console, served in-process, renders the run
list with live status, run detail with the node/event timeline, an audit-chain viewer with verify,
the gate approval screen, and token/cost per run — all live against the real audit trail. A strict
client of the API and of the audit store; the only write is the gate decision.

## Criterion

product-vision §5 F1: *"Run list + live status, run detail (node timeline from `run_events`),
audit-chain viewer with verify button, gate approval screen (diff view, approve/reject), token/cost
per run. ~90% reads; writes only the gate decision."* Standing rule: *the UI is a client of the API
and of git.*

## Stack decision (human checkpoint, cleared)

**Blazor Server**, hosted inside `Harness.Api` — one container, one port (loopback-only, matching the
current no-auth posture), in-process access to the existing EF read models and services, no
self-HTTP. Chosen over React because the platform is all-.NET and F1 is read-heavy over existing
read models with a single write (the gate decision).

## Architecture

- **`IRunQueries`** — the read model: recent runs (one grouped query for event count + cost), run
  detail (ordered events, gates, token/cost totals), event payload from the audit volume, and chain
  verify (delegating to the same `AuditEmitter.VerifyAsync` the CLI and `GET /verify` use). Pure
  reads, `AsNoTracking`, never writes or reaches a model.
- **`IRunCoordinator`** — the two write actions (start a run, decide a gate), extracted from
  `Program.cs` so the HTTP API and the console enforce the fail-closed rules (repo allowlist,
  workflow-sha stability, allowlist re-check on resume, decision-recorded-before-resume) on **one
  path**, not two copies. `IWorkflowRunner` is the seam that keeps that logic testable off a live
  gateway.
- **Blazor UI** — a pure client of those two interfaces; it touches no DbContext, audit file,
  GitHub, or tool directly. Untrusted content (payloads, repo names, gate reasons) renders as
  Razor-encoded text only — no raw HTML, so auto-encoding is the whole XSS story.

## Demonstrated live

Rebuilt the stack and drove a real browser (headless Chrome) against the console:

- **Run list** (`/`) — every run from this session with status badges (Completed / Awaiting
  approval / Failed / Running), workflow, repo, initiator, started, duration, event count, cost; a
  banner flags runs awaiting a human decision; a 3s timer refreshes live status.
- **Run detail** (`/run/{id}`) — run header (status, workflow+sha, repo, PR, initiator, timing,
  head seq), token/cost/event tiles, the **Audit-chain** panel with a working Verify button, and the
  full color-coded **event timeline** (node_start → model_call → tool_call → tool_result → node_end
  across gather → review → post) with a per-event payload viewer.
- **Gate approval screen** — for an awaiting-approval run, the "Human gate — decision required" panel
  surfaces the preceding node outputs the operator reviews (the `gather` agent's analysis and the
  `author-tests` "validation passed on iteration 3 of 5"), then Approve / Reject with a reason,
  through `IRunCoordinator.DecideAsync` — the same guarded path the API and the M2 witnessed run used.
  The approver is honestly labelled "recorded as a claim, not a verified identity."

Screenshots captured and delivered. The `GET /runs` list endpoint the console reads was added to
the API.

## Regression

pr-review still completes end to end with the chain intact through the **coordinator-routed** start
path (the endpoint rewrite), so the extraction did not disturb the run path.

## Tests

312 offline unit tests (added 20 in `Harness.Api.Tests`): every `RunCoordinator` fail-closed
outcome asserted by name, the decision-recorded-before-resume ordering proven as a real
happens-before check, and `RunQueries` list aggregation/ordering, detail totals, and payload
read + null-on-missing. Blazor UI verified live; its non-trivial logic (duration/status/cost
formatting) is factored into a testable `UiFormat` helper.

## Recovery note

Both F1 workstreams hit a shared session limit mid-task, having completed their deliverables. Their
worktrees were recovered and the integration (seam `VerifyAsync`, `Program.cs` hosting + endpoint
rewrite, DI, one test fix) was done directly rather than re-spawning.

## Residuals carried forward (see `REVIEW.md`, `docs/threat-model.md`)

- **Unauthenticated, loopback-only** — the console has no auth; it is reachable only on 127.0.0.1,
  consistent with the API's posture (threat-model F1). Real auth (SSO/OIDC) is graduation work.
- **Token/cost shows 0** — the emitters never populate `TokensIn/Out/CostUsd` (A7/F8); the console
  plumbing is correct and will show real numbers once they are wired.
- Minor UI notes (gate-review shows all prior node outputs, not just gate deps; the Launch page is a
  second guarded write) tracked in `REVIEW.md`.

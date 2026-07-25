# F2 Exit Check — Report

**Date:** 2026-07-25
**Verdict:** ✅ **PASS** — a workflow catalog & launcher on the F1 console: browse every workflow
(flat + team-namespaced, parsed from YAML), open a workflow's detail with its DAG (topological layers)
+ permissions + gates + agent/connector tools, launch it (repo/PR/issue/**team** params), and see
per-workflow run history + success rates. Read-only over the same query seam as F1; the only write is
the existing launch/gate path. Demonstrated live in-container (the pages render; no gateway needed).

## Criterion

product-vision §5 F2: *"Browse catalog and team workflows (parsed from YAML), workflow detail (DAG
rendered from depends_on, permissions, gates), launch form (repo/PR/issue params), run history +
success rates per workflow."* Standing rule: the UI is a client of the API + git — it can render
nothing the query model / audit trail can't account for.

## What shipped

- **`IWorkflowCatalogQueries` + `WorkflowCatalogQueries`** (`Harness.Api/Ops`, the F1 read-model
  pattern) — a pure read model: enumerates workflows via `WorkflowCatalog`, loads each through the
  **production `WorkflowLoader`** (so `agent_ref`s are merged and shas stamped exactly as a run sees),
  and reads run outcomes from the DB. Three views: `WorkflowSummary` (list), `WorkflowDetailView`
  (detail with `WorkflowNodeView` incl. a topological **Layer** per node), `WorkflowStat`
  (per-workflow totals + success rate). Fail-soft: a single unloadable workflow is omitted, never
  crashing the catalog; `GetWorkflow` is bounded to catalog members (an off-catalog/traversal name
  returns null, not a load).
- **`Catalog.razor`** (`/catalog`) — workflow cards grouped by scope, each with team/scope + `EndsAt`
  (→ PR / → comment / read-only) + human-gate badges and, when runs exist, total runs + success rate +
  last-run time. Cards link to the detail page.
- **`WorkflowDetail.razor`** (`/workflow/{*LoaderName}`, catch-all so team names with slashes route) —
  the **DAG as pure-CSS columns** (one column per topological layer; dependencies rendered textually as
  "← after: …", no JS/library), each node showing kind, gate (human distinct from auto), tools (or "via
  agent: X" with the merged tools), model tier, output schema; plus permissions, the pinned sha, and a
  Launch button.
- **`Launch.razor`** upgraded — prefilled from `?workflow=&team=`, a **team** field, `team` passed to
  `IRunCoordinator.StartAsync`, and the **`PolicyFloorBlocked`** outcome handled (F1's launch predated
  M7 and was missing it). The launch link passes the **bare name + team** (the M7 model), not the
  resolved loader name.
- **`Program.cs`** registers the read model; **`MainLayout`** gains a Catalog nav link; **`app.css`**
  gains catalog-card + DAG-column styles in the existing dark theme.

## Demonstrated live (in-container, credit-free — F2 is reads + the existing launch write)

```
GET /catalog                              → "Workflow catalog" + cards for pr-review, pr-security-review,
                                            dependency-audit, test-generation, … (with success-rate stats)
GET /workflow/pr-review                   → DAG columns: gather (L0) → review (L1) → post (L2) + Launch button
GET /workflow/teams/payments/pr-review    → HTTP 200 (catch-all route; team override detail)
Launch link on a team workflow            → /launch?workflow=pr-review&team=payments  (bare name + team)
Launch link on a flat workflow            → /launch?workflow=pr-review
```

## Review gate + the MAJOR it caught

Fresh independent audit confirmed the two invariant-critical items are clean — **read-only discipline**
(the read model does no writes/model/network; the only console write remains `IRunCoordinator`) and
**no XSS** (no `MarkupString` on any workflow-derived text; Blazor auto-encodes). It found one **MAJOR,
fixed in-milestone**:

- **F2-maj-1 — the catalog→launch path was broken for team/namespaced workflows.** The launch link
  passed the resolved loader name (`teams/payments/pr-review`) into the `workflow` field, but
  `StartAsync`'s `ResolveName` rejects a slashed name → `BadWorkflow`. Fail-closed (no invariant
  breach, no bad write) but the headline "browse a team workflow → launch it" path did not work.
  **Fixed**: the launch link now passes the **bare name + team** (`?workflow=pr-review&team=payments`),
  which `StartAsync` resolves to the team override; `Launch.razor` prefills both from the query.
  Verified live (the rendered hrefs above).

Minors: the `%2F`-encoded catch-all route was found to work (HTTP 200 — MINOR moot); the catalog→launch
round-trip is verified live but not unit-tested (Blazor page interaction, consistent with F1's
render-verify approach) — carried in `REVIEW.md`.

## Regression

The mandatory live pr-review-completion regression needs the gateway (out of credits) and is deferred,
consistent with the M6/M7* milestones. F2 adds a read model + UI + one DI line; it changes no run
path (the launch upgrade only adds the `team` arg — already supported — and the `PolicyFloorBlocked`
message). Build clean; `docker compose` boots healthy; the pages render.

## Tests

**511 offline tests, all green** (up from 505): `WorkflowCatalogQueriesTests` (6 — list incl. a team
workflow, detail + correct DAG layers, an `agent_ref` workflow's merged tools, and stats/success-rate
grouping). The Blazor pages are compile-verified + render-verified in-container (the F1 precedent — the
UI is a thin client, not unit-tested).

## Residuals carried forward (see `REVIEW.md`)

- The catalog→launch round-trip is render-verified, not unit-tested (Blazor interaction).
- Per-workflow stats read the runs table directly; large histories aren't paginated (fine at PoC
  volume).
- The mandatory live pr-review regression is pending an Anthropic credit top-up (spend checkpoint).

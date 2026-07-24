# Review log

Findings from the milestone review gates, and how they were dispositioned. One entry per
milestone; newest first.

---

## M7b — Named agent registry (2026-07-24)

Independent review gate audited the full M7b diff `ec79d26..HEAD` (3 new engine files + 3 test files +
2 agents + 2 prompts + 2 workflows; 8 modified shared files). Verdict: **no MAJORs, no correctness bug,
no invariant violation, zero scope creep** (no MCP/M7c, no standing agents, no UI). All seven
invariants hold: agents are data (YAML+MD), no new node kind; `model_tier` names a gateway group only;
both agent prompts carry the untrusted-content guard; agents name only floored, catalogued read tools;
the registry adds no tool and no write path. Fail-closed throughout — a bad model tier, a
missing/escaping prompt, an unresolved `agent_ref`, a workflow that references agents with no registry
configured, or `agent_ref` set together with inline prompt/tools/tier/schema all throw at load; the
boot sweep resolves every `agent_ref` workflow and validates every agent against the floor, refusing to
start on any violation. The reviewer specifically confirmed the **sha-fold is clean**: an agent's
prompt that is also a node prompt collapses to one tag-keyed entry (no double-count), and an
agent-less workflow's sha is **byte-identical to pre-M7b** (no pin regression). A referenced agent is
pinned in the run sha (different agent ⇒ different sha), and a team workflow's `agent_ref` resolves the
team's agent override, deterministically on resume from the stored workflow name. Demonstrated live
without the gateway: `pr-security-review` resolved the default agent, `team=payments` resolved the team
workflow + payments agent, and the two runs' shas differed.

### MINOR — fixed

- **M7b-min-1 — `output_schema` was outside the agent_ref/inline mutual-exclusion.** A node could set
  `agent_ref` and still override the agent's `output_schema` (merge used `??=`), contradicting the doc
  that says the agent fully defines the node. **Fixed**: `output_schema` is now part of the
  mutual-exclusion check and the merge is unconditional — `agent_ref` fully owns the node's agent
  config (prompt, tools, tier, schema).

### MINORS — carried as tracked residuals

- **M7b-min-2 — team agent override fires only through a team-namespaced workflow.** Agent scope
  follows workflow scope: a `team=X` run of a workflow that has no `teams/X/` override resolves the
  flat workflow (stored without a team prefix), so `TeamOf` returns null and the org-default agent is
  used. This is deliberate — it makes resume deterministic with no persisted team field — but it is a
  non-obvious consequence, now documented in the exit check.
- **M7b-min-3 — the `agents/defaults/` layer is coded and tested but unused.** `AgentCatalog` fully
  supports the Default scope (mirroring `WorkflowCatalog`); only flat + team agents ship. Dead-but-
  consistent, not a defect — it activates when an org adopts the `defaults/` split.
- **Live pr-security-review-completion run deferred** on gateway credit exhaustion (spend checkpoint);
  M7b's registry mechanics are demonstrated live.

---

## M7 — Team namespaces + org policy floor (2026-07-24)

Independent review gate audited the full M7 diff `a9a08c1..HEAD` (3 new engine files + 3 test files +
policy.yaml + an example team workflow/prompt; 6 modified shared files). Verdict: **no MAJORs, no
invariant violation, zero scope creep.** All seven invariants hold: M7 adds only resolution
(`WorkflowCatalog`) and validation (`PolicyFloor`/`PolicyFloorValidator`) C# plus YAML/MD data — no new
node kind, no new tool, the floor only *restricts* (its `allowed_tools` is a strict subset of the
platform catalog and cannot grant a capability). Fail-closed throughout: a malformed/missing
`policy.yaml`, negative budget, malformed repo entry, or blank tool name throws at load and stops
startup; empty `allowed_tools` is deny-all; unresolvable/traversal workflow names throw before
touching disk; a **boot-time sweep** validates every shipped workflow against the floor and refuses to
start on any violation; the floor is enforced at `POST /runs` and re-checked against the *current*
floor on resume. The team-override mechanic is correct — the resolved name is pinned as `run.Workflow`
so a resume re-loads the identical file, never falling back to the default. Demonstrated live without
the gateway: `team=payments` resolved to the `teams/payments/pr-review` override, no team to the flat
default, an unknown workflow to a fail-closed 400.

### MINORS — fixed

- **M7-min-1 — `DecideAsync` loaded the paused run's workflow unwrapped.** If a team removed its
  namespaced override file while a run was paused, resume threw an unhandled exception (HTTP 500)
  rather than a clean refusal. (Pre-existing pattern that M7 slightly widened.) **Fixed**: the resume
  load now maps a vanished/invalid definition to `DefinitionChanged` (409) — fail-closed, no resume,
  nothing recorded.
- **M7-min-2 — the resume floor-block path had no test.** `DecideAsync`'s new `PolicyFloorViolation`
  branch was uncovered. **Fixed**: added `DecideAsync_returns_PolicyFloorViolation_when_the_current_
  floor_rejects_on_resume` (a paused run whose workflow the current floor now denies is refused).

### MINORS — carried as tracked residuals

- **M7-min-3 — `max_run_budget_usd` is inert.** The floor validates it for well-formedness (negative
  rejected) but nothing enforces it against a run — `WorkflowDefinition` has no budget dimension and
  the validator checks only tool-ceiling + gate. Matches the documented "advisory, gateway-side
  enforcement" stance; ties to the A7/F8 token/cost residual.
- **M7-min-4 — `WorkflowCatalog.EnumerateAll` dedups a flat file shadowed by a same-named default.**
  A default and a flat of the same name share the `(name, team=null)` key, so only one surfaces —
  unlike the team-vs-default override pair, which correctly surfaces both. `EnumerateAll` is
  tooling-only (not on the run path) and no `defaults/` dir exists yet, so this is inert; the doc
  comment slightly overstates that override pairs always both appear.
- **Live pr-review-completion regression deferred** on gateway credit exhaustion (spend checkpoint).
  M7 changes only the pre-execution path; a real pr-review run was shown created + resolved correctly.
- **`team` is an unauthenticated caller claim** until API auth (F1), same trust model as `initiator`.

---

## M6 — Security workflow pack (2026-07-23)

Independent review gate audited the full M6 diff `c522554..HEAD` (3 workflows + 6 prompts + the
runner Dockerfile/allowlist + 1 test file; +405/−71). Verdict: **no MAJORs, no invariant violation,
zero scope creep into M7/vision territory.** All seven invariants hold: no merge/repo-create tool and
`github.open_pr` is terminal (`threat-model-draft.yaml`, nothing `depends_on: [open-pr]`); the two
analysis workflows end at `github.pr_comment` (`gate: auto`) while `threat-model-draft`'s `open-pr`
node is downstream of a `gate: human`/`approvers: [initiator]` node; every agent prompt keeps the
untrusted-content instruction; writes reuse the existing audited tool path; the diff is YAML + prompts
+ a pinned binary + one allowlist word + tests (no new C#, no new node kind); every tool named is in
the catalog. The gitleaks pin was verified byte-for-byte against the official 8.18.4 `linux_x64`
checksum. Demonstrated live: `dependency-audit` found the planted High CVE and `secrets-sweep` ran
clean and reported honestly (both posted PR comments).

### MINORS — carried as tracked residuals

- **M6-min-1 — `sast-triage` not delivered (3 of the 4 named M6 workflows shipped).** Reconciled by
  **explicit descope**: the three delivered packs already exercise all three shapes of the M6 pattern
  (auto-gated comment ×2, human-gated PR ×1), so `sast-triage` would be a fourth instance of a proven
  pattern (it needs a pinned SAST analyzer added to the runner image + allowlist — a curated platform
  change, invariant 7). Docs (CLAUDE.md, design-spec §5, product-vision §6) now state 3-of-4 with
  `sast-triage` deferred, so the source of truth no longer over-claims.
- **M6-min-2 — `threat-model-draft` live gated-PR demo deferred on gateway credit exhaustion.** The
  run failed mid-`draft` on a `strong`-model call when the gateway returned Anthropic's *"credit
  balance is too low"* (HTTP 400). This **failed fail-closed**: run → `Failed`, **no branch and no PR
  leaked** (verified), and the gate-decision endpoint correctly refused (`409 not awaiting approval`).
  The workflow's human-gate-before-`open_pr` structure is verified statically here and by the offline
  eval; only the live witness of the PR is pending a credit top-up (human spend checkpoint).
- **M6-min-3 (nit) — inconsistent leading U+FEFF BOM** across some prompt files (`secrets-sweep`,
  `threat-model-draft` have it; `dependency-audit` does not). Harmless (hashed consistently, ignored
  by the model); cosmetic only.
- **F11 egress (pre-existing) — `dependency-audit`'s `dotnet list package --vulnerable` reaches the
  NuGet advisory DB.** Runner egress is the tracked graduation residual, not M6-specific; noted for
  completeness. Analyzer isolation remains a subprocess sandbox, not a container.

---

## M5 — QA workflow pack (2026-07-23)

Independent review gate audited the full M5 diff `3d95fd0..HEAD` (2 workflows + 7 prompts + 1 test
file, no other source touched). Verdict: **shippable as-is, no MAJORs, zero scope creep.** All seven
invariants hold; the headline M5 invariant (workflows-as-data) holds strictly — no C#, no new node
kind, no new tool. The two riskiest areas were checked and are correct: the **characterization
discipline** (both authoring prompts direct the agent to assert the code's *actual* behaviour and
surface a suspected bug as a `SUSPECT:` note rather than assert an idealized value that would loop
forever — the exact fix for the M2 never-green contradiction) and the **gate-before-push ordering**
(both workflows place a human gate between the authoring loop and the open-pr node, enforced by
`depends_on`). Detection is genuinely deterministic (a real `dotnet test --collect` coverage
measurement with pinned `coverlet.collector` 6.0.2), and the agent triages the cobertura report
rather than inventing numbers. Demonstrated live to the gate (25% coverage / DiscountEngine 0% →
authored tests → green → paused).

### MINOR — fixed

- **M5-min-1 — offline load tests didn't assert catalog membership.** The loader validates
  ids/depends_on/prompt_ref/gate but not node kinds or tool names, so an invented tool/kind would
  only fail at runtime. **Fixed**: `ShippedWriteWorkflowTests` now asserts every node kind is known
  and every tool a workflow names is in `ToolCatalog.Default`, across all five shipped workflows —
  caught offline, not on a run.

### MINORS — carried as tracked residuals

- **M5-min-2 — `github: open_pr+issues` grants an unused "issues" sub-capability.** Both QA
  workflows declare it (consistent with `test-generation`) though neither uses an issue tool. It is
  the minimal lattice rung that permits `open_pr`/`push_branch` (there is no `open_pr`-without-issues
  level), so it is not genuinely over-broad — noted for when the lattice is refined.
- **M5-min-3 (pre-existing) — gate-before-write is a data+test guarantee, not engine-structural.**
  Applies to these two new workflows as to the M2 ones; belongs to the M7 `policy.yaml` floor.
- **Coverage-artifact cleanliness** — the `coverage/` output dir would have been committed into the
  PR; fixed in-milestone by adding `coverage/` + `**/TestResults/` to `.gitignore` in the
  enable-coverage step.

---

## F1 — Operations console (2026-07-23)

Independent review gate audited the full F1 diff `3e30ddb..HEAD`. Verdict: **shippable as-is, no
MAJORs.** All seven invariants hold; the F1 "UI is a strict client of the API/audit trail"
invariant holds strictly (the Blazor components inject only `IRunQueries`, `IRunCoordinator`,
`NavigationManager` — no DbContext/HttpClient/Octokit/File/tool access anywhere); the XSS surface
is clean (both payload sinks render as Razor-encoded `<pre>@text</pre>`, no `MarkupString`/raw HTML
in the components, no external CSS resource); the fail-closed guard extraction from `Program.cs`
into `RunCoordinator` is faithful and in the same order (allowlist → AwaitingApproval → allowlist
re-check → workflow-sha → decision-recorded-before-resume → the byte-faithful background error
handling with no status-overwriting finally), and the HTTP API and the UI now share that one
enforcement path. Exit met: run list + 3s live refresh, run detail with the node/event timeline and
per-event payload viewer, audit-chain viewer whose Verify calls the same `AuditEmitter.VerifyAsync`
as the CLI, the gate approval screen (surfacing node outputs for review before Approve/Reject), and
token/cost totals. 20 offline tests, honest (the records-before-resume ordering is a real
happens-before assertion). No scope creep into F2+.

### MINORS — carried as tracked residuals

- **F1-min-1 — the gate-decide endpoint dropped the `decidedSha`/`currentSha` diagnostic fields** the
  original `DefinitionChanged` response carried (the coordinator returns a bare enum). Harmless; the
  fields were a useful operator hint. Restoring them means widening the coordinator's return, not
  worth it now — noted.
- **F1-min-2 — the Launch page is a second UI write** (start a run) beyond product-vision §5's literal
  "writes only the gate decision." It goes through the same guarded `IRunCoordinator.StartAsync` as
  `POST /runs` (a form equivalent of the documented curl) and was sanctioned in the F1 brief — within
  the invariant, but the vision wording and the shipped surface should be reconciled in the doc.
- **F1-min-3 — the gate-review panel surfaces every completed `node_end` before the gate seq** rather
  than the gate's actual DAG dependencies (the UI has no workflow graph via the seam). Documented
  in-code; acceptable, but a large workflow shows more review material than strictly gate-relevant.
- **Token/cost shows 0** because the emitters never populate `TokensIn/Out/CostUsd` — the pre-existing
  A7/F8 residual, not an F1 defect; the console plumbing is correct and will display real numbers
  once the emitters are wired.

---

## M3 — Multi-repo & search (2026-07-23)

Independent review gate audited the full M3 diff `6464ac5..HEAD`. Verdict: **all 7 invariants hold,
exit criterion MET, zero scope creep, no MAJORs.** Per-run GitHub binding closes the M2 "split
source of truth" — the runner clones `run.Repo` and the GitHub tools now bind to the same
`ctx.Repo`. Search is read-only and confined to the allowlist two ways (request `repo:`/`user:`
qualifier AND an exact-full-name post-filter); a crafted query cannot surface a repo outside the
allowlist. The allowlist is enforced fail-closed before run creation and re-checked on resume; an
empty allowlist is deny-all (and fails startup).

Exit judged met on a demonstrably-correct mechanism: the single-repo live demo (allowlist rejects a
non-allowlisted/malformed repo; the allowlisted repo runs via the per-run factory and comments on
`run.Repo`) plus comprehensive offline multi-repo tests. A second *live* allowlisted repo is
confirmatory, not necessary (it needs a broader PAT — a legitimate token-scope deferral, matching
the spec's "GitHub App when more repos join"). No shipped workflow uses search yet — acceptable, as
the spec adds the *capability*; a search-using workflow is an invariant-6 data change.

### MINORS — fixed

- **M3-min-4 — the forbidden-name guard omitted `fork`** (pre-existing; invariant 1 names fork
  explicitly). **Fixed**: added `fork` to `ToolCatalog.ForbiddenToolName`, with test cases — a
  catalog declaring `github.fork`/`repo.fork_repo` is now rejected at load.
- **M3-min-2 — dead config.** `appsettings.json` still carried `GitHub:Owner/Repo` (and compose
  passed `GitHub__Owner/__Repo`), which nothing reads after the per-run un-binding. **Removed**, with
  a comment pointing at `RepoAllowlist`.

### MINORS — carried as tracked residuals

- **M3-min-1 — wildcard allowlist entries are inert for code search.** An `owner/*` entry is honored
  for run-targeting but produces an invalid `repo:owner/*` search qualifier + an exact post-filter
  that never matches, so a wildcard-only allowlist returns zero search results. Fail-closed and
  cannot leak, but "agents can search" is false for a wildcard operator. **Documented** the
  constraint in the `appsettings` allowlist comment (search scopes to exact `owner/name` entries).
  Full fix (expand a wildcard owner to a `user:owner` qualifier + owner-level post-filter) is a
  follow-up in `GitHubToolset`. Current config uses an exact entry, so live search works.
- **M3-min-3 — `RepoAllowlist.Assert()` doc vs. reality.** Production enforces via `IsAllowed()`
  (which reflects `req.Repo` into the JSON-escaped error body); `Assert()`'s non-echoing protection
  is unused. Low risk (escaped response to the submitter). Align the doc or route production through
  `Assert()` — noted, not changed.

---

## M2 — Write path (2026-07-23)

Independent review gate audited the full M2 diff `f732367..HEAD`. Verdict: **all 7 invariants hold**
(the no-merge headline invariant defended in depth — command allowlist is `{git, dotnet}` only, the
catalog loader regex-rejects merge/repo-lifecycle names, the tool switch is closed, negative tests
assert no merge API is ever called), **zero scope creep**, exit criterion partially met pending the
live witnessed run.

### MAJOR — fixed

- **M2-1 — read tools were not scoped to the run's worktree.** Only `repo.write_worktree` used the
  per-run clone; `repo.read`/`repo.list`/`codesearch.query` still pointed at the shared
  `/data/worktrees` root. In a write workflow the agent could not read the files it had just cloned
  and written (they live under `{root}/{runid}/…`), and `list`/`search` enumerated every concurrent
  run's worktree — a cross-run visibility gap. **Fixed** (commit *M2 review MAJOR (M2-1)*):
  `ToolRegistry` routes read tools through `RepoFor(ctx)` — the run's own worktree when a runner is
  attached, the shared root only for read-only workflows (pr-review unchanged). 4 new tests pin it.
  The review predicted the pending live run would surface this; it did (the first witnessed run,
  against the pre-fix code, showed the implement agent reading the wrong tree), which is why the fix
  landed before the clean witnessed run.

### MINORS — fixed

- **Prompt/tool-name mismatch:** four write prompts referenced `repo_list_dir`; the registered tool
  is `repo_list_files`. **Fixed** — the agent no longer wastes a call on a 404.

### MINORS — carried as tracked residuals

- **F11 — the subprocess runner has no egress control.** The bash/agent-loop validation runs
  untrusted `dotnet test` (NuGet restore + arbitrary cloned code) with unrestricted outbound
  network. Honestly disclosed in the runner code and `docs/threat-model.md`; closes only with the
  container drop-in (documented seam). The threat-model F11 row (which read "M2 — blocks the
  runner") is annotated to reflect the deliberate re-deferral to the container implementation,
  acceptable for a single-workstation PoC.
- **Gate-before-write is a data+test guarantee, not engine-structural.** Nothing in the engine
  forces a node holding `push_branch`/`open_pr` to depend on a human gate — it is enforced by how
  the two workflows are authored (invariant 6, change-controlled) plus `ShippedWriteWorkflowTests`.
  A policy control that *requires* a preceding human gate for write-frontier tools is the right
  defense-in-depth; noted for M7's org policy floor (`policy.yaml`), where per-vision-doc that class
  of rule ("any `github.open_pr` requires `gate: human`") belongs.
- **Split source of truth for the target repo:** the runner clones `run.Repo` (request) while
  `GitHubToolset` push/open_pr act on the startup-configured owner/repo. Harmless while they are the
  same single repo; the per-run `GitHubToolset` factory + repo allowlist is M3, correctly not pulled
  forward.
- **Token briefly in git argv** on a shared host (runner clone URL) — PoC-acceptable, closed by the
  container implementation; already in the threat model.

### Descope recorded

- **Archon bake-off** (spec §5 M2 "Archon-vs-platform bake-off data in hand"): descoped to
  *eval-harness-ready* — the external Archon pilot is unavailable here. The golden-run comparator
  (`tests/Harness.Eval`, tolerant + explainable, 13 tests) provides the comparison capability; there
  is simply no Archon data to feed it. The design-spec M2 status line is annotated to record this so
  it does not read as an unmet deliverable.

---

## M1 — Governance hardening (2026-07-23)

Independent review gate (a fresh agent, not involved in implementation) audited the full M1 diff
`387552c..HEAD` against the invariants and the M1 exit criterion. Verdict: **all 7 invariants
hold, exit criterion met, zero scope creep into M2+.** One MAJOR and several minors were raised;
disposition below.

### MAJOR — fixed

- **M-1 — deletion-detection rested on a mutable in-band anchor.** `ChainVerifier` used the
  heuristic "zero events + `run.Status != Pending` ⇒ broken", but `Status` is a mutable column in
  the same table reachable by the same DB role — delete every event, set `Status=Pending`, and the
  chain reported `intact: true`. Tail-truncation of a completed run was undetectable entirely, and
  F4 (the append-only DB grant the threat model rated High/M1) was never implemented.
  **Fixed** (commit *M1 review MAJOR (M-1)*): each run carries a chain-head anchor
  (`HeadSeq`/`HeadHash`) advanced by `AuditEmitter` in the same transaction as the event insert;
  `ChainVerifier` now checks the chain terminates exactly at the anchor, so tail and wholesale
  deletion are caught at the first missing seq. A `BEFORE UPDATE OR DELETE` trigger makes `Events`
  append-only at the database. Demonstrated live against Postgres (see `docs/m1-exit-check.md`).
  **Documented residual:** the anchor is itself an in-band mutable column and the app connects as
  the table owner, who can bypass the trigger and rewrite the anchor — so this is tamper-*evidence*
  hardening, not resistance against a malicious DB owner. Full resistance needs a least-privilege
  runtime role (INSERT/SELECT only); that is graduation/infra work (threat-model **F4**, M4).

### MINORS — fixed

- **Gate reject-resume skipped the workflow-sha check.** The approve path guarded replay against a
  changed definition; the reject path resumed with no check, so a definition changed while paused
  could execute inserted pre-gate nodes or run a DAG whose gate had moved. **Fixed**: both
  decisions now share the sha check, before the decision is recorded.
- **The fail-closed tool latch (invariant 2's runtime path) had no test.** **Fixed**: added
  `tests/Harness.Tools.Tests` (6 tests) — a policy block during invoke latches and survives being
  swallowed by the function loop, a clean call audits before and after, an ordinary tool error
  propagates to the model without latching.
- **Stale doc comments** on `Run.WorkflowSha` ("git SHA" → content hash) and `RunEvent.PayloadHash`
  ("prev+payload" → now binds five metadata fields). **Fixed**.

### MINORS — carried as tracked residuals

- **F7 — no size caps on tool output** (`RepoToolset.ListFiles`/`Search`, `GitHubToolset.GetPrDiff`).
  Partly contained gateway-side by `max_input_tokens` pre-call rejection, and the repo tools are
  inert until a worktree is cloned (M2). Carry into the M2 write-path hardening; ranked in
  `docs/threat-model.md`.
- **Attributability / auth (F1), egress control (F11), LiteLLM DB-backed per-workflow budgets** —
  all pre-existing threat-model items with the wrong shape for M1 (auth needs SSO/OIDC per spec;
  egress and budgets are M2/graduation). Tracked in `docs/threat-model.md` §6 with the M2-blocking
  ones flagged there.

### Not accepted as findings

- The review confirmed **no scope creep**: the `open_pr+issues` permission *level* and the
  `agent-loop` string in loader validation are forward-tolerant data/validation with no backing
  tool or executor — not capabilities. Gate stayed an attribute on `agent` nodes, not an M2 node
  kind.

# Review log

Findings from the milestone review gates, and how they were dispositioned. One entry per
milestone; newest first.

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

# Review log

Findings from the milestone review gates, and how they were dispositioned. One entry per
milestone; newest first.

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

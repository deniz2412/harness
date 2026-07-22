# M1 Exit Check — Report

**Date:** 2026-07-23
**Verdict:** ✅ **PASS** — M1 exit criterion met on a cold rebuild, after the review-gate MAJOR
was closed.
**Run:** `33ff4d3c-63eb-48dc-a0c0-487e23064bfc` (initiator `m1-final`) — the post-M-1 rebuild.
An earlier green run, `65de67eb-…` (`m1-regression`), predates the chain-head anchor; the numbers
below are from the final run unless noted.

## Criterion

Design-spec §5 M1 exit: *"a compliance colleague can be walked through a run end-to-end and
verify the chain."* Operationally that means the M1 scope is real and demonstrable: EF migrations
replace `EnsureCreated`, human-gate mechanics exist, a data-driven secret ruleset and real
permission-ceiling enforcement exist, gateway budgets exist, a STRIDE model was run, and the audit
chain can be independently verified — and actually detects tampering.

## Regression gate — M0 still holds

Cold rebuild (`docker compose down -v` → `up --build`, fresh volumes, the migration path exercised
from empty), then the pr-review run against the test PR:

| # | Check | Result |
|---|-------|--------|
| 1 | Both migrations apply on an empty volume | Pass — `InitialCreate` then `AnchorAndAppendOnlyEvents` applied; anchor columns + append-only trigger confirmed present |
| 2 | Review comment lands | Pass — [issuecomment-5052595226](https://github.com/deniz2412/test-repo-harness/pull/1#issuecomment-5052595226) |
| 3 | Run completes | Pass — status `Completed`, 15 events |
| 4 | `/verify` intact | Pass — `{"intact":true,"reason":null}`, `headSeq 15` with a matching `headHash` |

## What M1 added, demonstrated

**Tool calls are now audited (invariant 5, an M0 defect).** The event stream grew from 9 to 16
events: every tool call emits `tool_call` before it runs, and an externally visible write emits
`tool_result` after. The `post` node's write is seq 14→15, and the `tool_result` payload holds the
exact comment URL:

```
14  tool_call    post   tool=github.pr_comment
15  tool_result  post   "https://github.com/.../pull/1#issuecomment-5052432884"
```

The `review` node's three `repo.read` calls (seq 8–10) show arguments audited but no `tool_result`:
`repo.read` failed on the empty worktree (no clone until M2), which is an *ordinary* tool error —
returned to the model, not latched — so the agent reviewed off the gather output. A policy block
would instead have latched and failed the node. That is the fail-closed/fail-to-model split working
as designed.

**The run is tied to its definition (invariant, spec §2.6).** `workflowSha` is now a real content
hash over the workflow YAML and its prompts —
`48150c965b2740930668b035341413e83e32b17d6536607916112576d115ff1e` — not the hardcoded `"dev"`.

**The chain is independently verifiable — the exit criterion itself.** The `harness-audit` CLI,
which does not depend on the API, connected to Postgres and the payload volume from a throwaway
container and recomputed the chain:

```
VERDICT: intact — 16 event(s) verified.   (exit 0)
```

**And the verifier is not vacuous.** A reversible metadata tamper — retyping seq 3 from `tool_call`
to `node_end` directly in Postgres, leaving the payload file and the stored hash untouched (exactly
a DB-write attacker) — was caught at its own seq, then restored:

```
3  node_end  gather  BROKEN [HashMismatch] recomputed hash does not match the stored hash
VERDICT: BROKEN at seq 3                                                       (exit 1)
... restore ...
VERDICT: intact — 16 event(s) verified.                                       (exit 0)
```

The M0 hash covered only `prev + payload`, so this exact attack would have verified `intact`. The
widened hash (STRIDE F3) binds `RunId/Seq/Type/Node/Ts` with a length-prefixed injective encoding,
so retype / reattribute / renumber / backdate are all now detectable. Deletion holes (missing
payload file, seq gap, wholesale-deleted events) were closed in the same milestone.

**Deletion is now caught by a chain-head anchor, and the trail is append-only at the DB (review
MAJOR M-1 + F4).** The review gate found that deletion-detection had rested on a mutable in-band
heuristic. That is closed, and both the anchor and the append-only trigger were demonstrated live
against the running Postgres:

```
F4  DELETE on Events        →  ERROR: Events is append-only: DELETE on "Events" is not permitted
F4  UPDATE on Events        →  ERROR: Events is append-only: UPDATE on "Events" is not permitted
M-1 head anchor bumped 15→16 (claiming a 16th event that does not exist):
    /verify → intact:false, firstBrokenSeq 16,
      "chain ends at seq 15 but the run's head anchor is seq 16 — 1 event(s) deleted from the tail"
    ... restore anchor to 15 ...
    /verify → intact:true
```

The append-only trigger stops accidental and non-owner mutation; the anchor makes a tail or
wholesale deletion detectable even if the trigger is bypassed. Honest limit, documented in the
migration and carried in `REVIEW.md`: the app connects as the table owner, who can both bypass the
trigger and rewrite the in-band anchor — so this is tamper-*evidence*, not tamper-*resistance*
against a malicious DB owner. That needs a least-privilege runtime role at graduation
(threat-model F4/M4).

## Tests

163 offline unit tests across the milestone: 104 policy (ruleset, entropy gate, redaction,
fail-closed, the exact pr-review ceiling case), 31 audit (tamper/deletion/metadata/anchor
detection, stores, node-output readback), 22 engine (gate pause/approve/reject/resume, sha
stability), 6 tools (the fail-closed tool latch, audit-before/after). `dotnet build` clean,
`dotnet test` green.

## Known residuals carried forward

- **Tail-truncation of the chain** is still undetectable — deleting the last N events leaves a
  shorter but internally consistent chain. Closing it needs a per-run head anchor (two `Run`
  fields + emit/verify changes). This is the top audit residual; see `REVIEW.md`.
- **No authentication, caller-supplied initiator** (threat-model F1) — attributability is only as
  good as the caller's honesty until auth lands. Must close before the M2 write path.
- **Prompt injection to an external write** (F2) — mitigated by the untrusted-content instruction
  now on every prompt and by tool auditing, but the real control is a human gate on write-tool
  nodes, which is M2.
- Full list and ranking in `docs/threat-model.md` §6.

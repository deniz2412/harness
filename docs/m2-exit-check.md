# M2 Exit Check — Report

**Date:** 2026-07-23
**Verdict:** ✅ **PASS** — three workflows operational, writes human-gated, a real gated PR opened
and witnessed. Archon bake-off descoped to eval-harness-ready (external pilot unavailable).
**Witnessed run:** `2eeaec97-91ae-4813-ba24-c0af3ac04625` (`test-generation`, initiator `m2-witness`)

## Criterion

Design-spec §5 M2 exit: *"three workflows operational; writes gated; Archon-vs-platform bake-off
data in hand."* The write path ends at a PR — no merge, ever.

## Regression gate — pr-review still holds

After the full M2 diff, a cold-rebuild `pr-review` run (read-only) still completes with the chain
intact and — importantly — creates **no** runner sandbox (the write path is inert for a read-only
ceiling). The M2 image moved to the .NET SDK + git so the runner can clone and run `dotnet test`
as subprocesses inside the container, as a non-root `app` user.

## The three workflows

| Workflow | Kind | Status |
|----------|------|--------|
| `pr-review` | read-only, ends at a PR comment | operational since M0, regression-green |
| `test-generation` | write path, human-gated, ends at a PR | **demonstrated live (PR #2)** |
| `issue-to-pr` | write path, human-gated, ends at a PR | loads + validates; same machinery as test-generation |

All three load through the real `WorkflowLoader` and are content-pinned (`WorkflowSha`).

## The witnessed gated write — demonstrated, not asserted

`test-generation` against PR #1 of the test repo, driven end to end with the human at the gate:

```
gather (agent, read)                          → context on the discount engine
author-tests (agent-loop, 2 iterations)       → wrote DiscountEngineTests.cs into the worktree,
                                                 `dotnet test` went green on iteration 2
approve (gate: human)  ⏸  AwaitingApproval     → RUN PAUSED. Worktree persisted on disk with the
                                                 generated tests; nothing pushed yet.
      … human reviewed the pending tests and approved via POST /gates/approve/decide …
open-pr (agent)        ▶  resumed              → reused the same worktree, push_branch +
                                                 github.open_pr
```

Result: **[PR #2 "Generated tests for PR #1"](https://github.com/deniz2412/test-repo-harness/pull/2)**,
branch `test-generation-pr-1`, opened — never merged. Run `Completed`, and the `harness-audit` CLI
independently verified the chain: **`VERDICT: intact — 68 event(s) verified`**, including the gate
request/decision and both write tool calls (`push_branch` seq 64, `open_pr` seq 66, each with a
`tool_call` before and a `tool_result` after — invariant 5 on the write path).

The generated tests are genuine characterization tests: the agent captured the discount engine's
**actual** (buggy, compounding) behaviour as passing assertions — e.g. `510 * 0.95 * 0.90 = 436.05`
with a comment flagging the cumulative bug — rather than asserting a "correct" value that would fail
the loop. That is the right behaviour for a test-generation tool: pin what the code does, flag the
suspicion for a human.

## Three real defects the live run exposed (all fixed before the PR opened)

The unit tests were green throughout; these only surfaced by running the whole path against a real
repo, and each is now covered by a test that would fail if it regressed.

1. **Read tools were not worktree-scoped (review MAJOR M2-1).** Only `write_worktree` used the
   per-run clone; the read tools pointed at the shared root, so the implement agent couldn't read
   the files it had cloned. Fixed: `ToolRegistry` scopes reads to `ctx.Runner.WorktreePath`.
2. **The agent-loop never fed validation output back to the agent.** With `fresh_context`, each
   iteration started blind and reproduced the same `dotnet test` failure five times. Fixed: the
   failed output (bounded tail) is fed to the next iteration and recorded in the audit trail.
3. **The worktree was torn down at the gate pause** — so a resumed run had nothing to push. This
   is incompatible with the spec's implement→gate→push shape. Fixed: the worktree is keyed by run
   id and reused on resume; the session is disposed only when the run truly ends, never at a pause.

## Tests

228 offline unit tests: policy 104, audit 31, engine 31, runner 17, tools 26, agents 9, eval 13.
`dotnet build` clean, `dotnet test` green. The runner suite clones from a local git origin (no
network); the eval comparator catches a dropped/downgraded finding while tolerating cosmetic drift.

## Known residuals carried forward (see `REVIEW.md`, `docs/threat-model.md`)

- **F11 — the subprocess runner has no egress control.** Untrusted `dotnet test` runs with open
  outbound network; only the container runner (documented drop-in behind `IRunnerFactory`) closes
  it. The headline runner residual, deferred to graduation.
- **Abandoned gate pauses leak a worktree** until a TTL reaper (future work).
- **Double `gate_decision` audit event** on approval (both the decide endpoint and the executor
  emit one) — redundant, not wrong; the chain still verifies. Minor, tracked.
- **The agent-loop discards the agent's final text**, so a test-generation `SUSPECT:` line does not
  yet reach the PR body. Minor refinement.
- Gate-before-write is a data+test guarantee, not engine-structural — an org policy floor requiring
  a human gate before write-frontier tools is M7.

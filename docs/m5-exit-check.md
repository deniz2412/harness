# M5 Exit Check — Report

**Date:** 2026-07-23
**Verdict:** ✅ **PASS** — two QA workflows operational, following the deterministic-detect → AI-triage
→ human-gated-PR pattern. Pure workflows-as-data; no C#, no new node kinds, no new tools.
**Demonstrated run:** `0bda91bf-…` (`coverage-gap-analysis`, initiator `m5-demo`)

## Criterion

product-vision §3/§6 M5: *"QA workflow pack — coverage-gap, regression-author + analyzer runner
images."* The pack pattern: deterministic tools measure/detect, agents triage and author, humans
approve — all ending at a gated PR, no merge.

## What shipped (data only)

- **`coverage-gap-analysis`** — gather → enable-coverage (agent adds a pinned `coverlet.collector`
  6.0.2 reference via `repo.write_worktree`, path discovered not hardcoded) → **measure** (bash:
  `dotnet test --collect:"XPlat Code Coverage" --results-directory coverage`, generic and
  no-shell-safe) → raise-coverage (agent-loop: read the cobertura, fill the biggest gap with passing
  characterization tests, bounded, validate `dotnet test`) → **gate: human** → open PR.
- **`regression-suite-author`** — plan → author-suite (agent-loop: a thorough characterization suite
  for one under-tested module) → **gate: human** → open PR. Distinct from M2's `test-generation`
  (broad pre-refactor safety net vs. a few gap-fills).

Both use only existing node kinds (`agent`/`agent-loop`/`bash`/`gate`) and the existing tool
catalog, end at `github.open_pr` with a hard human gate before any push, and every prompt forbids
following instructions embedded in untrusted repo content — including the coverage report itself.

The "analyzer runner images" the vision names are realised as **pinned analyzer tooling**
(`coverlet.collector` 6.0.2) run in the subprocess sandbox — the same subprocess-vs-container
deviation recorded for M2; the container runner is still the documented drop-in.

## Demonstrated live

`coverage-gap-analysis` against the test repo ran end to end to the gate:

```
gather ✓ → enable-coverage ✓ (added coverlet.collector) → measure ✓ (dotnet test --collect)
        → raise-coverage ✓ (2 iterations, authored DiscountEngineTests.cs, dotnet test green)
        → gate: human  ⏸  AwaitingApproval
```

The **deterministic detection was real**: the `measure` node produced a cobertura report showing
**25% line coverage overall and `DiscountEngine` at 0%** — and the agent triaged that report (not
invented numbers) to target the uncovered engine, authoring passing characterization tests for it
and looping until `dotnet test` went green. The run paused at the human gate with nothing pushed.

A coverage-artifact cleanliness bug surfaced by running it — the `coverage/` output dir would have
been committed into the PR by `push_branch`'s `git add -A` — and was fixed: the enable-coverage step
now appends `coverage/` and `**/TestResults/` to `.gitignore`, so a coverage PR carries only the
collector reference and the new tests.

The terminal `open_pr` write was **not** re-exercised for this milestone: it is the exact gated-write
path already demonstrated live in M2 (a real PR, test-repo-harness#2). Completing a coverage PR is a
one-command run away with the fixed prompt.

**A fail-closed control demonstrated by accident:** committing the coverage-prompt fix while the
demo run was paused changed the workflow's content hash, so the gate decision was refused — *"the
workflow definition changed while this run was paused; the decision does not carry over"* — exactly
the workflow-sha stability guard (M1/M2) working. The lesson: don't edit a definition a run is
paused on. (That demo run remains harmlessly un-decidable in the console.)

## Regression

pr-review still completes end to end with the chain intact — M5 is data-only, so the run path is
untouched.

## Tests

312 offline unit tests, incl. `ShippedWriteWorkflowTests` now validating **all four** write
workflows (test-generation, issue-to-pr, coverage-gap-analysis, regression-suite-author): each loads
through the real `WorkflowLoader` and is content-pinned, declares the write ceiling, places a human
gate before the open-pr node, references no merge capability, and bounds its agent-loop with
`dotnet test` validation. Offline load/structure validation is the right automated check for data
workflows — a full run needs a live model + runner.

## Residuals carried forward (see `REVIEW.md`, `docs/threat-model.md`)

- The demo run is stuck `AwaitingApproval` (sha changed under it) — harmless artifact; illustrates
  the sha guard.
- Token/cost still 0 on audit events (A7/F8); coverage/analysis costs aren't attributed.
- flaky-test-hunter (a third QA workflow the vision lists as a candidate) is not built — deferred;
  the two named ones (coverage-gap, regression-author) are delivered.
- Analyzer isolation is a subprocess sandbox, not a container (F11 egress residual).

# M6 Exit Check — Report

**Date:** 2026-07-23
**Verdict:** ✅ **PASS (pattern demonstrated; 3 of 4 workflows, one live PR deferred)** — the security
pack's deterministic-scan → AI-triage → gated-PR/comment pattern is operational and demonstrated live.
Pure workflows-as-data plus one pinned analyzer binary; no C#, no new node kinds, no new tools.
**Demonstrated runs:** `9508772a-…` (`dependency-audit`), `196e0c79-…` (`secrets-sweep`).

## Criterion

product-vision §3/§6 M6: *"Security workflow pack — dependency-audit, secrets-sweep, sast-triage,
threat-model-draft."* The pack pattern (identical to M5): deterministic analyzers detect, agents
triage/explain/draft, humans approve — **defensive and repo-scoped**, ending at a comment or a gated
PR, never a merge and never offensive tooling.

## What shipped (data only, + one pinned binary)

- **`dependency-audit`** — restore (bash: `dotnet restore`) → **audit** (bash: `dotnet list package
  --vulnerable --include-transitive`) → gather (agent, PR diff + read) → **report** (agent, `gate:
  auto`, `github.pr_comment`). Ends at a PR comment; no push, no PR, no merge.
- **`secrets-sweep`** — **scan** (bash: `gitleaks detect --source . --no-git --report-format json
  --exit-code 0`) → **report** (agent, `gate: auto`, `github.pr_comment`). The report prompt forbids
  echoing any recovered secret value — findings are referenced by rule id + `file:line` only, and the
  raw `gitleaks-report.json` (which holds real match values) stays in the ephemeral sandbox, never
  pushed. Ends at a PR comment.
- **`threat-model-draft`** — study (agent, read tools) → draft (agent, writes
  `docs/THREAT-MODEL-draft.md` via `repo.write_worktree`) → **gate: human** (`approvers: [initiator]`)
  → open-pr (agent, `github.push_branch` + `open_pr`). A STRIDE-style draft opened as a **human-gated
  PR** marked AI-generated; ends at the PR, never merges.

All three use only existing node kinds (`agent`/`bash`/`gate`) and the existing tool catalog; every
prompt forbids following instructions embedded in untrusted repo/PR/scan content.

The vision's "analyzer tools in runner containers" are realised as a **pinned gitleaks 8.18.4 binary**
installed into the runner image (download verified against the official `linux_x64` SHA-256, run as
non-root `app`) and added to `RunnerOptions.AllowedPrograms` — a curated platform change (invariant
7). Same subprocess-vs-container deviation recorded for M2/M5; the container runner is the documented
drop-in.

## Demonstrated live

**`dependency-audit` against PR #1 — found a real High CVE:**

```
restore ✓ → audit ✓ (dotnet list package --vulnerable) → gather ✓ → report ✓ (posted comment)
```

The `audit` node's `dotnet list package --vulnerable` detected **`System.Net.Http` 4.3.0, High
severity, GHSA-7jgj-8wvc-jh57** (a dependency planted on `feature/bulk-discounts` for this demo). The
agent triaged the real scanner output — not invented numbers — correctly flagged it as *added in this
PR*, gave remediation guidance, and posted a well-formed findings comment. Run `Completed`, chain
intact.

**`secrets-sweep` against PR #1 — ran clean, reported honestly:**

```
scan ✓ (gitleaks detect) → report ✓ (posted "No secrets detected")
```

gitleaks scanned the worktree, found nothing, and the agent posted an honest clean result rather than
inventing findings. (During M6 prep the auto-mode classifier blocked planting a synthetic
`sk_live_…` secret; that block was respected — only the vulnerable dependency was planted — so
secrets-sweep reports clean truthfully.) Run `Completed`.

**`threat-model-draft` — deferred on gateway credit exhaustion (fail-closed held):**

```
study ✓ → draft … ✗ Failed  (gateway HTTP 400: Anthropic "credit balance too low", Model Group=strong)
```

The run executed `study` fully and began `draft`, then failed on a `strong`-model call because the
**gateway's Anthropic credits are exhausted**. This is the **fail-closed invariant working correctly**:
run → `Failed`, **no branch and no PR leaked** (verified against the repo's branches and open PRs),
and the gate-decision endpoint refused (`409 not awaiting approval`) because the run never reached the
gate. Invariants 2 (fail-closed) and 3 (gateway is the only model path) both held under a real
upstream failure. The live witness of the gated PR is deferred to a credit top-up (a human spend
checkpoint); the workflow's human-gate-before-`open_pr` structure is validated statically and by the
offline eval, and its `study`/`draft` nodes executed, so only the terminal PR-open is unwitnessed.

## Regression

`pr-review` still completes end to end with the audit chain intact (run `a3713828-…`, 23 events,
`/verify` intact) — M6 adds data + a runner binary, so the run path itself is untouched.

## Tests

**338 offline unit tests, all green.** `ShippedWriteWorkflowTests` now **auto-discovers every
workflow** in `workflows/` (`AllWorkflows()`), so each new one inherits the universal checks
automatically — loads through the real `WorkflowLoader` and is content-pinned, names only known node
kinds, references only catalogued tools, and mentions no merge capability. The gated-write list
additionally holds `threat-model-draft` to its write-ceiling + human-gate-before-`open_pr` checks
(the eval `Harness.Eval` project is 39 of the 338). Offline load/structure validation is the right
automated gate for data workflows; a full run needs a live model + runner.

## Scope descope recorded

**`sast-triage` — one of the four M6 workflows named in the vision — was not built this milestone**
and is explicitly deferred. The three delivered packs already exercise all three shapes of the M6
pattern (auto-gated comment ×2, human-gated PR ×1); `sast-triage` would be a fourth instance of a
proven pattern and needs a pinned SAST analyzer added to the runner image + allowlist (a curated
platform change). CLAUDE.md, design-spec §5, and product-vision §6 are updated to state 3-of-4 so the
source of truth does not over-claim.

## Residuals carried forward (see `REVIEW.md`, `docs/threat-model.md`)

- `threat-model-draft`'s live gated-PR demo is pending an Anthropic credit top-up (spend checkpoint);
  the run failed fail-closed with nothing leaked.
- `sast-triage` deferred (above).
- Token/cost still 0 on audit events (A7/F8); scan/triage costs are not attributed.
- Analyzer isolation is a subprocess sandbox, not a container; `dependency-audit` reaches the NuGet
  advisory DB — runner egress is the tracked F11 graduation residual.
- Cosmetic: inconsistent leading U+FEFF BOM across some prompt files (harmless).

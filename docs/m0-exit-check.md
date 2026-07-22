# M0 Exit Check — Report

**Date:** 2026-07-22
**Verdict:** ✅ **PASS** — all three exit criteria met.
**Run:** `044e7256-1840-4a15-8ba1-10605e3265c3` (initiator `m0-exit-check`)

## Criteria and results

The exit check defined in `CLAUDE.md`: compose up → run `pr-review` against the test PR →
review comment lands → `/runs/{id}/events` populated → `/runs/{id}/verify` returns `intact: true`.

| # | Criterion | Result |
|---|-----------|--------|
| 1 | `docker compose up` | Pass — all three services running; postgres gated `Healthy` before harness started |
| 2 | Review comment lands on the test PR | Pass — [issuecomment-5052015438](https://github.com/deniz2412/test-repo-harness/pull/1#issuecomment-5052015438), 21:54:18Z |
| 3 | `/runs/{id}/events` populated | Pass — 9 events, full `gather → review → post` |
| 4 | `/runs/{id}/verify` intact | Pass — `{"intact":true,"firstBrokenSeq":null}` |

## Evidence

Run record:

```json
{
  "id": "044e7256-1840-4a15-8ba1-10605e3265c3",
  "workflow": "pr-review",
  "workflowSha": "dev",
  "initiator": "m0-exit-check",
  "repo": "test-repo-harness",
  "pullRequest": 1,
  "status": 3,
  "startedAt": "2026-07-22T21:53:42.515972+00:00",
  "finishedAt": "2026-07-22T21:54:22.256150+00:00"
}
```

Status `3` = `Completed`. Wall time 39.7s.

Event stream — three nodes, each emitting start / model call / end:

```
1  node_start  gather     4  node_start  review     7  node_start  post
2  model_call  gather     5  model_call  review     8  model_call  post
3  node_end    gather     6  node_end    review     9  node_end    post
```

Model tiering, from the `model_call` audit payloads — cheap for gather, strong for the
reasoning and write nodes, as designed, with each node seeing only its declared tools:

```
seq 2  model=cheap   tools=[github.pr_diff,repo.read,codesearch.query]
seq 5  model=strong  tools=[repo.read]
seq 8  model=strong  tools=[github.pr_comment]
```

Review quality: the agent caught the planted discount bug, correctly identifying that sequential
`if` statements compound rather than tier the discounts (27.325% instead of 15% for quantity > 100),
plus the exception-swallowing coupon parser.

## What this proves

The full path executes end to end:

```
POST /runs → DagExecutor (topological) → AgentNodeExecutor → MAF AIAgent (AsAIAgent)
           → OpenAI-compatible client → LiteLLM gateway → Anthropic
           → Octokit → GitHub PR comment
           → AuditEmitter (hash-chained, payloads on volume) → /verify
```

Invariants observed in this run: the gateway was the only path to a model (errors during
bring-up surfaced as `litellm.BadRequestError`, confirming no direct provider calls); the run
ended at a PR comment with no merge capability; fail-closed behaviour halted every failed
attempt below rather than proceeding.

## Route to green

The check did not pass first time. Five defects were found and fixed getting here; each is
recorded because they were found by running the system, not by reading the spec.

| Defect | Symptom | Fix | Commit |
|--------|---------|-----|--------|
| MAF 1.6.1 API drift | `CreateAIAgent` does not exist | Extension is `AsAIAgent` on `OpenAI.Chat.OpenAIChatClientExtensions` | `f9a113d` |
| Blank GitHub config accepted | Empty owner/repo passed startup, 404s mid-run | Fail fast at startup; password from env | `c68ac64` |
| Compose ignored root `.env` | `-f docker/compose.yaml` makes `docker/` the project dir; every `${VAR}` blank | Documented `--env-file .env` | `7c4c089` |
| Postgres race | `EnsureCreated()` hit a not-yet-listening server, no retry | `pg_isready` healthcheck + `condition: service_healthy` | `4d5d631` |
| Audit volume root-owned | Non-root `app` (uid 1654) could not write payloads; every `EmitAsync` failed, killing runs at the first event | `/data` created app-owned in image so fresh volumes inherit it | `4d5d631` |
| Silent failure | Error handler's own audit emit threw, masking the original exception inside an unobserved `Task.Run` — run with no events, no logs | Emits wrapped with `LogCritical`; handlers log before emitting | `4d5d631` |

Two further blockers were external, not code: an exhausted Anthropic credit balance, and a
fine-grained PAT missing **Pull requests: Read and write** (reads succeeded, the comment POST
returned `Resource not accessible by personal access token`).

## Limitations of this check

Stated plainly, so the pass is not read as more than it is.

- **Warm stack, not a cold rebuild.** The run reused the already-running stack. The final commit
  touched only Markdown, so the running image is equivalent to a rebuild, but a clean
  `down` / `up --build` was not performed as part of the check.
- **`workflowSha` is `"dev"`.** Hardcoded in the `/runs` handler, so this run cannot be tied to
  the workflow and prompt versions that produced it. The audit chain proves the events were not
  tampered with; it does not prove *what definition* ran.
- **Tool calls are absent from the trail.** The 9 events cover node and model boundaries only.
  The PR comment — the sole externally visible write — emitted no audit event of its own.
- **Single scenario.** One workflow, one PR, one repo, happy path. No gate, no policy block, no
  agent-loop or bash node, no resume, no concurrent runs.
- **Unit tests remain thin** — 2 engine tests; nothing covers the agent, tool or audit layers.

## Carried into M1

The exit check surfaced correctness gaps between what the codebase claims and what it does.
Full list and ordering in `CLAUDE.md`; the three that matter most:

1. **Tool calls unaudited** — violates invariant 5. `ToolRegistry`'s doc comment asserts a
   "Harness.Agents middleware" that does not exist.
2. **Pre-tool policy stage is vacuous** — `AssertToolAllowed(name, toolNames)` is called from
   inside a loop over `toolNames`, so it cannot fail; the workflow `permissions:` ceiling is
   read by nothing.
3. **Tool results bypass the scanner** — `ScanOutbound` covers the prompt and final output, not
   content fetched mid-loop, so untrusted repo content reaches the model unscanned.

All three share one seam — the point of tool invocation — and a single `DelegatingAIFunction`
wrapper in `ToolRegistry` closes them together.

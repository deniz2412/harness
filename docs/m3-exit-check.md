# M3 Exit Check — Report

**Date:** 2026-07-23
**Verdict:** ✅ **PASS** — GitHub tooling is per-run, gated by a fail-closed repo allowlist; read-only
cross-repo search is available and confined to the allowlist. Any *allowlisted* repo runs; no repo
create/delete/fork.

## Criterion

Design-spec §5 M3 exit: *"any workflow runs against any allowlisted repo; agents can search but
never create repos."*

## What M3 changed

- **`GitHubToolset` is per-run.** It was a singleton built from a single startup-configured repo
  (`GitHub:Owner/Repo`). Now `GitHubToolsetFactory.ForRepo(run.Repo)` builds it per run, lazily, so
  a run acts on the repo it was launched against. This also closes the M2-review "split source of
  truth": the runner clones `run.Repo` **and** the GitHub tools bind to the same repo.
- **A repo allowlist is the policy control.** `RepoAllowlist` (operator config) decides which repos
  a run may target — exact `owner/name` or `owner/*`. Enforced fail-closed at `POST /runs` before
  anything is created, and re-checked on gate-resume. An empty/absent allowlist denies every run
  (and fails startup).
- **Read-only cross-repo search.** `github.search_code` / `github.search_repos`, catalogued at
  `(github, read)`, confined to the allowlist two ways (a `repo:`/`user:` request qualifier **and**
  an exact-full-name post-filter), double-bounded output (30 results / 8000 chars), fail-closed on
  an empty scope or blank query (no API call).
- **No repo create/delete/fork, no merge** — invariant 1 holds; the catalog loader now also rejects
  `fork` names as defence in depth.

## Demonstrated live

Against the running stack (allowlist = `deniz2412/test-repo-harness`):

```
POST /runs repo=someone-else/private-repo  → 400 "not allowlisted"        (fail-closed)
POST /runs repo=not-a-repo                 → 400 "not allowlisted"        (malformed denied)
POST /runs repo=deniz2412/test-repo-harness→ Completed, chain intact,
                                             comment posted on run.Repo   (per-run factory)
```

The allowlisted pr-review run completed on a **clean rebuild** with `{"intact":true}` and its
review comment posted via the per-run `GitHubToolset` bound to `run.Repo` — not any startup config
(there is none anymore). A non-allowlisted and a malformed repo are both refused before any tool
runs.

## "Any allowlisted repo" and search — evidence basis

The **mechanism** (per-run factory + allowlist + confined search) is demonstrated live with one
repo and covered by comprehensive offline tests exercising multiple repos and owners
(`acme/widgets` + `acme/tools` + an out-of-scope `evil/secrets` that never surfaces in search),
wildcard matching, factory binding, and every fail-closed path. A **second live** allowlisted repo
is confirmatory, not necessary — it needs a broader PAT (a token-scope item the spec defers to "a
GitHub App with selected-repository installation when more repos join"). No shipped workflow uses
search yet; the spec adds the *capability* (a search-using workflow is an invariant-6 data change),
so this is not a gap.

## Regression

pr-review (read-only) still completes with the chain intact through the new per-run path — the
startup `GitHub:Owner/Repo` binding is retired and the repo now flows from the request.

## Tests

292 offline unit tests (policy 142, tools 49, engine 31, audit 31, runner 17, agents 9, eval 13).
`dotnet build` clean, `dotnet test` green. New: allowlist admit/deny/wildcard/malformed/empty;
search confinement + fail-closed + bounds; factory bind + fail-fast; fork-guard rejection.

## Residuals carried forward (see `REVIEW.md`, `docs/threat-model.md`)

- **Wildcard `owner/*` entries are inert for code search** (fail-closed, no leak) — documented in
  the allowlist config; full fix (expand to a `user:` qualifier) is a follow-up.
- **No GitHub App auth** — the PAT stays; the App with selected-repository install is the
  "when more repos join" future item, and is what a real second-repo demo would use.
- Pre-existing, unchanged: no API auth + caller-supplied initiator (F1), runner egress (F11),
  least-privilege DB role (F4).

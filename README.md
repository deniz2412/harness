# Harness

Bank-grade AI coding harness on Microsoft Agent Framework (.NET 8). Declarative YAML workflows
drive agents through plan → act → validate → PR with policy gates and a hash-chained audit trail.
Workflows end at opening a PR — a merge operation does not exist in this codebase, by design.

Design docs: `../AI-Harness-Analysis-and-Plan.md`, `../Option-B-Harness-Platform-Design-Spec.md`.
Working context for AI assistants: `CLAUDE.md`.

## Quickstart

```bash
cp .env.example .env          # fill in ANTHROPIC_API_KEY + GITHUB_TOKEN
# set GitHub owner/repo in src/Harness.Api/appsettings.json
docker compose -f docker/compose.yaml up --build
# trigger a review:
curl -X POST localhost:8080/runs -H "Content-Type: application/json" \
  -d '{"workflow":"pr-review","repo":"you/test-repo","pr":1}'
# inspect: GET /runs/{id} · /runs/{id}/events · /runs/{id}/verify (audit chain)
```

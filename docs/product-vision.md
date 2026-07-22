# Harness — Product Vision & Extended Roadmap

**From AI harness to developer suite**
**Companion to:** Option-B-Harness-Platform-Design-Spec.md (the engineering contract for M0–M4)
**Date:** 22 Jul 2026 · **Status:** Vision draft v1

---

## 1. The vision

The harness stops being "a PR review bot" and becomes a **developer suite**: a platform where
teams run, monitor, and *author* governed AI workflows across the whole engineering lifecycle —
implementation, QA, security testing — through a real frontend, with team/organization-level
workflow ownership.

The pitch in one sentence: **GitHub Actions gave teams deterministic CI they own as code; Harness
gives teams governed AI workflows they own the same way — plus a workbench to run and write them.**

Nothing in this vision replaces the current milestone discipline. M0→M2 build the engine this
suite runs on; everything here layers on top of seams that already exist.

---

## 2. Why the current architecture already supports this

Three design decisions made early are the reason this vision is an extension, not a rewrite:

1. **Workflows are data (YAML), not code.** A QA workflow or a security-testing workflow is a new
   YAML file + prompts — zero engine changes. Team-level workflows are just *namespaced* YAML.
2. **Node kinds are few and composable.** `agent`, `agent-loop`, `bash`, `gate` already express
   implementation loops, deterministic test runs, and human checkpoints. New lifecycle stages
   rarely need new node kinds — they need new *tools* and *prompts*.
3. **Everything emits audit events.** A frontend is largely a *read model* over `runs` +
   `run_events` — the data the UI needs has been accumulating since M0.

---

## 3. The workflow catalog (target state)

| Stage | Workflows | Mostly needs |
|---|---|---|
| **Review** | `pr-review` (M0 ✅), `pr-security-review` | prompts |
| **Implementation** | `issue-to-pr` (M2), `refactor-safely`, `resolve-conflicts` | write path + gates (M2) |
| **QA testing** | `test-generation` (M2), `coverage-gap-analysis`, `regression-suite-author`, `flaky-test-hunter` | bash node + test-runner images |
| **Security testing** | `dependency-audit`, `secrets-sweep`, `sast-triage`, `threat-model-draft` (STRIDE-style) | analyzer tools in runner containers |
| **Ops/hygiene** | `docs-sync`, `changelog-draft`, `stale-issue-triage` | prompts + GitHub tools |

Security-testing scope note: these workflows are **defensive and repo-scoped** — scanning and
triaging *your own* code (dependency CVEs, secret leaks, static-analysis findings, threat-model
drafts). The harness does not become an offensive tooling platform; that stays out of the tool
layer the same way merge and repo-creation did.

QA/security workflows lean on the `bash` node running **pinned analyzer images** (test runners,
dependency scanners, SAST tools) in isolated runner containers — deterministic tools produce
findings; agents *triage, explain, and propose fixes* as gated PRs. That division (deterministic
detection, AI judgment, human approval) is the pattern for the whole suite.

---

## 4. Team/organization workflow ownership

The model, in increasing order of ceremony — all git-backed, because git *is* the governance:

- **Catalog (built-in):** workflows shipped with the platform (`workflows/defaults/`), reviewed by
  the platform owner. Teams run them as-is.
- **Team workflows:** each team gets a namespace — `workflows/teams/<team>/*.yaml` in a governed
  repo (or per-team repos later). Adding/changing a workflow = PR with review, which doubles as
  MRM change control. Same-named team workflow overrides a default (the Archon override pattern).
- **Org policy floor:** a `policy.yaml` per org/team defines what workflows *may* do — allowed
  tools, repo allowlists (M3), gate requirements ("any `github.open_pr` requires `gate: human`"),
  budget caps. The engine validates every workflow against the policy floor at load time: teams
  can author freely *within* a ceiling the org sets. This is the piece that makes self-service
  safe in a bank.
- **Versioning:** already free — runs pin the workflow git SHA in the audit trail (M0 design).
- **Named agent registry (long-term goal):** today every `agent` node defines its agent inline
  (prompt + tools + model tier). The registry makes agents first-class: define
  `agents/<name>.yaml` once — persona prompt, allowed tools, model tier, output schema — and
  reference it from any workflow via `agent_ref: security-reviewer`. Teams then own *agents* the
  same way they own workflows: namespaced, PR-reviewed, versioned, validated against the org
  policy floor. Lands with M7 (ownership model) and is authorable in the F3 workbench. Agents
  remain bounded to runs — no standing autonomous agents; that stays a deliberate exclusion.

---

## 5. The frontend: a developer suite in four increments

Build order chosen so each increment is small, immediately useful, and mostly reads existing data.
Stack suggestion: Blazor (stays all-.NET, one deploy) or a small React SPA on the existing API —
decide at F1, not before.

**F1 — Operations console** *(pull-forward of the M4 "thin ops page"; sensible right after M2)*
Run list + live status, run detail (node timeline from `run_events`), audit-chain viewer with
verify button, **gate approval screen** (diff view, approve/reject) — the first UI with real
operational value, and token/cost per run. ~90% reads; writes only the gate decision.

**F2 — Workflow catalog & launcher**
Browse catalog and team workflows (parsed from YAML), workflow detail (DAG rendered from
`depends_on`, permissions, gates), launch form (repo/PR/issue params), run history + success
rates per workflow.

**F3 — Authoring workbench**
YAML editor with schema validation + policy-floor checks *before* commit; "dry-run" (validate +
render DAG, no execution); PR-based publishing flow to the team namespace (the UI drives git, it
does not bypass it). Prompt editing with versioned diffs.

**F4 — Suite dashboards & visual builder**
Org/team dashboards: spend, run volumes, gate latency, eval scores over time (M2's golden-run
harness feeds this). Only here, if still wanted: a visual drag-and-drop workflow builder that
*emits the same YAML* — sugar over the artifact, never a second source of truth.

Standing rule from the spec, restated: the UI is a client of the API and of git. If the frontend
can do something the API + audit trail can't account for, that's a bug in the frontend's scope.

---

## 5a. Tool extensibility — the MCP connector layer

Honesty note: M0 implements tools as in-process C# toolsets behind MCP-shaped contracts; true MCP
pluggability is the planned extension. The customization ladder for developers:

1. **Compose (now):** pick tools per node in workflow YAML from the curated catalog. New catalog
   tools (e.g. `github.create_issue` — a draft-shaped write, gate-eligible, unlike the permanently
   excluded merge/repo-create) are added by platform PR: one method + registry entry + gate rule.
2. **Contribute (M7):** teams author workflows and agents against the catalog; the org policy
   floor scopes which tools each team may use. New tools arrive as reviewed PRs to the platform.
3. **Connect (new — "M7c"):** the platform mounts **external MCP servers** as namespaced toolsets
   (`jira.*`, `sonar.*`, internal team servers), declared in configuration with an explicit
   per-operation allowlist. A new toolset becomes config + review instead of code. Team-supplied
   MCP servers go through an approval flow (vendor/supply-chain review for third-party ones).

Permanent boundary: **developers compose and request tools; the platform approves and mounts
them.** Unreviewed MCP servers never attach to write-capable agents — tools are the agent's hands,
and the connector allowlist is what keeps extensibility compatible with the audit and policy
story. Every mounted operation is logged per call like any built-in tool.

## 5b. Beyond development — business workflow packs (long term)

The engine is not actually code-specific. Its real primitives are: a DAG of agent/deterministic
steps, a permission ceiling, human gates before anything externally visible, and a hash-chained
audit trail. Swap "repo" for a document store and "open a PR" for "submit a draft for approval,"
and the same machinery runs governed business workflows. The division of labor stays identical:
**deterministic tools extract/detect, agents draft/triage/explain, humans approve.**

Candidate packs for a bank, in rough order of feasibility:

| Pack | Example workflows | New tools needed |
|---|---|---|
| **Compliance & regulatory** | regulation-change impact analysis (new circular → affected policies map), policy-document drafting, control-evidence collection for audits | document store read, policy repo |
| **Risk & governance** | MRM model-documentation drafting, vendor-review triage (questionnaire → findings summary), risk-report first drafts | document store, forms/questionnaire ingest |
| **IT ops** | incident postmortem drafting from tickets/logs, runbook generation and drift-checking, change-request summarization | ticket system, log store (read-only) |
| **Internal knowledge** | onboarding-guide maintenance, cross-system documentation sync, FAQ generation from resolved tickets | wiki/SharePoint read-write-draft |

Hard boundaries that make this defensible rather than reckless:

- **No customer data until graduation.** These packs start only after M8 (real infra, Vault, SIEM,
  SSO) and a data-classification review — internal documents first, customer/PII workloads last
  or never.
- **Same write ceiling.** Business workflows end at a *draft for human approval* (document PR,
  ticket comment, review request) — never at filing, submitting, or sending anything themselves.
- **Same policy floor.** A compliance team authors workflows inside an org-defined ceiling exactly
  as a dev team does; the audit chain is what makes an AI-drafted document traceable end-to-end.

This is deliberately the *last* horizon: the development suite proves the platform with the most
verifiable domain (code has tests; prose does not). Business packs inherit a hardened engine
rather than beta-testing it.

## 6. Revised roadmap (engineering contract + suite)

| Milestone | Content | Status |
|---|---|---|
| M0 | Walking skeleton — pr-review end-to-end, audited | ✅ (final verification in progress) |
| M1 | Governance hardening — gates, secret ruleset, migrations, budgets | next |
| M2 | Write path — agent-loop/bash/gate, test-generation, issue-to-pr, eval harness | planned |
| M3 | Multi-repo & search — per-run tooling, repo allowlist, read-only search | planned |
| **F1** | Operations console (runs, audit viewer, gate approvals) | after M2, parallel with M3 |
| **M5** | QA workflow pack — coverage-gap, regression-author + analyzer runner images | new |
| **M6** | Security workflow pack — dependency-audit, secrets-sweep, sast-triage, threat-model-draft | new |
| **M7** | Team workflow namespaces + org policy floor (policy.yaml validation) | new |
| **F2–F3** | Catalog/launcher, then authoring workbench | after M7 (F3 needs the policy floor) |
| **M7b** | Named agent registry — first-class, team-owned agent definitions (`agent_ref`) | with M7/F3 |
| **M7c** | MCP connector layer — mount external MCP servers as allowlisted toolsets (§5a) | after M7 |
| M4/M8 | Graduation — real infra (OpenShift, Vault, SIEM, SSO), multi-team operation | last |
| **F4** | Dashboards + (optional) visual builder | last |
| **M9+** | Business workflow packs (compliance, risk, IT ops, knowledge) — §5b, post-graduation only | horizon |

Guardrail update: the spec's "no visual builder ever unless demanded" is now "demanded" — it moves
to F4, i.e. *last*, and only as a YAML-emitting layer. The anti-scope-gravity rule still holds:
nothing from F2 onward starts before the write path (M2) is real and gated.

---

## 7. What this changes right now

Almost nothing — by design. The current session still targets: green M0 run → M1 hardening.
The two concrete near-term effects: F1 is now an approved pull-forward the moment M2's human
gates exist, and new workflows should be written with the future `workflows/defaults/` vs
`workflows/teams/<team>/` split in mind (flat layout is fine until M7).

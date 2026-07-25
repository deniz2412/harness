# Harness — Product Vision & Extended Roadmap

**From AI harness to developer suite**
**Companion to:** Option-B-Harness-Platform-Design-Spec.md (the engineering contract for M0–M4)
**Date:** 22 Jul 2026 · **Status:** Vision draft v3 — adds §5c (upstream SDLC suite + capped
cascade), §5d (shared knowledge base + multi-repo workspaces), T1 line comments, P0–P3 pipeline,
K1/W1. Engine is at M6 done; M7 next.

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

## 5c. Upstream SDLC — the analysis, stories & spec suite

The harness so far starts at *code* (review, test, implement a PR). This extends it **upstream**:
from a requirement or epic, through business/solution analysis, to user stories, to a
human-approved design spec, which then **hands off** to the implementation workflows that already
exist. The whole SDLC becomes a chain of governed, audited, human-gated runs — the same engine,
pointed at earlier stages.

The three product ideas driving this:

1. **Task a user story to an agent (UI intake).** A frontend surface where you write or paste a
   user story and dispatch it — it launches the implementation pipeline (`issue-to-pr` and kin)
   against a chosen repo. This is F2/F3's launcher plus a story-shaped entry form and a
   **pipeline view** tracking story → spec → PR status.
2. **Business/Solution-Analysis pack (upstream of stories).** BA/SA agents take an epic or
   requirement and **decompose it into user stories** — created as GitHub Issues (draft-shaped
   write, the already-planned `github.create_issue`), human-gated before anything is filed. Those
   issues become the work items other agents pick up. Analysis is a *drafting* act: the agent
   proposes stories/acceptance-criteria; a human curates and approves.
3. **Story → spec → implementation handoff (the flagship flow).** You add a story; a spec agent
   works *with you iteratively* to produce a design spec; when you're satisfied, it **hands off**
   to the implementation agents. This is the end-to-end loop, and it needs two new engine
   primitives below.

### New engine primitives this requires

- **Interactive refinement loop.** Current gates are approve/reject. Spec authoring needs a third
  path: **revise-with-feedback** — the human returns comments, the agent revises and re-presents,
  looping until approved. Modeled as an extension of `agent-loop` + `gate` (`interactive: true`,
  `until: approved`), bounded by `max_iterations` and budget. Every iteration is audited; the
  approved artifact pins a content hash, exactly like workflows do.
- **Workflow handoff / chaining.** Today a run is one workflow. Handoff lets an *approved* run
  spawn downstream runs (spec approved → create the implementation run(s) for its stories).
  Modeled as a `handoff` node kind — but with a hard rule below.

### Cascade governance (agents creating work for agents — capped, not forbidden)

**Decision:** autonomous handoff *is allowed* — an agent may create work that dispatches to other
agents without a human gate at every boundary — but it is bounded by a **cascade budget** the
engine enforces. This makes "agents creating work for agents" a first-class capability while
keeping runaway work-generation (the top risk here) structurally impossible.

The **cascade budget** is a set of caps carried on the originating run and inherited by every
descendant, defined in the org/team `policy.yaml` (M7) with per-workflow overrides *downward only*
(a workflow may tighten but never loosen the org ceiling):

- **`max_depth`** — how many handoff hops deep the chain may go (e.g. spec → implementation = 1;
  spec → implementation → auto-fix = 2). Default small (e.g. 2).
- **`max_fanout`** — how many downstream runs a single handoff may spawn (a spec that decomposed
  into 30 stories with `max_fanout: 5` launches at most 5; the rest wait for explicit dispatch).
- **`max_total_runs`** — hard cap on runs in the whole cascade tree, regardless of depth/fanout.
- **`cascade_budget_usd`** — total spend cap across the tree; when hit, the cascade halts
  fail-closed and surfaces for human continuation.
- **`gate_policy`** — per stage-boundary, one of `auto` (cascade proceeds within budget),
  `human` (this boundary always requires approval), or `human_over(N)` (auto up to N downstream
  runs, human approval beyond). Lets the org keep, say, spec→implementation automatic but
  story-creation→spec human, tuned to appetite.

Enforcement: caps are checked by the engine at every `handoff` node *before* spawning; exceeding
any cap halts fail-closed (invariant 2) and creates a human-continuation point. Every spawn emits
an audit event carrying the cascade root id, depth, and remaining budget — so the whole tree is
one traceable, attributable lineage.

### Other invariants preserved

- **Same write ceiling.** Analysis ends at *draft issues*; spec authoring ends at a *spec document*
  (a PR to a docs repo); implementation still ends at a PR. Nothing files, assigns, or merges
  autonomously. Bounded runs only — no standing agents; a cascade is a finite tree, not a daemon.
- **Reuses everything.** Stories are GitHub Issues; specs are Markdown PRs; handoff is gated run
  creation. No new persistence model, no new trust boundary — just new workflows, one node kind,
  one loop mode, the cascade-budget accounting, and UI.
- **Default posture is conservative.** Ship with tight defaults (`max_depth: 2`, small
  `max_fanout`, a modest `cascade_budget_usd`) and let teams raise them within the org ceiling —
  a bank tenant can even pin `gate_policy: human` everywhere, reducing to the fully-gated model;
  a personal/PoC tenant can run fully automatic within budget.

### Why it's a later horizon
The interactive spec-authoring UX genuinely wants a good frontend (a chat-like refinement panel,
not curl), so the full suite lands after the authoring workbench (F3). The two engine primitives
(refinement loop, handoff) are independent of M7 and could land earlier if you want to prototype
the flagship flow sooner — but the *pleasant* version is post-F3.

## 5d. Multi-repo microservices & a shared knowledge base

Two requested capabilities that reinforce each other: agents that work to a **unified standard**
across a codebase, and agents that **operate across multiple repositories** (a microservices
system). Both build on primitives already in the platform.

### Part A — Shared knowledge base (the unified standard)

A new **trusted-context** primitive: **knowledge sets** — authored Markdown (coding standards,
architecture principles, API/versioning conventions, ADRs, naming rules) versioned in git and
PR-reviewed. Scoped org / team / workspace, referenced from a workflow or agent definition
(`knowledge: [org-standards, payments-team]`) and injected into the agent's context so every run
applies the same conventions.

The important design point is a **new trust category**. Invariant 4 says repo and issue content is
*untrusted* (agents must not obey instructions embedded in it). Knowledge sets are the opposite:
they are a **trusted instruction source** — precisely because they pass the same review gate as
workflows and prompts (a change to a standard is a reviewed PR). Prompts must still *delimit*
trusted standards from untrusted repo content clearly, so the two never blur. This keeps
"the agent follows our standards" and "the agent ignores injected instructions in a diff"
simultaneously true.

Knowledge (soft guidance the agent *should* follow) stays distinct from `policy.yaml` (hard rules
the engine *enforces*). They complement: the knowledge base shapes *how* code is written; the
policy floor bounds *what* a workflow may do. A standard that must never be violated graduates from
knowledge into policy.

Scale path, in order: ripgrep over a knowledge repo first (consistent with today's `codesearch`);
embeddings/vector retrieval only if that proves insufficient (still behind the "no embeddings until
needed" guardrail); external corpora (Confluence, SharePoint, Notion) mounted read-only via the
MCP connector layer (M7c) rather than copied in.

### Part B — Multi-repo workspaces

A **workspace** is a named, allowlisted **set of repos** that form one system (the microservices
group) plus its shared knowledge base. It extends M3: where M3 scoped a run to a single allowlisted
`run.Repo`, a workspace lets a run target the *group*, with tools repo-parameterized within the
workspace allowlist and a per-repo worktree each.

Write ceiling preserved and extended: a coordinated change opens **one PR per affected repo**, each
independently human-gated — never a cross-repo atomic merge (that stays impossible, by extension of
the no-merge invariant). Per-repo permission ceilings still apply.

Cross-repo changes ride the **cascade/handoff** primitive (P0): a workspace-level run (e.g. "bump
the shared contract, update all consumers") fans out to per-repo implementation runs, bounded by
the same cascade budget (`max_fanout`, `max_total_runs`, spend cap). This *is* the microservices
"change a shared interface and propagate to every service" flow — governed, capped, and audited as
one lineage, ending in a reviewable PR per service.

Together: the workspace's knowledge base is what makes the fanned-out per-repo work *consistent* —
every service's PR is written against the same standards, so multi-repo changes don't drift.

## 6. Revised roadmap (engineering contract + suite)

| Milestone | Content | Status |
|---|---|---|
| M0 | Walking skeleton — pr-review end-to-end, audited | ✅ done |
| M1 | Governance hardening — gates, secret ruleset, migrations, budgets | ✅ done |
| M2 | Write path — agent-loop/bash/gate, test-generation, issue-to-pr, eval harness | ✅ done (live gated PR) |
| M3 | Multi-repo & search — per-run tooling, repo allowlist, read-only search | ✅ done |
| **F1** | Operations console (runs, audit viewer, gate approvals) | ✅ done (Blazor Server) |
| **M5** | QA workflow pack — coverage-gap, regression-author + analyzer runner images | ✅ done |
| **M6** | Security workflow pack — dependency-audit, secrets-sweep, threat-model-draft (sast-triage deferred) | ✅ done (3 of 4; deps-audit + secrets-sweep live, threat-model gated-PR demo deferred on credits) |
| **M7** | Team workflow namespaces + org policy floor (policy.yaml validation) | ✅ done (override + floor validation live without the gateway; boot-sweep enforced; full pr-review regression deferred on credits) |
| **F2** | Workflow catalog & launcher (browse, DAG detail, launch, per-workflow stats) | ✅ done (Blazor on the F1 console; demonstrated live) |
| **F3** | Authoring workbench — YAML editor + policy-floor checks before commit, dry-run, PR-based publish | **next** (needs the M7 policy floor, shipped) |
| **M7b** | Named agent registry — first-class, team-owned agent definitions (`agent_ref`) | ✅ done (agent_ref resolution + agent-pinned sha + team overrides live without the gateway; full run deferred on credits) |
| **M7c** | MCP connector layer — mount external MCP servers as allowlisted toolsets (§5a) | ✅ done (config-declared, allowlisted toolsets mounted through the audited seam + write-capable boundary; in-process stub transport, real MCP client a drop-in; governance pinned offline) |
| **T1** | Inline PR line comments — `github.pr_review_comment` (line-anchored), catalog tool, `pr-review` upgraded to post per-line findings + a summary | small, do anytime (see below) |
| **P0** | Engine primitives — interactive refinement loop (`interactive`/revise-with-feedback) + `handoff` node kind (gated, capped) | after M2; enables P1–P3 |
| **P1** | Analysis pack — `epic-to-stories` / `story-refinement` BA/SA workflows → gated GitHub Issues (needs `github.create_issue`) | after P0 |
| **P2** | Interactive spec authoring — `story-to-spec` with the refinement loop → approved spec as a docs PR | after P0 + F3 (wants UI) |
| **P3** | Story→spec→implementation handoff + UI intake/pipeline view (the flagship flow) | after P1, P2 |
| **K1** | Shared knowledge base — trusted, org/team/workspace-scoped standards injected into agents (§5d.A) | with/after M7 |
| **W1** | Multi-repo workspaces — allowlisted repo groups, per-repo worktrees + PRs, cross-repo via cascade (§5d.B) | after M3 + P0 |
| M4/M8 | Graduation — real infra (OpenShift, Vault, SIEM, SSO), multi-team operation | deferred (last) |
| **F4** | Dashboards + (optional) visual builder | last |
| **M9+** | Business workflow packs (compliance, risk, IT ops, knowledge) — §5b, post-graduation only | horizon |

**T1 note (inline line comments):** currently `pr-review`'s `post` node uses `github.pr_comment`
(one issue-level comment). Adding `github.pr_review_comment` — anchored to file + line/position via
the PR review API — lets the agent attach findings to the exact lines, optionally batched into a
single review submission. It's a draft-shaped, gate-eligible catalog tool (curated addition, one
method + registry entry + gate rule), and `review-findings.json` already carries `file`/`line`, so
the reviewer output maps straight onto it. Independent of everything else — a good small win to
slot whenever, e.g. alongside M7 or as a warm-up before P0.

Guardrail update: the spec's "no visual builder ever unless demanded" is now "demanded" — it moves
to F4, i.e. *last*, and only as a YAML-emitting layer. The anti-scope-gravity rule still holds:
nothing from F2 onward starts before the write path (M2) is real and gated.

---

## 7. What this changes right now

Almost nothing — by design. The current session still targets: green M0 run → M1 hardening.
The two concrete near-term effects: F1 is now an approved pull-forward the moment M2's human
gates exist, and new workflows should be written with the future `workflows/defaults/` vs
`workflows/teams/<team>/` split in mind (flat layout is fine until M7).

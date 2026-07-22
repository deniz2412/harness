# AI Harness for Software Engineering — Analysis & Delivery Plan

**Organization:** Bank (regulated) · **Anchor use case:** internal developer coding agents
**Author:** AI Architecture · **Date:** 22 Jul 2026 · **Status:** HISTORICAL — superseded by
Option-B-Harness-Platform-Design-Spec.md (engineering contract) and
Harness-Product-Vision-Roadmap.md (long-term direction). Outcome: Option B chosen, built as a
personal PoC on Docker Desktop; Archon parallel pilot consciously dropped.

---

## 1. Executive summary

We will stand up an **AI harness** — the engineering scaffolding that turns a raw LLM into a
governed, agentic system — anchored on **internal developer coding workflows**. GitHub Copilot
already gives developers inline completion; the harness adds the layer above it: **multi-step
agentic tasks** (plan → implement → validate → review → PR) that need tool access, guardrails, and
a full audit trail.

Rather than pick one product up front, we will run a **structured bake-off across four paths** and
converge on one for production. The end-state you described is consistent across all four: *a
developer authenticates or supplies a key, and the harness uses those tokens to do the work
against our repos and tickets.*

**The four paths**

1. **Open source** — **Archon** (open-source harness/workflow engine for AI coding) running on our
   OpenShift, driving Claude Code with our existing Anthropic API key.
2. **AWS Bedrock** — **Strands Agents** deployed to **Amazon Bedrock AgentCore Runtime**.
3. **Microsoft Foundry** — **Microsoft Agent Framework (MAF)** hosted on **Azure AI Foundry**.
4. **Own .NET PoC** — a bank-owned agent built on **Microsoft Agent Framework 1.0 (.NET)**,
   self-hosted on OpenShift.

**Recommended sequencing given "pilot first, no cloud, use what we have":** start the pilot on
**Path 1 (Archon)** — it runs entirely on OpenShift, reuses the Claude API key developers already
use, and is purpose-built for exactly this (deterministic AI-coding workflows). Scope **Path 4
(.NET MAF PoC)** as the parallel "what we would actually own and operate" track, since you lean
.NET. Treat **Paths 2 and 3** as cloud-hosting options we evaluate *after* the concept is proven,
when the provider/cloud decision is due.

> **One overlap to flag up front:** Path 3 and Path 4 use the **same framework** — Microsoft Agent
> Framework. The difference is *hosting and ownership*, not technology: Path 3 runs MAF agents as
> **hosted/managed** agents on Azure AI Foundry; Path 4 runs the **same MAF .NET code** on our own
> OpenShift. So "Foundry" vs "own .NET PoC" is really one framework, two deployment targets — which
> is convenient: a PoC built on MAF can later be lifted onto Foundry with limited rework.

---

## 2. Our stack (constraints the harness must fit)

| Component | Our environment | Implication for the harness |
|---|---|---|
| On-prem compute | **OpenShift** (Kubernetes) | All paths must be containerizable; favors OpenShift-native deploy over managed-only tools |
| Version control | **GitHub** | Native fit for all four; agents open PRs, never auto-merge |
| Ticketing | **Jira** | Integrate via MCP/tools so agents can read tickets and post results |
| Model access today | **Copilot** + **Claude via API key** | Reuse the Claude key for the pilot; no new cloud model host needed to start |
| Cloud | Multi-cloud / on-prem heavy, provider **undecided** | Keep provider choice reversible; abstract model access behind a gateway |
| Scale | Under 100 developers | Pilot-sized; avoid premature platform engineering |
| Priorities | Compliance & audit, security, time-to-value, cost/scale — **all four** | Reconcile via a thin, read-mostly first slice with an audit trail from day one |

The pilot deliberately avoids standing up **new** cloud infrastructure (Bedrock/Foundry). It uses
**existing** OpenShift compute plus the **existing** Anthropic API key — so "no cloud cost" means
no new managed-service spend; token usage is the same line item you already have.

---

## 3. The four paths compared

| | **1. Archon (OSS)** | **2. Strands + Bedrock AgentCore** | **3. MAF on Azure Foundry** | **4. Own .NET PoC (MAF)** |
|---|---|---|---|---|
| **What it is** | Open-source workflow engine for AI coding; YAML-defined dev workflows, git-worktree isolation | AWS open-source agent SDK + serverless AgentCore runtime | Microsoft's GA agent framework, hosted on Azure AI Foundry | Same MAF framework, self-hosted, bank-owned |
| **License / ownership** | MIT, self-hosted | Apache-style OSS SDK; runtime is AWS-managed | OSS framework; hosting is Azure-managed | OSS framework; fully bank-owned |
| **Runs on OpenShift?** | **Yes** (Docker image; containers) | SDK yes; AgentCore runtime is AWS-only | Framework yes; Foundry hosting is Azure | **Yes** (.NET containers) |
| **Model source** | Drives **Claude Code** → our Anthropic key | Any provider via Bedrock (Anthropic, Nova, etc.) | Azure/Foundry catalog (+ Anthropic via Foundry) | Any provider incl. Anthropic key or on-prem models |
| **Cloud cost to pilot** | **None new** (OpenShift + existing key) | AWS account + AgentCore + Bedrock spend | Azure + Foundry spend | **None new** (OpenShift + existing key) |
| **Built-in governance** | Deterministic workflows, human-approval gates, worktree isolation | Guardrails, PII redaction, evals SDK, red-teaming, session isolation (microVMs) | Azure policy, identity, content safety, Foundry observability | Whatever we build (full control) |
| **GitHub / Jira fit** | Native GitHub adapter; Jira via tools | Via MCP tools | Via MCP tools | Via MCP tools we write |
| **Language** | TypeScript/Bun | Python / TypeScript | .NET / Python | **.NET** (our preference) |
| **Best as** | **Fast cloud-free pilot** | Cloud production option (AWS estate) | Cloud production option (Azure estate) | **The thing we own long-term** |
| **Main risk** | OSS maturity; drives an external CLI | AWS lock-in; new cloud spend/approval | Azure lock-in; new cloud spend/approval | We build & maintain more ourselves |

**Reading of the table.** Paths 1 and 4 are the two that satisfy "no new cloud, use what we have."
Path 1 is the fastest way to *prove value*; Path 4 is the most defensible way to *own the
capability*. Paths 2 and 3 are hosting decisions to make later — and because Strands (2) and MAF
(3/4) are both provider-agnostic, choosing a cloud does not force us to throw away pilot work.

---

## 4. Target architecture (common across paths)

Whichever engine wins, the surrounding layers stay the same — this is what makes the bake-off fair
and the eventual choice reversible.

- **Model access / gateway.** One internal endpoint every agent calls (start by pointing it at the
  Anthropic API with the existing key; later it can fan out to Bedrock/Foundry/on-prem models).
  This is the layer that keeps the provider decision reversible and centralizes logging, budgets,
  and PII redaction.
- **Orchestration / agent loop.** The plan→act→observe→review loop. In Archon this is YAML
  workflows; in MAF it's the framework's workflow/agent constructs; in Strands it's the SDK's agent
  loop. All support human-approval gates.
- **Context & tools (MCP).** Read-only repo access, code search, GitHub, and **Jira** exposed as
  MCP tools so each call is logged and permissioned. Start read-only; add "open a PR" (never merge)
  only once guardrails are proven.
- **Guardrails & policy.** Secret/PII detection, tool/repo allow-lists, and **mandatory
  human-in-the-loop on any write**. Bank-specific — we own this layer regardless of path.
- **Observability, eval & audit.** Full run tracing (prompt, tool calls, outputs, cost), an
  offline eval harness, and an **immutable audit log** wired to existing SIEM/compliance tooling.
- **Identity & access.** Agents act under scoped SSO identity, least-privilege repo/tool scopes,
  secrets from the existing vault — never in prompts.
- **Developer surfaces.** A **PR bot** (recommended first surface), a CLI, and later IDE
  integration alongside Copilot.

---

## 5. Governance & compliance

Because this is a bank, the harness must be defensible from the first pilot, not retrofitted.

- **Model risk (SR 11-7 / MRM):** treat each agent workflow as a model with an owner, documented
  purpose, validation evidence (the eval harness), and monitoring. Version prompts and workflows.
- **Data handling & residency:** for the pilot, code is sent to the Anthropic API under the
  existing agreement — confirm the data-processing terms cover repo content before go-live. PII
  redaction at the gateway; keep customer data out of pilot scope.
- **Auditability:** immutable, queryable log of every run and tool call, retained per policy.
- **Human-in-the-loop:** no autonomous writes; every code change is a human-approved proposal.
- **Security / threat modeling:** run a **STRIDE threat model** on the harness data flows before
  the pilot goes live (there is a project skill for this). Design first against **prompt injection
  via repo content**, **tool-permission escalation**, and **secret exfiltration**.
- **Third-party risk:** the gateway is the single control point for provider terms, and for Archon
  note its anonymous telemetry ping (`workflow_invoked`) — **disable it** (`DO_NOT_TRACK=1`) for a
  bank deployment.

---

## 6. Delivery plan

**Phase 0 — Foundations (Week 0–1).**
Pick the pilot cohort (5–10 developers, 2–3 non-sensitive repos) and success metrics (e.g., % of
bot review comments rated useful; time-to-first-review). Stand up **Archon on OpenShift**, wired to
the existing Anthropic key, telemetry disabled. Baseline guardrails; audit-log skeleton. In
parallel, spin up a **.NET MAF** scaffold repo for Path 4.
*Exit:* Archon running on OpenShift against one pilot repo; cohort and metrics agreed.

**Phase 1 — Cloud-free pilot (Weeks 1–3).**
Run Archon's **PR-review workflow** in shadow mode on pilot repos: read-only repo access, human
approval gates, full run tracing. No write access. Collect eval data and developer usefulness
ratings. Begin the STRIDE threat model.
*Exit:* PR bot producing useful reviews, every run auditable, eval baseline established, threat
model drafted.

**Phase 2 — Own-it track + harden (Weeks 4–8).**
Build the **.NET MAF PoC (Path 4)** to replicate the winning Archon workflow — this is the "what we
operate long-term" candidate — and compare it head-to-head with Archon on the same eval set.
Formalize guardrails and audit integration into SIEM. Add a second workflow (test generation, which
opens PRs for human approval — the first *write* action, gated). Complete STRIDE + MRM docs.
*Exit:* two workflows live, write actions gated/audited, Archon-vs-.NET comparison decided,
governance sign-off achieved.

**Phase 3 — Cloud decision & platformize (Quarter 2).**
With value proven, make the hosting call: evaluate **Path 2 (Strands/Bedrock)** and **Path 3
(MAF/Foundry)** against the chosen cloud, data-residency terms, and cost. Because Strands and MAF
are provider-agnostic, the pilot workflows port with limited rework. Add self-serve onboarding,
per-team budgets, and (optionally) an **on-prem open-weight model** as a second gateway backend.
*Exit:* production hosting chosen, provider-flexible, running under a documented operating model.

---

## 7. Risks & mitigations

- **Path overlap misread as four separate builds** → treat MAF as one framework (Paths 3 & 4);
  budget for two builds (Archon + MAF), not four.
- **Prompt injection via repo content** → untrusted-by-default repo content; constrained tools;
  STRIDE before go-live.
- **Scope creep into writes too early** → read-only until guardrails proven; writes only as
  human-approved PRs.
- **OSS supply-chain / telemetry (Archon)** → vendor-review the dependency tree; disable telemetry;
  pin versions; run in an isolated namespace.
- **Cloud lock-in (Paths 2/3)** → defer to Phase 3; keep the gateway abstraction so the model
  source is swappable.
- **Provider data terms** → confirm Anthropic API terms cover source code before the pilot sends a
  single repo.

---

## 8. What I need from you to proceed

1. **Green-light the sequencing:** Archon pilot now, .NET MAF PoC in parallel, cloud (Bedrock vs
   Foundry) decided in Phase 3 — or reprioritize.
2. **Pilot cohort & repos:** who are the 5–10 developers and 2–3 (non-sensitive) repositories?
3. **Anthropic API terms:** confirm the existing agreement permits sending internal source code, or
   tell me who owns that sign-off.
4. **OpenShift access & vault:** which namespace/quota we can use, and the secrets store to pull the
   Claude key from.
5. **Jira details:** instance URL and a service account so I can scope the Jira MCP integration.

Once these are settled I can produce the Phase 0 technical design (OpenShift manifests, Archon
config, gateway sketch) and a concrete task breakdown.

---

### Sources
- Archon — https://github.com/coleam00/archon · https://archon.diy/getting-started
- Strands Agents / Bedrock AgentCore — https://strandsagents.com/docs/user-guide/deploy/deploy_to_bedrock_agentcore/
- Microsoft Agent Framework 1.0 (GA Apr 2026, .NET & Python) — https://learn.microsoft.com/en-us/agent-framework/overview/ · https://devblogs.microsoft.com/agent-framework/microsoft-agent-framework-version-1-0/

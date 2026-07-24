# M7b Exit Check — Report

**Date:** 2026-07-24
**Verdict:** ✅ **PASS** — a named agent registry: agents are first-class, reusable, team-owned
definitions referenced from workflow nodes via `agent_ref`, resolved and content-pinned at load time
and validated against the org policy floor. The registry mechanics were demonstrated **live without
the gateway**; the full end-to-end pr-security-review *completion* run is deferred on gateway credit
exhaustion (M7b changes only the pre-execution resolve/merge path).
**Demonstrated runtime:** `POST /runs {workflow: pr-security-review, team: payments}` resolved to the
`teams/payments/pr-security-review` workflow and pinned the payments agent (distinct sha).

## Criterion

product-vision §4 "Named agent registry": *"define `agents/<name>.yaml` once — persona prompt,
allowed tools, model tier, output schema — and reference it from any workflow via `agent_ref:`. Teams
own agents the same way they own workflows: namespaced, PR-reviewed, versioned, validated against the
org policy floor. Agents remain bounded to runs — no standing autonomous agents."

## What shipped (resolution/merge C# + data; no new node kind)

- **`AgentDefinition`** (`Harness.Contracts`) — Name, Description, PromptRef, Tools, ModelTier
  (`cheap`|`strong`), OutputSchema, Sha.
- **`AgentLoader`** (`Harness.Engine`) — loads/validates one `agents/<name>.yaml` (fail-closed: bad
  tier, missing/escaping prompt, missing name → throw); stamps a content Sha over the agent YAML + its
  prompt, and exposes the tag→bytes digest map (`LoadWithDigests`) so a referencing workflow can fold
  the agent's identity into its own sha — the same content-hash scheme as `WorkflowLoader`.
- **`AgentCatalog`** (`Harness.Engine`) — resolves an agent `(name, team)` with precedence
  `agents/teams/<team>/<name>` → `agents/defaults/<name>` → flat `agents/<name>` (an exact mirror of
  `WorkflowCatalog`; same fail-closed traversal guards).
- **`NodeDefinition.AgentRef` / `ModelTier`** (`Harness.Contracts`) — a node references a named agent
  instead of spelling out prompt/tools/tier/schema inline; the two forms are **mutually exclusive**
  (agent_ref together with any inline prompt_ref/tools/model_tier/output_schema → load-time throw).
- **`WorkflowLoader`** — resolves each `agent_ref` and **merges** the agent's prompt, tools, model tier
  and output schema onto the node (so the executors, which read those node fields, run a referenced
  agent unchanged), and **folds the agent's content into the workflow sha**. The team is the one the
  workflow belongs to (a `teams/<team>/` workflow uses that team's agents; a default/flat workflow uses
  the org defaults) — derived from the stored workflow name, so resume re-resolves the identical agent
  with no persisted team field.
- **`AgentInvoker.ModelFor`** — a node's declared tier (inline or from its agent) now wins; the
  node-id heuristic (`gather`/`plan` → cheap) is the null fallback (back-compat).
- **`Program.cs`** — the boot-time sweep now resolves `agent_ref` workflows AND validates every agent's
  tools against the floor; the process refuses to start on any violation. `agents/` is mounted
  read-only.
- **Data** — `agents/security-reviewer.yaml` (+ prompt), a `payments` team override
  (`agents/teams/payments/security-reviewer.yaml` + prompt), and two workflows that reference the agent
  (`pr-security-review.yaml` flat, `workflows/teams/payments/pr-security-review.yaml` team).

## Demonstrated live (no gateway / no credits needed)

Agent resolution, merge, sha-pinning and team override all run before the gateway is reached:

```
boot  → container starts; sweep resolves agent_ref workflows + validates every agent vs the floor ✓
POST /runs pr-security-review              → run created (agent_ref resolved), run.workflow = "pr-security-review"
POST /runs pr-security-review team=payments → run.workflow = "teams/payments/pr-security-review"
   default run sha 0ad6ac74… ≠ payments run sha ecc9c5f1…  (different agent pinned per run)
```

The differing shas are the key proof: the two runs reference the **same agent name** but resolve
**different agents** (org default vs payments override), and the run's pin reflects exactly which agent
version ran. The boot sweep passing proves resolution + merge + floor validation work in-container.

## Regression

The full end-to-end **pr-security-review completion** run needs the gateway (out of Anthropic credits)
and is **deferred**, same as M7. M7b touches only the pre-execution resolve/merge path — the
agent-execution path is unchanged (the executors read the same node fields, now populated from an
agent) — and a real run was shown to be *created and resolved* correctly (it fails only at the first
model call). Build clean; `docker compose` boots healthy; agent-less workflow shas are byte-identical
to pre-M7b (no pin regression).

## Tests

**442 offline tests, all green** (up from 397): `AgentCatalogTests` + `AgentLoaderTests` (precedence,
override, traversal, bad tier, missing/escaping prompt, deterministic sha) and `AgentRegistryTests`
(every agent within the floor; team override distinct from default; a workflow's `agent_ref` node
inherits the agent's prompt/tools/tier; a team workflow resolves the team agent; referencing an agent
pins it in the workflow sha). The existing eval `Loader()` helpers were updated to pass the agents dir.

## Review gate

Fresh independent audit of the M7b diff: **no MAJORs, no invariant violation, zero scope creep** (no
MCP/M7c, no standing agents, no UI). Confirmed the sha-fold is cleanly tag-deduped (an agent's prompt
that is also a node prompt collapses to one entry) and that agent-less workflow shas are byte-identical
to pre-M7b. One minor **fixed in-milestone**: `output_schema` is now part of the agent_ref/inline
mutual-exclusion (agent_ref fully owns the node's agent config). Two carried (below). Details in
`REVIEW.md`.

## Residuals carried forward (see `REVIEW.md`, `docs/threat-model.md`)

- The full live pr-security-review-completion run is pending an Anthropic credit top-up (spend
  checkpoint); M7b's own mechanics are demonstrated live.
- **Team agent override fires only through a team-namespaced workflow** (agent scope follows workflow
  scope). This is deliberate — it makes resume deterministic without a persisted team field — but a
  `team=X` run of a workflow that has no `teams/X/` override uses the org-default agent.
- The `agents/defaults/` layer is coded + tested (mirroring `WorkflowCatalog`) but unused until an org
  adopts the `defaults/` split.
- `team` is an unauthenticated caller claim until API auth (F1), same trust model as `initiator`.

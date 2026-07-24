# M7c Exit Check — Report

**Date:** 2026-07-24
**Verdict:** ✅ **PASS** — the platform mounts config-declared, allowlisted external MCP toolsets as
namespaced tools through the *same* audited/policed seam as a built-in, with a per-operation allowlist
and a write-capable boundary enforced fail-closed before resolution. The transport is an in-process
stub (the real MCP stdio/SSE client is a deliberate drop-in behind `IMcpConnector`, same posture as
the subprocess-vs-container runner deviation). The mount + audit + all fail-closed refusals are pinned
deterministically **offline**; the live agent-invokes-`docs.search` run is deferred on gateway credit
exhaustion (it exercises the gateway, not the connector governance).

## Criterion

product-vision §5a "Connect (M7c)": *"the platform mounts external MCP servers as namespaced toolsets,
declared in configuration with an explicit per-operation allowlist. A new toolset becomes config +
review instead of code. Permanent boundary: developers compose and request tools; the platform
approves and mounts them. Unreviewed MCP servers never attach to write-capable agents. Every mounted
operation is logged per call like any built-in tool."* Invariant 7 explicitly anticipates this as the
sanctioned extension path — "never attached ad hoc."

## What shipped (governance C# + config + stub transport; no new node kind)

- **`McpConnectorRegistry`** (`Harness.Policy`, alongside the other fail-closed allowlists) — parses
  `connectors.yaml`: declared namespaces, each with a per-operation allowlist and a `write_capable`
  flag. Fail-closed: malformed/dup/blank/reserved namespace, non-identifier name, blank op all throw
  at load; an **absent** file is an empty registry (mounts nothing, not "allow all"); deny-by-default
  lookups. A connector namespace may **not** shadow a built-in toolset (`github`/`repo`/`codesearch`
  are reserved and rejected).
- **`IMcpConnector` + `StubMcpConnector`** (`Harness.Tools`) — the transport seam and an in-process,
  deterministic, no-egress stub (default echo responder; fail-closed on an unadvertised op). A real
  MCP client swaps in behind the interface without touching callers.
- **`PolicyPipeline.AssertToolAllowed`** is now connector-aware: a declared `<namespace>.<operation>`
  tool is governed by the **connector allowlist + write-capable boundary** instead of the curated code
  catalog; built-ins (reserved namespaces) fall through to the catalog path with no collision. The
  **write-capable boundary**: a read-only connector's tools are refused on a node whose ceiling grants
  a write frontier (`repo: write-worktree` or `github: open_pr+issues`, via catalog ranks).
- **`ToolRegistry`** mounts a declared connector op as an `AIFunction` via its `IMcpConnector` — and
  because `Resolve` wraps *every* tool in `AuditedTool`, a mounted op is policy-scanned (pre + post)
  and audited (`tool_call` + `tool_result`) **per call, exactly like a built-in**. No `AuditedTool`
  change was needed — that is why invariants 4 and 5 hold for connector ops for free.
- **`Program.cs`** loads `connectors.yaml` (fail-fast on malformed), builds a stub per declaration, and
  injects the registry into `PolicyPipeline` and the mounted connectors into `ToolRegistry`.
  `connectors.yaml` is mounted read-only.
- **Data** — `connectors.yaml` (one read-only `docs` stub connector: `search`, `get_page`) and
  `pr-review-with-context.yaml` (a read-only review that names `docs.search` alongside built-ins;
  `docs.search` added to the org floor's `allowed_tools`).

## Demonstrated (offline — the connector governance, deterministically)

- **`ConnectorMountTests`** (Harness.Tools.Tests): resolve `docs.search` → it comes back as an
  `AuditedTool` → invoke it → the stub answers AND `["tool_call","tool_result"]` are audited; plus the
  three fail-closed refusals — a read-only connector on a **write-capable node**, an **un-allowlisted
  operation**, and an **undeclared namespace** — all throw before the tool runs.
- **`ConnectorLayerTests`** (Harness.Eval): the shipped `connectors.yaml` declares `docs` read-only
  with its op allowlist; `AssertToolAllowed` admits `docs.search` on a read-only node and refuses the
  write-capable, un-allowlisted, and undeclared cases against the **real** config; and the
  `pr-review-with-context` workflow loads and satisfies the org floor.

## Demonstrated (in-container, credit-free)

- Boot sweep passes: the container loads `connectors.yaml`, makes `PolicyPipeline` connector-aware,
  mounts the `docs` connector, and validates `pr-review-with-context` (which names `docs.search`)
  against the floor at startup.
- `POST /runs pr-review-with-context` is **created** (status Running): `docs.search` passed the floor +
  ceiling checks at `StartAsync`. The actual agent invocation of `docs.search` needs a model call, so
  it is deferred on credits — but the mount + audit + governance it would exercise are all pinned
  offline (a stronger, deterministic proof than a live run for this milestone).

## Regression

The full end-to-end run needs the gateway (out of Anthropic credits) and is deferred, consistent with
M6/M7/M7b. M7c adds a governed tool-resolution path and a stub transport — no change to the model path.
Build clean; `docker compose` boots healthy; every existing workflow's sha and behavior are unchanged
(the connector branch only activates for a declared `<ns>.<op>` name).

## Tests

**505 offline tests, all green** (up from 442): `McpConnectorRegistryTests` (36), `StubMcpConnectorTests`
(13), `ConnectorMountTests` (4 — real mount/invoke/audit + 3 refusals), `ConnectorLayerTests` (6 —
shipped-config governance + floor). `ShippedWriteWorkflowTests` was made **additive** (a tool is valid
if it is a curated built-in OR a declared connector op that `IsAllowed`), not weakened.

## Review gate

Fresh independent audit: **no MAJORs, no invariant violation, zero scope creep** (no network/subprocess
egress, no UI; one read-only stub connector). The reviewer specifically verified the governance crux
(connector path vs catalog path with no collision; the org floor still gates naming) and the
write-capable boundary against the lattice. One minor **fixed in-milestone** (case-insensitive mount
lookup so a name the policy admits can never miss the mount). One carried (below). Details in `REVIEW.md`.

## Residuals carried forward (see `REVIEW.md`, `docs/threat-model.md`)

- **The transport is an in-process stub.** A real MCP client (JSON-RPC over stdio/SSE) is the deferred
  drop-in behind `IMcpConnector`; wiring a real external server introduces network egress (the tracked
  F11 residual) and a vendor/supply-chain approval flow — graduation-era work, out of PoC scope.
- **The write-capable boundary is the two write frontiers** (`repo: write-worktree`,
  `github: open_pr+issues`), so a read-only connector may ride alongside the low-risk auto-gated
  `github.pr_comment` (the demonstrator does). Deliberate and necessary for a read-only-review use of a
  connector; the connector's output is still scanned + audited.
- The full live connector-invocation run is pending an Anthropic credit top-up (spend checkpoint).

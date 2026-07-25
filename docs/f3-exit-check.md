# F3 Exit Check — Report

**Date:** 2026-07-25
**Verdict:** ✅ **PASS (agreed scope)** — an authoring workbench on the console: a YAML editor that
validates a draft against the **same structure + curated-catalog + org-policy-floor** a real run
enforces, renders a **dry-run DAG (never executed)**, and offers a **publish preview** (the target PR
path + a "reviewed PR" message — no git write). Author YAML is validated but **never executed and never
written**. Demonstrated live in-container (the page renders); the validation service is unit-tested
against the real floor/catalog. Real git-publish and prompt-diff editing are deliberately deferred
(see scope note).

## Criterion & confirmed scope

product-vision §5 F3: *"YAML editor with schema validation + policy-floor checks before commit;
dry-run (validate + render DAG, no execution); PR-based publishing to the team namespace (the UI drives
git, not bypasses it); prompt editing with versioned diffs."*

**Human-confirmed PoC scope:** *validate + dry-run + publish-PREVIEW.* The container mounts `workflows/`
read-only and has no governed workflows repo, and a direct write would bypass PR review — so publish
produces the validated artefact + its target path as a preview, with the real git-publish a documented
graduation drop-in (same posture as the stub MCP transport). **Prompt-diff editing is deferred** to a
later increment; the workflow-authoring core is the substantive F3 deliverable.

## What shipped

- **`WorkflowLoader.LoadFromText`** (`Harness.Engine`) — a text-based sibling of `Load`: parses YAML,
  runs the identical structural validation, resolves prompt_refs/agent_refs against the **real**
  prompts/agents dirs (so a dangling ref fails), and stamps a sha. It **executes nothing** and **writes
  nothing**. Additive — `Load`'s body and every existing sha are byte-unchanged.
- **`IWorkbenchService` + `WorkbenchService`** (`Harness.Api/Ops`) — `Validate(yaml, team?)` →
  `WorkbenchResult { Ok, Issues[], Dag?, PublishPath? }`. It: (1) parses+structurally validates via
  `LoadFromText` (structural failures surface one at a time, DAG null); then collects **all** remaining
  issues — **curated-catalog/connector membership** per tool, every **policy-floor** violation, and a
  **dependency-cycle** check (added at review — see below); builds the dry-run **DAG** (topological
  layers, cycle-guarded); and computes the preview **PublishPath**. Pure, read-only: no model, no tool
  execution, no write.
- **`Authoring.razor`** (`/authoring`) — a monospace YAML editor prefilled with a valid template + a
  team field; a "Validate & dry-run" button; a status banner + an issues list (error/warn, with node
  chips); the dry-run DAG rendered exactly like the F2 detail page under a "not executed" header; and a
  publish-preview box (target path + reviewed-PR text + a Publish button that only shows a
  "prepared for publish… (PR creation wired at graduation)" message — **no write, no git, no run**).
- **`Program.cs`** registers the service; **`MainLayout`** gains an Author nav link; **`app.css`** gains
  editor/issue/publish styles.

## Demonstrated

- **Offline (the validation logic, deterministically):** `WorkbenchServiceTests` (10) — a valid review
  (Ok + DAG layers 0/1/2 + publish path); blank; malformed YAML (1 error, null DAG); a dangling
  `depends_on` (structural error); an un-catalogued tool (`github.merge_pr` → catalog error naming
  tool+node, DAG still built); `github.open_pr` without a human gate (floor error) vs with one (clean);
  a `docs.search` connector op on a read-only node (Ok); an `agent_ref` under `team="payments"` (resolves
  the team agent); a bogus `agent_ref` (structural error); and a **dependency cycle** (rejected even
  though structure/catalog/floor all pass).
- **In-container (the page):** `GET /authoring` renders the editor, the prefilled template, and the
  Validate button. The interactive validate/dry-run is a Blazor server interaction (render-verified,
  per the F1/F2 precedent that the UI is a thin client; the logic is unit-tested).

## Review gate + fix

Fresh independent audit confirmed the three highest-risk axes for an authoring UI are clean:
**never-execute** (Validate/LoadFromText/Authoring touch no executor, model, or IRunCoordinator),
**un-bypassable validation** (the workbench holds a draft to the exact floor/catalog a run enforces —
an un-catalogued tool and an ungated write both error), and **no XSS** (no `MarkupString` on any
author-derived value; Blazor auto-encodes). `LoadFromText` is additive and does not regress `Load`'s
sha. **No MAJORs.** One MINOR **fixed in-milestone**: cyclic workflows previously validated as "Valid"
(the loader's structural check omits cycles — only the executor's topo-sort catches them), so the
workbench's "valid" wasn't a full run-acceptance guarantee; a **cycle-detection pass** now rejects them
(+ a test). One MINOR carried (a harmless dead-code branch in `Authoring.razor`). Details in `REVIEW.md`.

## Regression

The mandatory live pr-review-completion regression needs the gateway (out of credits) and is deferred,
consistent with the M6/M7*/F2 milestones. F3 adds a read/validate-only service + a page + one additive
loader method; it changes no run path. Build clean; `docker compose` boots healthy (the boot sweep is
unaffected by `LoadFromText`); `/authoring` renders.

## Tests

**521 offline tests, all green** (up from 511): `WorkbenchServiceTests` (10, incl. the cycle case). The
Blazor page is compile- + render-verified (the F1/F2 precedent).

## Residuals carried forward (see `REVIEW.md`)

- **Prompt-diff editing is deferred** (agreed scope) — the workflow-authoring core shipped.
- **Real git-publish is a graduation drop-in** — publish is a preview until a governed workflows repo +
  write access exist (the container mounts workflows read-only).
- The validate/dry-run round-trip is render-verified, not unit-tested at the page level (Blazor
  interaction; the service is fully unit-tested).
- The mandatory live pr-review regression is pending an Anthropic credit top-up (spend checkpoint).

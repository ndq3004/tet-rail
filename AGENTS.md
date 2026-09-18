# AGENTS.md

## 1. Scope and sources of truth

- This file applies to the entire repository.
- Start every task with `docs/knowledge/README.md`; it routes each type of change to the required knowledge.
- Before designing or implementing, read the relevant documents under `docs/`, prioritizing:
  - `docs/product-brief.md` for product scope and business behavior.
  - `docs/architecture.md` for service boundaries, data ownership, and contracts.
  - `docs/delivery-plan.md` for implementation order and Definition of Done.
- Do not change accepted architecture decisions without explicit approval. If documents conflict or omit a decision that materially affects implementation, report the blocker and request confirmation.
- Do not invent project knowledge. Add newly confirmed information to the appropriate file under `docs/knowledge/` in the same feature change.
- Conflict precedence: current user-confirmed requirement → accepted ADR/decision → source documents under `docs/` → knowledge summary → current code. Always surface conflicts instead of silently selecting one side.
- All repository-authored documentation, plans, ADRs, Markdown files, code comments, contract descriptions, and operational instructions must be written in English. Preserve external names and domain terms when translation would change their meaning. Do not add Vietnamese documentation.

## 2. Feature-plan management

- Store every feature plan directly in `docs/features/`; do not scatter plans elsewhere.
- Use one file per feature: `docs/features/<NNN>-<feature-slug>.md`.
- Use `docs/features/_template.md` when creating a plan.
- A plan must be small enough to implement, review, and verify independently. Split oversized work into plans with explicit dependencies.
- Valid statuses: `planned`, `in-progress`, `blocked`, and `done`.
- Before coding:
  1. Create or update the plan.
  2. Finalize scope, acceptance criteria, contract/data impact, and verification approach.
  3. Change status to `in-progress`.
- During implementation, check off completed work and record important decisions in the plan immediately.
- Change status to `done` only after acceptance criteria are met and appropriate verification succeeds.
- Do not keep a verbose action log; retain only state, decisions, and evidence needed to continue.

## 3. Implementation principles

- Preserve the five core-service boundaries and data ownership defined by the architecture.
- Use Go for the Booking Engine, .NET for the other backend services, and React + TypeScript for the frontend unless a new accepted decision changes this.
- A service must not read another service's database/schema directly.
- Version HTTP/event contracts; breaking changes require a migration or compatibility plan.
- Commands, consumers, and retryable webhooks must be idempotent.
- Do not rely only on cache or distributed locks for overbooking correctness; keep database constraints as the final barrier.
- Never log secrets, tokens, full identifiers, or sensitive payment data.
- Do not add dependencies, abstractions, or infrastructure without a need from the current feature.
- Prefer small, focused changes; avoid unrelated refactoring.
- Follow each service's existing style, formatter, linter, and layout. If none exists, use the runtime's official defaults and record the decision in the plan.

## 4. Testing and verification

- Every acceptance criterion needs at least one concrete verification method: automated test, contract test, benchmark, load test, or documented manual check.
- Run the narrowest relevant checks first, then broader suites when risk requires them.
- Concurrency, idempotency, allocation, and payment changes require retry/race/failure-path tests.
- Booking Engine hot-path changes require unit tests, the race detector, and relevant benchmarks.
- Contract changes require producer/consumer compatibility checks.
- Never claim `done`, `fixed`, or `passing` without verification. If a check cannot run, record `NOT RUN`, the reason, and remaining risk.
- Never weaken, skip, or delete tests merely to make a pipeline pass.

## 5. Repository change safety

- Preserve existing user changes; do not overwrite files outside the task scope.
- Do not use destructive Git or filesystem operations without an explicit request.
- Do not commit, push, create branches, or open pull requests unless requested.
- Never add secrets or real environment files; use examples with placeholder values.
- Data migrations require a rollback or documented forward-fix path in the plan.

## 6. Handoff response format

When completing a feature or an independently deliverable part, reply briefly using this structure and omit non-applicable items:

```text
Completed: <feature/feature portion>

- Changes: <1–3 points about behavior or key files>
- Verification: <main command/check and result, or NOT RUN + reason>
- Plan: <plan path> — <status/checkpoint>
- Remaining: <next work, risk, or blocker; use "None" when finished>
```

Token-efficiency rules:

- Lead with the result; do not narrate the entire process.
- Default to no more than eight lines and four bullets.
- Do not list every touched file; mention only files/contracts requiring review attention.
- Do not paste long test logs; report pass/fail and counts or the core failure.
- Do not repeat plan content; link to the plan.
- If blocked, use exactly one sentence stating the blocker and required decision/action.

## 7. Partial feature completion

- Check only completed items and keep the plan `in-progress`.
- State clearly what is complete and what comes next.
- The delivered portion must build/test independently within reasonable scope; do not intentionally leave the repository broken.

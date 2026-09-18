# 000 — Agent rules and project knowledge base

Status: `done`  
Owner: `Codex`  
Depends on: `none`

## Outcome

Agents have one consistent entry point for project knowledge, know which authoritative sources to read, and use a concise handoff format after each completed unit of work.

## Scope

### In scope

- Repository rules in `AGENTS.md`.
- A canonical feature-plan directory and template.
- A knowledge map, glossary, business rules, contracts, and foundational technical decisions.

### Out of scope

- TetRail source-code implementation.
- Resolving open product decisions.

## Acceptance criteria

- [x] Agents have one entry point for finding knowledge by task type.
- [x] Knowledge summaries link to source documents instead of duplicating them.
- [x] Dedicated files exist for terminology, business rules, contracts, and decisions.
- [x] `AGENTS.md` requires knowledge to be updated with any feature that changes confirmed information.
- [x] A single feature-plan directory and template exist.

## Design and impact

- Service/module: repository documentation only.
- API/event contract: none.
- Data/migration: none.
- Security/observability: knowledge rules prohibit secrets and PII.
- Key decisions: `docs/knowledge/README.md` is the entry point; detailed documents remain authoritative.

## Implementation

- [x] Add repository rules and handoff format.
- [x] Add the feature-plan workflow and template.
- [x] Add the project knowledge base and source links.

## Verification

- [x] Structure: required files exist.
- [x] Content: the entry point links all knowledge files and source documents.
- [x] Build/tests: N/A — Markdown-only change.

## Progress notes

- 2026-09-05: Created the initial rules, plan workflow, and knowledge base from the three existing project documents.

## Remaining risks

- Knowledge summaries must be maintained with future features to prevent drift.

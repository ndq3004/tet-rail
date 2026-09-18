# Project Knowledge Map

This is the primary entry point for TetRail project context. Read only the files relevant to the task; open the source documents when details are needed or when a summary is insufficient for a decision.

## Reading guide by task type

| Task | Required knowledge | Source documents |
|---|---|---|
| Product, scope, acceptance criteria | `business-rules.md`, `glossary.md` | `../product-brief.md` |
| Architecture, service boundaries, data ownership | `technical-context.md`, `decisions.md` | `../architecture.md` |
| API, events, integration | `contracts.md`, `business-rules.md` | `../architecture.md`, `../product-brief.md` |
| Planning or feature ordering | `decisions.md`, `../features/README.md` | `../delivery-plan.md` |
| Booking, concurrency, performance | all knowledge files | `../architecture.md`, `../delivery-plan.md` |

## Knowledge files

- `glossary.md`: domain terminology and core identifiers.
- `business-rules.md`: invariants and business rules that must not be violated.
- `contracts.md`: HTTP/event contracts and foundational integration conventions.
- `technical-context.md`: stack, service boundaries, data ownership, and quality gates.
- `decisions.md`: accepted decisions and open questions.

## Maintenance rules

- Knowledge files are retrieval summaries; detailed source documents remain authoritative.
- Do not copy long passages from source documents. Link to them and record concise invariants or decisions.
- When a feature changes the domain, contracts, or architecture, update the relevant knowledge file in the same change.
- Record only confirmed information. Keep unresolved matters under `Open questions` in `decisions.md`.
- Do not record secrets, credentials, tokens, real personal data, or sensitive environment information.
- Link every significant knowledge change to the relevant feature plan or ADR.

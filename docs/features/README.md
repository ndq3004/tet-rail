# Feature Plans

This directory is the single canonical location for feature implementation plans.

## Conventions

- File name: `<NNN>-<feature-slug>.md`, for example `001-repository-foundation.md`.
- Each implemented or planned feature has exactly one plan file directly in this directory.
- Copy `_template.md` when creating a new plan.
- The numeric ID indicates the expected delivery order; explicit dependencies in each plan remain authoritative.
- Valid plan statuses are `planned`, `in-progress`, `blocked`, and `done`.
- `N/A` in the catalog means that no implementation plan has been created and implementation has not started. It is not a valid status inside a feature plan.

## Catalog

| ID | Feature | User story | Status | Dependencies |
|---|---|---|---|---|
| 000 | Agent rules and project knowledge base | Foundation | done | none |
| 001 | Project skeleton | Foundation | done | 000 |
| 002 | Local platform foundation | Foundation | done | 001 |
| 003 | Trip search and fare discovery | US-02 | in-progress | Foundation |
| 004 | Seat map and availability projection | US-03 | planned | US-02 |
| 005 | Atomic time-limited seat holds | US-04 | in-progress | US-03 |
| 006 | Authentication and passenger management | US-01 | planned | Foundation |
| 007 | Order creation from an active hold | US-05 | N/A | US-01, US-04 |
| 008 | Payment simulator and idempotent webhooks | US-06 | N/A | US-05 |
| 009 | Hold expiration and automatic seat release | US-07 | N/A | US-04, US-05 |
| 010 | Electronic ticket issuance and delivery | US-08 | N/A | US-06 |
| 011 | Peak traffic control and waiting room | US-09 | N/A | Complete vertical slice |
| 012 | Administration, reporting, and operations | US-10 | N/A | Service domain events |
| 013 | Aspire local application orchestration | Foundation | in-progress | 002 |
| 014 | Customer booking web experience | UI foundation | done | 003, 004 |
| 015 | API endpoint overview | Documentation | done | 003 |
| 016 | Local Swagger UI | Foundation | in-progress | 013 |
| 017 | Catalog migration runner | Operations | in-progress | 002 |

Update this catalog in the same change whenever a feature plan is added or its status changes.

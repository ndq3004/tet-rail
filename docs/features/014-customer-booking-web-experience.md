# 014 — Customer booking web experience

Status: `done`
Owner: `Codex`
Depends on: `003`, `004`

## Outcome

Customers have a responsive, accessible web experience for searching journeys, comparing a result, and choosing projected seat-map states before the hold flow is connected.

## Scope

### In scope

- React/TypeScript booking interface with a journey search form, selected-trip summary, carriage selector, seat map, selection summary, and availability disclaimer.
- Keyboard-accessible controls and responsive desktop/mobile layout.
- Local preview data and interactions that mirror the published trip-search and planned seat-map response shapes.

### Out of scope

- Live HTTP requests, authentication, hold submission, checkout, payments, and ticket delivery.
- Treating a selected or `AVAILABLE` seat as reserved.

## Acceptance criteria

- [x] A customer can choose journey inputs, see a search result, select a carriage, and select/deselect only `AVAILABLE` seats.
- [x] The UI distinguishes `AVAILABLE`, `HELD`, and `SOLD`; it limits selection to four seats and states that availability is only a projection.
- [x] The interface works with keyboard controls and adapts to a narrow mobile viewport without clipped primary actions.
- [x] Preview data is clearly isolated from future Gateway API integration.

## Design and impact

- Service/module: `web/` only.
- API/event contract: no production calls or contract changes; preview types intentionally align with `GET /api/trips` and planned `GET /api/trips/{tripId}/seats`.
- Data/migration: none.
- Security/observability: no customer or payment data is stored or logged; selection state is in-memory only.
- Key decisions: UI never promises a seat; a future hold request is the only authoritative reservation action.

## Implementation

- [x] Add customer journey and seat-selection UI with local preview data.
- [x] Add responsive and accessible styling.
- [x] Verify production frontend build and visual layout.

## Verification

- [x] Build: `npm run build` from `web/` passed.
- [x] Manual: browser checks confirmed available-seat selection, held-seat feedback, activated continuation action, and the 390px-wide responsive layout.

## Progress notes

- 2026-09-19: Frontend-first implementation uses local preview data until US-02 and US-03 HTTP endpoints are connected.
- 2026-09-19: Production build passed. Browser checks verified local search feedback, seat selection, unavailable-seat feedback, and responsive rendering.

## Remaining risks

- API integration must handle loading, stale projection, unavailable, and error states once US-02/US-03 are available.

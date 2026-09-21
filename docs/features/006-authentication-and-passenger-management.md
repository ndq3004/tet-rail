# 006 — Authentication and passenger management

Status: `in-progress`
Owner: `Codex`
Depends on: `002`

## Outcome

Customers can authenticate through the selected OIDC provider and manage only their own passenger profiles. Gateway and service authorization distinguish customer and administrator access, refresh/revocation behavior is supported by the identity provider, and sensitive National ID data is protected and never exposed in logs.

## Scope

### In scope

- Configure AWS Cognito User Pools behind standard OAuth 2.0 / OpenID Connect and JWT interfaces.
- Add Catalog-owned user identity mapping, customer/admin role assignment, and passenger-profile persistence with ownership enforcement.
- Add versioned HTTP contracts for the authenticated current-user and passenger-profile operations, including API error v1 responses.
- Configure Gateway JWT signature validation, user-context propagation, and route-level customer/admin authorization without copying identity-domain logic into Gateway.
- Validate, protect, and mask National ID data; audit passenger create, update, and delete operations without recording full sensitive values.
- Cover access tokens, refresh/revocation behavior, ownership, authorization, sensitive-data handling, and audit behavior with automated tests.

### Out of scope

- Custom password/credential storage, a bespoke token issuer, social login, MFA, account recovery, or production identity-provider tenancy operations.
- Administrative catalog/schedule management, which belongs to feature 012.
- Order passenger snapshots, which belong to feature 007.
- Waiting-room admission tokens, which belong to feature 011.

## Acceptance criteria

- [ ] The selected OIDC provider issues standards-compliant access and refresh tokens; token refresh and revocation follow the provider-supported flow and are verified in the local/test environment.
- [ ] Gateway validates JWT signatures, expiry, issuer, audience, and required role claims before forwarding authenticated requests, while downstream services still enforce authorization.
- [ ] A customer can create, read, update, and delete only passenger profiles they own; cross-customer access returns API error v1 without disclosing profile existence.
- [ ] Administrative operations require the administrator role; customer credentials cannot gain administrative access by a forged or missing claim.
- [ ] Passenger input is bounded and validated. National ID data is protected at rest, masked in responses where shown, and excluded from logs, traces, metrics, and audit payloads.
- [ ] Passenger changes create an append-only audit record containing actor, action, profile reference, correlation ID, and timestamp, but no complete sensitive values.
- [ ] HTTP contracts are versioned and compatibility-tested; invalid, unauthenticated, forbidden, and ownership-denied responses use API error v1.
- [ ] Automated tests cover authorization, ownership, validation, masking/protection boundaries, refresh/revocation, and audit redaction.

## Design and impact

- Service/module: Catalog owns identity mapping, customer/admin roles, passenger profiles, and their audit data. Gateway owns edge JWT validation and route authorization only; it must not query Catalog tables or own passenger rules.
- API/event contract: add a versioned Identity/Passenger OpenAPI contract under `contracts/http/`; use `api-error.v1`. No domain event is required unless a confirmed consumer need emerges.
- Data/migration: add Catalog-owned tables for external subject mapping, roles, passenger profiles, and append-only audit records. Encrypt/protect sensitive National ID data using a provider/key-management mechanism selected with the OIDC implementation. Migrations are forward-only after shared data exists; provide a forward-fix for any protection-key rotation or migration defect.
- Security/observability: use OIDC/JWT standards, least-privilege claims, bounded inputs, correlation IDs, and redacted structured logging. Never place tokens, complete National IDs, or protected data in cache keys, telemetry, or audit values.
- Key decisions: AWS Cognito User Pools is the direct MVP OIDC provider (D-010); do not implement a custom credential or JWT issuer. Catalog remains authoritative for passenger ownership, and Gateway validation does not replace service-level authorization.

## Implementation

- [x] Confirm AWS Cognito User Pools as the MVP OIDC provider; Gateway and Catalog validate its JWTs directly.
- [ ] Define local/test Cognito-compatible issuer/audience/JWKS configuration, `cognito:groups` role mapping, client flows, refresh/revocation checks, and production key-management deployment.
- [x] Define versioned HTTP contracts, authorization/error semantics, and sensitive-field representation.
- [ ] Add producer/consumer compatibility fixtures.
- [x] Add Catalog-owned repositories for identity mapping, roles, passenger profiles, and redacted append-only audit records.
- [x] Implement Catalog authenticated passenger endpoints with validation, ownership checks, role checks, data protection, and correlated telemetry.
- [x] Configure Gateway JWT validation and protected-route forwarding; preserve user and correlation context without trusting client-provided identity headers.
- [ ] Add live-Cognito integration, contract, and end-to-end tests, including denied/forged/expired/revoked credential paths.
- [x] Update the knowledge base with confirmed provider and identity-contract decisions.

## Verification

- [ ] Unit/component: `dotnet test tests/catalog/TetRail.Catalog.Tests.csproj` covers passenger validation, ownership, authorization, masking, audit redaction, and protection boundaries.
- [ ] Integration/contract: run Catalog and Gateway integration tests against the selected OIDC provider and PostgreSQL; validate OpenAPI fixtures and API error v1 responses.
- [ ] Security: test expired, invalid-signature, wrong-issuer, wrong-audience, missing-role, forged-header, refresh, and revocation paths; inspect logs/traces/audit records for token and National ID leakage.
- [ ] End-to-end: authenticate a customer, manage an owned passenger, verify a second customer is denied, and verify an administrator-only route is unavailable to the customer.

## Progress notes

- 2026-09-20: Created from US-01, the product security requirements, and the Gateway/Catalog service boundary.
- 2026-09-20: AWS Cognito User Pools was confirmed as the direct MVP OIDC provider (D-010); feature implementation started.
- 2026-09-20: Added the versioned HTTP contract and a forward-only Catalog migration for Cognito identity mapping, customer/admin roles, protected passenger data, and value-free audit records. Endpoint/authentication wiring and automated coverage remain in progress.
- 2026-09-20: Added Catalog and Gateway JWT authentication, owner-scoped passenger create/read/update/delete routes, protected National ID storage, mutation audit records, API-error authentication responses, Gateway bearer forwarding, and a test-only authentication scheme. Catalog tests pass 18/18 with Kafka disabled only in the explicit `Testing` environment.

## Remaining risks

- Live Cognito integration requires issuer/audience environment configuration and an access token from the configured user pool. Production Data Protection key persistence remains to be configured with an AWS-managed protected store.

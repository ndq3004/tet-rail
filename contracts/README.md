# Contracts

Language-neutral, versioned contracts shared through schemas rather than runtime assemblies.

- `http/`: OpenAPI documents grouped by owning service.
- `events/`: AsyncAPI and event payload schemas.

Add schemas only with the feature that implements producer/consumer contract tests.

Foundation contracts shared by all features:

- `http/common/api-error.v1.schema.json`: Problem Details-compatible error with a stable machine code and correlation ID.
- `events/event-envelope.v1.schema.json`: common event identity, version, time and correlation metadata.

Concrete event schemas compose the envelope and narrow `event_type`, `version` and `payload`. Adjacent examples are contract-test fixtures.

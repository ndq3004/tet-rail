# Verify US-06 locally

Start the local Compose dependencies and both backend services. Configure `Payments__SimulatorSecret` with a locally supplied value before starting Order & Payment. Aspire forwards this setting when it launches the service. Outside Development, apply the Order & Payment migrations before starting the worker.

- Set `ORDER_TEST_DATABASE_URL` to a local test PostgreSQL connection string and run `dotnet test tests/order-payment`. The integration cases cover all five scenarios, concurrent delivery, restart, idempotency, and compensation records.
- Run `go test -race -tags integration ./internal/commands -run TestPaymentConfirmation` from the Booking Engine directory against the test database. The cases cover physical and multi-carriage holds, expired holds, allocation conflicts, and concurrent replay.
- Run `pwsh tests/order-payment/verify-payment-flow.ps1 -OrderUrl <order-service-url> -BookingUrl <booking-service-url>` against the local stack. The script uses trusted local service headers, waits for the Kafka confirmation round trip, and checks that invalid signatures fail and valid replays succeed.

Use the order state and the corresponding payment lifecycle event to diagnose a payment. If an order remains pending after authorization, check unpublished rows in the Order & Payment outbox and consumer lag for `booking-payment-confirmation-v1` and `ordering-payment-results-v1`. Restore PostgreSQL or Kafka connectivity and allow the workers to replay. For `PAYMENT_REVIEW`, inspect the compensation-required record before resolving the order. Ticket issuance is tracked in US-08.

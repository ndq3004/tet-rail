# Verify and operate electronic ticket issuance

Run PostgreSQL, Kafka and MinIO with the local Compose configuration. Aspire supplies the default development MinIO connection settings. Configure `Payments__SimulatorSecret` before running the payment flow. Deployments apply `services/ticketing/001_tickets.sql` and provision a private object bucket before launching Ticketing. Set `ConnectionStrings__TicketPostgres`, `Kafka__BootstrapServers`, `Ordering__BaseUrl`, and the `TicketStorage` configuration for the deployment. Use the AWS credential chain for S3. For MinIO supply an endpoint reachable by PDF clients, access key and secret key. Only the Gateway exposes customer routes publicly.

- Set `TICKET_TEST_DATABASE_URL` to a disposable PostgreSQL database and run `dotnet test tests/ticketing`.
- Run `pwsh tests/ticketing/verify-ticket-flow.ps1` against the three local backends using the service URLs as needed.
- The simulated notification adapter writes one delivery receipt per ticket after its PDF is ready. There is no external email transport in this slice.
- Inspect `ticketing.jobs` rows with `dead_at` set to find dead-letter jobs. After restoring the failing dependency, reset that job's `dead_at` and `attempts` and set `available_at` to the current time. The original ticket ID and object key are reused.

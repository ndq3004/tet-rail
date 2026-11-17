# Dot-source this script so its variables remain available to the current terminal:
# . .\scripts\set-local-env.ps1

$env:DATABASE_URL = "postgres://tetrail:tetrail-local-only@localhost:5432/tetrail"
$env:KAFKA_BROKERS = "localhost:9092"
$env:REDIS_URL = "redis://localhost:6379"
$env:ENABLE_KAFKA_DIAGNOSTICS = "true"

Write-Host "Booking Engine local environment is set."

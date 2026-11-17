param([string]$GatewayUrl = "http://127.0.0.1:52757", [int]$Requests = 200)
$ErrorActionPreference = "Stop"
$results = 1..$Requests | ForEach-Object -Parallel {
    $reply = Invoke-WebRequest "$using:GatewayUrl/api/trips?from=HNO&to=SGO&date=2026-12-01" -SkipHttpErrorCheck
    [pscustomobject]@{ Status = [int]$reply.StatusCode; Retry = $reply.Headers['Retry-After'] }
} -ThrottleLimit 32
$shed = @($results | Where-Object Status -eq 429)
if ($shed.Count -eq 0 -or @($shed | Where-Object { !$_.Retry }).Count -gt 0) {
    throw 'Expected controlled 429 shedding with Retry-After.'
}
$results | Group-Object Status | Select-Object Name,Count

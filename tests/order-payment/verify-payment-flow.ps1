param(
    [string]$OrderUrl = "http://127.0.0.1:54881",
    [string]$BookingUrl = "http://127.0.0.1:54880"
)
$ErrorActionPreference = "Stop"
if (!$env:Payments__SimulatorSecret) { throw "Set Payments__SimulatorSecret to the same value as Order & Payment." }

$owner = [guid]::NewGuid().ToString()
$headers = @{
    'X-User-Id' = $owner
    'X-Correlation-Id' = [guid]::NewGuid().ToString()
    'Idempotency-Key' = [guid]::NewGuid().ToString()
}
function PostJson($url, $payload) {
    Invoke-RestMethod -Uri $url -Method Post -Headers $headers -ContentType application/json -Body ($payload | ConvertTo-Json -Depth 8 -Compress)
}
function Poll($operation, $done) {
    for ($attempt = 0; $attempt -lt 80; $attempt++) {
        $value = & $operation
        if (& $done $value) { return $value }
        Start-Sleep -Milliseconds 250
    }
    throw "Timed out waiting for expected state. Last state was $($value.state) $($value.status)."
}

foreach ($scenario in @('success', 'failure', 'duplicate_callback')) {
    $trip = [guid]::NewGuid().ToString()
    $segments = @(
        @{trip_id=$trip;carriage_id='A';seat_id=[guid]::NewGuid().ToString();segment_index=1},
        @{trip_id=$trip;carriage_id='B';seat_id=[guid]::NewGuid().ToString();segment_index=1}
    )
    $headers['Idempotency-Key'] = [guid]::NewGuid().ToString()
    $request = PostJson "$BookingUrl/api/holds" @{segments=$segments}
    $hold = Poll { Invoke-RestMethod "$BookingUrl/api/booking-requests/$($request.request_id)" -Headers $headers } { param($value) $value.status -eq 'HELD' }
    $passengers = @(
        @{passenger_id=[guid]::NewGuid().ToString();full_name='Test Passenger A';date_of_birth='1990-01-01'},
        @{passenger_id=[guid]::NewGuid().ToString();full_name='Test Passenger B';date_of_birth='1990-01-01'}
    )
    $headers['Idempotency-Key'] = [guid]::NewGuid().ToString()
    $order = PostJson "$OrderUrl/api/orders" @{hold_id=$hold.hold_id;passengers=$passengers;fare=@{seat_class='standard';price=100;currency='VND';fare_version=1}}
    $headers['Idempotency-Key'] = [guid]::NewGuid().ToString()
    $payment = PostJson "$OrderUrl/api/payments/$($order.order_id)/simulate" @{scenario=$scenario}
    $replayed = PostJson "$OrderUrl/api/payments/$($order.order_id)/simulate" @{scenario=$scenario}
    if ($payment.provider_transaction_id -ne $replayed.provider_transaction_id) { throw "Attempt replay did not match." }
    $expected = if ($scenario -eq 'failure') { 'PAYMENT_FAILED' } else { 'PAID' }
    $null = Poll { Invoke-RestMethod "$OrderUrl/api/orders/$($order.order_id)" -Headers $headers } { param($value) $value.state -eq $expected }

    $callback = @{order_id=$order.order_id;provider_transaction_id=$payment.provider_transaction_id;outcome=$(if ($expected -eq 'PAID') {'success'} else {'failure'})}
    $body = [Text.Encoding]::UTF8.GetBytes(($callback | ConvertTo-Json -Compress))
    $key = [Text.Encoding]::UTF8.GetBytes($env:Payments__SimulatorSecret)
    $hash = [Convert]::ToHexString([Security.Cryptography.HMACSHA256]::HashData($key, $body))
    $webhook = "$OrderUrl/api/payment-webhooks/simulator"
    $invalid = Invoke-WebRequest $webhook -Method Post -ContentType application/json -Body $body -Headers @{'X-Simulator-Signature'=('0' * 64)} -SkipHttpErrorCheck
    if ($invalid.StatusCode -ne 401) { throw "Expected signature rejection." }
    $valid = Invoke-WebRequest $webhook -Method Post -ContentType application/json -Body $body -Headers @{'X-Simulator-Signature'=$hash} -SkipHttpErrorCheck
    if ($valid.StatusCode -ne 204) { throw "Expected idempotent signed callback, got $($valid.StatusCode)." }
    Write-Output "$scenario -> $expected"
}

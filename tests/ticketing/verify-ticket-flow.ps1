param(
    [string]$BookingUrl = "http://127.0.0.1:54880",
    [string]$OrderUrl = "http://127.0.0.1:54881",
    [string]$TicketUrl = "http://127.0.0.1:54882"
)
$ErrorActionPreference = "Stop"
$headers = @{ 'X-User-Id' = [guid]::NewGuid().ToString(); 'X-Correlation-Id' = [guid]::NewGuid().ToString() }
function Post($url, $payload) {
    $headers['Idempotency-Key'] = [guid]::NewGuid().ToString()
    Invoke-RestMethod $url -Method Post -Headers $headers -ContentType application/json -Body ($payload | ConvertTo-Json -Depth 8 -Compress)
}
function Wait-For($operation, $done) {
    for ($i = 0; $i -lt 120; $i++) {
        $value = & $operation
        if (& $done $value) { return $value }
        Start-Sleep -Milliseconds 250
    }
    throw 'Timed out waiting for the ticket flow.'
}
$segment = @{ trip_id=[guid]::NewGuid().ToString(); carriage_id='A'; seat_id=[guid]::NewGuid().ToString(); segment_index=1 }
$holdRequest = Post "$BookingUrl/api/holds" @{segments=@($segment)}
$hold = Wait-For { Invoke-RestMethod "$BookingUrl/api/booking-requests/$($holdRequest.request_id)" -Headers $headers } { param($v) $v.status -eq 'HELD' }
$order = Post "$OrderUrl/api/orders" @{
    hold_id=$hold.hold_id
    passengers=@(@{passenger_id=[guid]::NewGuid().ToString();full_name='Test Passenger';date_of_birth='1990-01-01'})
    fare=@{seat_class='standard';price=100;currency='VND';fare_version=1}
}
$null = Post "$OrderUrl/api/payments/$($order.order_id)/simulate" @{scenario='duplicate_callback'}
$list = Wait-For { Invoke-WebRequest "$TicketUrl/api/orders/$($order.order_id)/tickets" -Headers $headers | ForEach-Object { $_.Content | ConvertFrom-Json -NoEnumerate } } { param($v) $v.Count -eq 1 -and $v[0].pdfReady }
$ticketId = @($list)[0].ticketId
$ticket = Invoke-RestMethod "$TicketUrl/api/tickets/$ticketId" -Headers $headers
$check = Post "$TicketUrl/api/tickets/validate" @{reference=$ticket.ticket.qrReference}
if (!$check.valid) { throw 'Issued QR reference was invalid.' }
$check = Post "$TicketUrl/api/tickets/validate" @{reference=[guid]::NewGuid().ToString()}
if ($check.valid) { throw 'Unknown QR reference was accepted.' }
$foreign = Invoke-WebRequest "$TicketUrl/api/tickets/$ticketId" -Headers @{'X-User-Id'=[guid]::NewGuid().ToString()} -SkipHttpErrorCheck
if ($foreign.StatusCode -ne 404) { throw 'Foreign ticket access was not rejected.' }
$pdf = Invoke-WebRequest $ticket.pdfUrl
if ($pdf.Headers['Content-Type'] -notcontains 'application/pdf' -or [Text.Encoding]::ASCII.GetString($pdf.Content,0,5) -ne '%PDF-') { throw 'Expected a downloadable PDF.' }
$uri = [uri]$ticket.pdfUrl
if ($uri.Query -notmatch 'X-Amz-Expires=300') { throw 'Expected a five-minute signed URL.' }
$unsigned = Invoke-WebRequest ($uri.GetLeftPart([UriPartial]::Path)) -SkipHttpErrorCheck
if ($unsigned.StatusCode -ne 403) { throw 'PDF should require a signed URL.' }
'Paid order issued one ticket after duplicate callbacks. Owner access, QR validation, private PDF and five-minute signature verified.'

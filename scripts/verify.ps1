param([string]$BaseUrl = 'http://localhost:8080')
$ErrorActionPreference = 'Stop'
function Wait-Ready {
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest "$BaseUrl/health/ready" -TimeoutSec 3
            if ($response.StatusCode -eq 200) { return }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    throw 'API did not become ready.'
}
function Wait-Active([int]$Id) {
    $deadline = (Get-Date).AddSeconds(90)
    while ((Get-Date) -lt $deadline) {
        $item = Invoke-RestMethod "$BaseUrl/api/subscriptions/$Id"
        if ($item.isActive) { return }
        Start-Sleep -Milliseconds 300
    }
    throw "Subscription $Id was not activated."
}
function Docker-Step([string[]]$Arguments) {
    & docker @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Docker failed: $Arguments" }
}
Wait-Ready
$user = Invoke-RestMethod "$BaseUrl/api/users" -Method Post -ContentType 'application/json' -Body (@{
    name = 'Smoke'; email = "smoke-$([guid]::NewGuid().ToString('N'))@example.com"
} | ConvertTo-Json)
$body = @{ userId = $user.id; plan = 'basic' } | ConvertTo-Json
$headers = @{ 'Idempotency-Key' = [guid]::NewGuid().ToString() }
$first = Invoke-WebRequest "$BaseUrl/api/subscriptions/create" -Method Post -ContentType 'application/json' -Headers $headers -Body $body
if ($first.StatusCode -ne 202) { throw 'Expected 202.' }
$subscription = $first.Content | ConvertFrom-Json
Wait-Active $subscription.id
$repeat = Invoke-WebRequest "$BaseUrl/api/subscriptions/create" -Method Post -ContentType 'application/json' -Headers $headers -Body $body
if ($repeat.Content -ne $first.Content) { throw 'Replay response changed.' }

# A live outage verifies durable storage, process restart and eventual delivery.
try {
    Docker-Step @('compose', 'stop', 'rabbitmq')
    $apiReady = Invoke-WebRequest "$BaseUrl/health/ready" -TimeoutSec 5
    if ($apiReady.StatusCode -ne 200) { throw 'API should remain ready with RabbitMQ stopped.' }
    $headers['Idempotency-Key'] = [guid]::NewGuid().ToString()
    $pendingResponse = Invoke-WebRequest "$BaseUrl/api/subscriptions/create" -Method Post -ContentType 'application/json' -Headers $headers -Body $body -TimeoutSec 15
    $pending = $pendingResponse.Content | ConvertFrom-Json
    if ($pending.isActive) { throw 'Subscription unexpectedly activated with broker stopped.' }
    Docker-Step @('compose', 'restart', 'api')
    $deadline = (Get-Date).AddSeconds(45)
    $restarted = $false
    while ((Get-Date) -lt $deadline) {
        try {
            $replay = Invoke-WebRequest "$BaseUrl/api/subscriptions/create" -Method Post -ContentType 'application/json' -Headers $headers -Body $body -TimeoutSec 3
            $apiReady = Invoke-WebRequest "$BaseUrl/health/ready" -TimeoutSec 3
        } catch { Start-Sleep -Milliseconds 300; continue }
        # Assertions stay outside the retry catch: an incorrect response is a failure,
        # not a transient connection error that a later response can hide.
        if ($replay.Content -ne $pendingResponse.Content) { throw 'Replay response changed after API restart.' }
        if ($apiReady.StatusCode -ne 200) { throw 'Restarted API is unready while only RabbitMQ is down.' }
        $restarted = $true
        break
    }
    if (-not $restarted) { throw 'API did not accept replay while broker was unavailable.' }
} finally { Docker-Step @('compose', 'start', 'rabbitmq') }
Wait-Ready
Wait-Active $pending.id
try {
    Docker-Step @('compose', 'stop', 'postgres')
    # Allow the configured Npgsql connection timeout (15s by default) to finish.
    $ready = Invoke-WebRequest "$BaseUrl/health/ready" -SkipHttpErrorCheck -TimeoutSec 30
    if ($ready.StatusCode -ne 503) { throw 'API must become unready when PostgreSQL is unavailable.' }
    $live = Invoke-WebRequest "$BaseUrl/health/live" -TimeoutSec 3
    if ($live.StatusCode -ne 200) { throw 'Database outage must not fail process liveness.' }
} finally { Docker-Step @('compose', 'start', 'postgres') }
Wait-Ready
Write-Output 'VERIFIED: HTTP workflow, exact replay, broker outage, API restart, outbox recovery, DB readiness/liveness.'

param([string]$BaseUrl = 'http://127.0.0.1:8080')
$ErrorActionPreference = 'Stop'

# Runs on the local/CI Compose project selected by COMPOSE_FILE and
# COMPOSE_PROJECT_NAME. It deliberately stops its broker and worker.
# Use fresh volumes to test initial topology creation, as GitHub Actions does.
function Compose([string[]]$Arguments) {
    $result = & docker compose @Arguments
    if ($LASTEXITCODE -ne 0) { throw "Compose failed: $Arguments" }
    return $result
}
function Wait-Until([scriptblock]$Condition, [string]$Failure) {
    $deadline = (Get-Date).AddSeconds(90)
    $lastFailure = ''
    while ((Get-Date) -lt $deadline) {
        try { if (& $Condition) { return } }
        catch { $lastFailure = $_.Exception.Message }
        Start-Sleep -Milliseconds 500
    }
    throw "$Failure $lastFailure"
}
function Wait-Queued {
    Wait-Until {
        $state = Compose @('ps', '--format', 'json', 'rabbitmq')
        if (($state -join "`n" | ConvertFrom-Json).Health -ne 'healthy') { return $false }
        $raw = Compose @('exec', '-T', 'rabbitmq', 'rabbitmqctl', '-q', 'list_queues', '--formatter', 'json', 'name', 'messages_ready')
        $queue = ($raw -join "`n" | ConvertFrom-Json) | Where-Object name -eq 'subscription-activation'
        return $queue.messages_ready -ge 1
    } 'The API did not deliver the payment into a durable queue before any worker started.'
}

try {
    Compose @('stop', 'worker', 'rabbitmq')
    Compose @('up', '--build', '-d', 'api')
    $runningBroker = Compose @('ps', '--status', 'running', '-q', 'rabbitmq')
    if ($runningBroker) { throw 'Starting the outbox API unexpectedly started RabbitMQ.' }
    Wait-Until { (Invoke-WebRequest "$BaseUrl/health/ready" -TimeoutSec 3).StatusCode -eq 200 } 'Cold API is not ready.'
    $user = Invoke-RestMethod "$BaseUrl/api/users" -Method Post -ContentType 'application/json' -Body (@{
        name = 'Cold start'; email = "cold-$([guid]::NewGuid().ToString('N'))@example.com"
    } | ConvertTo-Json) -TimeoutSec 10
    $body = @{ userId = $user.id; plan = 'basic' } | ConvertTo-Json
    $headers = @{ 'Idempotency-Key' = [guid]::NewGuid().ToString() }
    $response = Invoke-WebRequest "$BaseUrl/api/subscriptions/create" -Method Post -ContentType 'application/json' -Headers $headers -Body $body -TimeoutSec 15
    if ($response.StatusCode -ne 202) { throw 'Cold API did not accept payment.' }
    $pending = $response.Content | ConvertFrom-Json
    if ($pending.isActive) { throw 'A subscription activated without any worker.' }
    Compose @('up', '-d', 'rabbitmq')
    Wait-Queued
    Compose @('restart', 'rabbitmq')
    Wait-Queued
    Compose @('up', '--build', '-d', 'worker')
    Wait-Until { (Invoke-RestMethod "$BaseUrl/api/subscriptions/$($pending.id)" -TimeoutSec 3).isActive } 'Queued payment was not activated.'
    Write-Output 'VERIFIED: cold API without RabbitMQ, publish before first worker, durable queue across broker restart, eventual activation.'
}
finally {
    Compose @('up', '-d', 'rabbitmq', 'worker')
}

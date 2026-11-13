param(
    [string]$PostgresBin = "C:\Program Files\PostgreSQL\18\bin",
    [int]$PostgresPort = 15432,
    [int]$OrderingPort = 19080,
    [int]$InventoryPort = 19081,
    [switch]$SkipBuild,
    [switch]$SkipE2E,
    [switch]$SkipHardKill
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$local = Join-Path $repo ".local-test"
$id = [guid]::NewGuid().ToString("N").Substring(0, 12)
$data = Join-Path $local ("pg-broker-" + $id)
$marker = Join-Path $data ".reliableflow-disposable"
$pgLog = Join-Path $local ("postgres-broker-" + $id + ".log")
$pgCtl = Join-Path $PostgresBin "pg_ctl.exe"
$initDb = Join-Path $PostgresBin "initdb.exe"
$psql = Join-Path $PostgresBin "psql.exe"
if (!(Test-Path $pgCtl) -or !(Test-Path $initDb) -or !(Test-Path $psql)) {
    throw "PostgreSQL 18 CLI not found; pass -PostgresBin."
}
New-Item -ItemType Directory -Path $local -Force | Out-Null
$transcriptPath = Join-Path $local ("broker-lab-" + $id + ".transcript.log")
Start-Transcript -Path $transcriptPath -Force | Out-Null

$ordering = $null
$inventory = $null
$pgStarted = $false
$rabbitStartedHere = $false
$rabbitUserCreated = $false
$rabbitVhostCreated = $false
$rabbitUser = "rf_" + $id
$rabbitVhost = "rf_" + $id
$savedEnvironment = @{}
$wslKeepAlive = $null
$orderingBase = "http://127.0.0.1:$OrderingPort"
$inventoryBase = "http://127.0.0.1:$InventoryPort"

function Check-Exit([string]$what) {
    if ($LASTEXITCODE -ne 0) { throw "$what failed with exit code $LASTEXITCODE" }
}

function Wait-For([string]$name, [scriptblock]$check, [int]$seconds = 90) {
    $deadline = [datetime]::UtcNow.AddSeconds($seconds)
    while ([datetime]::UtcNow -lt $deadline) {
        try {
            if (& $check) {
                Write-Host "PASS: $name"
                return
            }
        } catch { }
        Start-Sleep -Milliseconds 500
    }
    throw "TIMEOUT: $name"
}

function Api([string]$url) {
    return Invoke-RestMethod -Uri $url -TimeoutSec 5
}

function Available-Stock([string]$sku) {
    $items = Api "$inventoryBase/stock"
    foreach ($item in $items) {
        if ($item.sku -eq $sku) { return [int]$item.available }
    }
    throw "SKU not present in Inventory: $sku"
}

function Count-Reservations([string]$orderId) {
    $reservations = Api "$inventoryBase/reservations"
    $found = 0
    foreach ($reservation in $reservations) {
        if ($reservation.orderId -eq $orderId) { $found++ }
    }
    return $found
}

function Start-Ordering([string]$suffix) {
    $env:ASPNETCORE_URLS = $orderingBase
    return Start-Process -FilePath "dotnet.exe" -ArgumentList (Join-Path $repo "samples/Ordering.Api/bin/Release/net10.0/Ordering.Api.dll") -WorkingDirectory $repo -PassThru -RedirectStandardOutput (Join-Path $local ("ordering-" + $suffix + ".stdout.log")) -RedirectStandardError (Join-Path $local ("ordering-" + $suffix + ".stderr.log"))
}

try {
    foreach ($port in @($PostgresPort, $OrderingPort, $InventoryPort)) {
        if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) {
            throw "Local TCP port $port is already in use; use different test ports."
        }
    }
    # WSL can shut down its VM while Windows .NET processes are still using
    # RabbitMQ. Keep a dedicated, short-lived WSL session alive for this lab.
    $wslKeepAlive = Start-Process -FilePath "wsl.exe" -ArgumentList "-u root -e sleep 900" -PassThru
    Write-Host "Starting isolated PostgreSQL and WSL RabbitMQ"
    & $initDb -D $data -U demo -A trust -E UTF8 --no-instructions
    Check-Exit "initdb"
    Set-Content -Path $marker -Value "ReliableFlow disposable test cluster"
    & $pgCtl -D $data -l $pgLog -o "-h 127.0.0.1 -p $PostgresPort" -w start
    Check-Exit "pg_ctl start"
    $pgStarted = $true
    & $psql -h 127.0.0.1 -p $PostgresPort -U demo -d postgres -v ON_ERROR_STOP=1 -f (Join-Path $repo "scripts/create_databases.sql")
    Check-Exit "CREATE DATABASE demo schemas"

    $initialRabbit = (& wsl.exe -u root -e systemctl is-active rabbitmq-server 2>$null | Out-String).Trim()
    if ($initialRabbit -ne "active") {
        & wsl.exe -u root -e systemctl start rabbitmq-server
        Check-Exit "WSL RabbitMQ service start"
        $rabbitStartedHere = $true
    }
    & wsl.exe -u root -e /usr/sbin/rabbitmqctl await_startup
    Check-Exit "RabbitMQ startup"
    & wsl.exe -u root -e /usr/sbin/rabbitmqctl add_user $rabbitUser demo_only
    Check-Exit "RabbitMQ test user"
    $rabbitUserCreated = $true
    & wsl.exe -u root -e /usr/sbin/rabbitmqctl add_vhost $rabbitVhost
    Check-Exit "RabbitMQ test vhost"
    $rabbitVhostCreated = $true
    & wsl.exe -u root -e /usr/sbin/rabbitmqctl set_permissions -p $rabbitVhost $rabbitUser ".*" ".*" ".*"
    Check-Exit "RabbitMQ permissions"

    foreach ($name in @("ConnectionStrings__Orders", "ConnectionStrings__Inventory",
                        "RabbitMQ__Host", "RabbitMQ__VirtualHost",
                        "RabbitMQ__User", "RabbitMQ__Password",
                        "ASPNETCORE_URLS", "ASPNETCORE_ENVIRONMENT",
                        "RF_ORDERING_URL", "RF_INVENTORY_URL",
                        "ReliableFlow__DemoLeaseSeconds")) {
        $savedEnvironment[$name] = [Environment]::GetEnvironmentVariable($name, "Process")
    }
    $env:ConnectionStrings__Orders = "Host=127.0.0.1;Port=$PostgresPort;Database=orders_db;Username=demo"
    $env:ConnectionStrings__Inventory = "Host=127.0.0.1;Port=$PostgresPort;Database=inventory_db;Username=demo"
    # Windows->WSL localhost forwarding is not guaranteed; use WSL's current VM IP.
    $wslAddresses = ((& wsl.exe -e hostname -I | Out-String).Trim() -split '\s+')
    $rabbitHost = $wslAddresses[0]
    if ([string]::IsNullOrWhiteSpace($rabbitHost)) {
        throw "Cannot resolve WSL address for the RabbitMQ broker"
    }
    $env:RabbitMQ__Host = $rabbitHost
    $env:RabbitMQ__VirtualHost = $rabbitVhost
    $env:RabbitMQ__User = $rabbitUser
    $env:RabbitMQ__Password = "demo_only"
    $env:ASPNETCORE_ENVIRONMENT = "Development"
    $env:RF_ORDERING_URL = $orderingBase
    $env:RF_INVENTORY_URL = $inventoryBase
    # Keep real hard-crash tests short; changes only the development demo.
    $env:ReliableFlow__DemoLeaseSeconds = "12"

    if (!$SkipBuild) {
        & dotnet build (Join-Path $repo "ReliableFlow.slnx") -c Release --no-restore -v quiet
        Check-Exit "Release build"
    }

    $ordering = Start-Ordering "first"
    $env:ASPNETCORE_URLS = $inventoryBase
    $inventory = Start-Process -FilePath "dotnet.exe" -ArgumentList (Join-Path $repo "samples/Inventory.Service/bin/Release/net10.0/Inventory.Service.dll") -WorkingDirectory $repo -PassThru -RedirectStandardOutput (Join-Path $local "inventory.stdout.log") -RedirectStandardError (Join-Path $local "inventory.stderr.log")

    Wait-For "Ordering API starts" { (Api "$orderingBase/health").status -eq "ok" } 60
    Wait-For "Inventory API starts" { (Api "$inventoryBase/health").status -eq "ok" } 60
    if (!$SkipE2E) {
        & python (Join-Path $repo "scripts/e2e.py")
        Check-Exit "RabbitMQ E2E failure lab"
        Write-Host "PASS: RabbitMQ normal/retry/duplicate E2E"
    }

    if (!$SkipHardKill) {
    $before = Available-Stock "BOOK-001"
    $beforeDuplicates = (Api "$inventoryBase/demo/consumer-metrics").duplicates
    Invoke-RestMethod -Uri "$orderingBase/demo/faults/pause-after-publish-once" -Method Post -ContentType "application/json" -Body "{}" | Out-Null
    $created = Invoke-RestMethod -Uri "$orderingBase/orders" -Method Post -ContentType "application/json" -Body '{"sku":"BOOK-001","quantity":1}'
    Wait-For "Broker-confirmed publisher paused before outbox update" { (Api "$orderingBase/demo/faults/status").isPausedAfterConfirm } 50
    Wait-For "Original consumer reservation committed" {
        (Count-Reservations $created.id) -eq 1
    } 45
    $pending = @((Api "$orderingBase/outbox") | Where-Object { $_.id -eq $created.eventId })
    if ($pending.Count -ne 1 -or $pending[0].publishedAtUtc) {
        throw "Outbox marked published before real process kill"
    }

    Stop-Process -Id $ordering.Id -Force
    $ordering.WaitForExit()
    Write-Host "PASS: real Windows process force-killed after broker confirmation"
    $ordering = Start-Ordering "restarted"
    Wait-For "Ordering process restarted" { (Api "$orderingBase/health").status -eq "ok" } 60
    Wait-For "Expired lease reclaimed, event re-published" {
        $row = @((Api "$orderingBase/outbox") | Where-Object { $_.id -eq $created.eventId })
        $row.Count -eq 1 -and $null -ne $row[0].publishedAtUtc
    } 150
    Wait-For "Duplicate observed by Inventory" {
        (Api "$inventoryBase/demo/consumer-metrics").duplicates -gt $beforeDuplicates
    } 60

    $after = Available-Stock "BOOK-001"
    $reservationCount = Count-Reservations $created.id
    Write-Host "Final assertion: stock $before -> $after, reservations=$reservationCount"
    if ($after -ne ($before - 1) -or $reservationCount -ne 1) {
        throw "Duplicate publication changed inventory more than once"
    }
    Write-Host "PASS: real process kill -> lease recovery -> duplicate delivery -> one business effect"
    }
    Write-Host "PASS: LOCAL REAL-BROKER FAILURE LAB"
} finally {
    foreach ($process in @($ordering, $inventory)) {
        if ($null -ne $process -and !$process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
    }
    foreach ($pair in $savedEnvironment.GetEnumerator()) {
        [Environment]::SetEnvironmentVariable($pair.Key, $pair.Value, "Process")
    }
    if ($rabbitUserCreated) {
        & wsl.exe -u root -e /usr/sbin/rabbitmqctl delete_user $rabbitUser | Out-Null
    }
    if ($rabbitVhostCreated) {
        & wsl.exe -u root -e /usr/sbin/rabbitmqctl delete_vhost $rabbitVhost | Out-Null
    }
    if ($rabbitStartedHere) {
        & wsl.exe -u root -e systemctl stop rabbitmq-server | Out-Null
    }
    $pgStopped = !$pgStarted
    if ($pgStarted) {
        & $pgCtl -D $data -m immediate -w stop | Out-Null
        $pgStopped = $LASTEXITCODE -eq 0
    }
    if ($pgStopped -and (Test-Path $marker)) {
        Remove-Item -LiteralPath $data -Force -Recurse
    } elseif (Test-Path $marker) {
        Write-Warning "Preserving test database files because PostgreSQL did not stop cleanly: $data"
    }
    if ($null -ne $wslKeepAlive -and !$wslKeepAlive.HasExited) {
        Stop-Process -Id $wslKeepAlive.Id -Force -ErrorAction SilentlyContinue
    }
    Stop-Transcript | Out-Null
}

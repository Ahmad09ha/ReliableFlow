param(
    [string]$PostgresBin = "C:\Program Files\PostgreSQL\18\bin",
    [int]$Port = 15433,
    [switch]$KeepData
)

$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$local = Join-Path $repo ".local-test"
$data = Join-Path $local "postgres"
$log = Join-Path $local "postgres.log"
$marker = Join-Path $data ".reliableflow-disposable"
$initDb = Join-Path $PostgresBin "initdb.exe"
$pgCtl = Join-Path $PostgresBin "pg_ctl.exe"
if (!(Test-Path $initDb) -or !(Test-Path $pgCtl)) {
    throw "PostgreSQL initdb.exe and pg_ctl.exe required. Set -PostgresBin explicitly."
}

New-Item -ItemType Directory -Path $local -Force | Out-Null
$created = !(Test-Path $data)
if ($created) {
    & $initDb -D $data -U demo -A trust -E UTF8 --no-instructions
    if ($LASTEXITCODE -ne 0) { throw "initdb failed" }
    Set-Content -Path $marker -Value "Disposable test cluster created by ReliableFlow"
} elseif (!(Test-Path $marker)) {
    throw "Refusing to start unrecognized PostgreSQL data directory: $data"
}

$started = $false
$prior = [Environment]::GetEnvironmentVariable("RF_PG_ADMIN_CONNECTION", "Process")
try {
    & $pgCtl -D $data -l $log -o "-h 127.0.0.1 -p $Port" -w start
    if ($LASTEXITCODE -ne 0) { throw "Local PostgreSQL startup failed. Inspect $log" }
    $started = $true
    $env:RF_PG_ADMIN_CONNECTION = "Host=127.0.0.1;Port=$Port;Database=postgres;Username=demo"
    & dotnet run --project (Join-Path $repo "tests\ReliableFlow.PostgresStress") -c Release
    if ($LASTEXITCODE -ne 0) { throw "PostgreSQL stress tests failed" }
} finally {
    [Environment]::SetEnvironmentVariable("RF_PG_ADMIN_CONNECTION", $prior, "Process")
    if ($started) {
        & $pgCtl -D $data -m immediate -w stop
        if ($LASTEXITCODE -ne 0) { Write-Warning "Failed to stop PostgreSQL test instance" }
    }
    if ($created -and !$KeepData -and (Test-Path $marker)) {
        Remove-Item -LiteralPath $data -Recurse -Force
    }
}

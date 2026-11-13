$ErrorActionPreference = "Stop"
$repo = Split-Path -Parent $PSScriptRoot
$packagePath = Join-Path $repo "artifacts"
$temp = Join-Path ([System.IO.Path]::GetTempPath()) ("ReliableFlowSmoke-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $temp -Force | Out-Null
try {
    & dotnet new classlib -n PackageSmoke -o $temp --framework net10.0 --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Could not scaffold consuming project" }
    $csproj = Join-Path $temp "PackageSmoke.csproj"
    & dotnet add $csproj package ReliableFlow.EntityFrameworkCore --version 0.1.0 --no-restore
    if ($LASTEXITCODE -ne 0) { throw "Could not add local NuGet package" }
    @'
using Microsoft.EntityFrameworkCore;
using ReliableFlow.EntityFrameworkCore;

namespace PackageSmoke;

public sealed class ExternalAppDb(DbContextOptions<ExternalAppDb> options) : DbContext(options)
{
    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.AddReliableFlow();

    public Guid StageMessage(Guid commandId) =>
        this.EnqueueOutbox("smoke.created.v1", new { CommandId = commandId });
}
'@ | Set-Content -Path (Join-Path $temp "Smoke.cs") -Encoding UTF8
    $nugetConfig = Join-Path $temp "NuGet.Config"
    @"
<configuration>
  <packageSources>
    <clear/>
    <add key="local" value="$packagePath" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
  </packageSources>
</configuration>
"@ | Set-Content -Path $nugetConfig -Encoding UTF8
    & dotnet restore $csproj --configfile $nugetConfig
    if ($LASTEXITCODE -ne 0) { throw "NuGet restore of independent consumer failed" }
    & dotnet build $csproj --no-restore -c Release -v quiet
    if ($LASTEXITCODE -ne 0) { throw "Independent package consumer did not compile" }
    Write-Output "PASS: independent project restored and compiled against ReliableFlow 0.1.0 NuGet"
} finally {
    Remove-Item -Path $temp -Force -Recurse -ErrorAction SilentlyContinue
}

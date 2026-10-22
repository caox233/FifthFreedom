[CmdletBinding()]
param(
    [string]$OutputDirectory = "",
    [ValidateSet('full', 'bootstrap', 'manager', 'host', 'updater', 'dedicated', 'control-plane', 'update-server', 'assets', 'python')]
    [string]$Component = 'full',
    [switch]$SkipDedicatedTests
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\..'))
$work = Join-Path $repoRoot '.work\windows-server'
$publish = Join-Path $work 'publish'
$output = if ($OutputDirectory) { [System.IO.Path]::GetFullPath($OutputDirectory) } else { Join-Path $repoRoot 'release-output\windows-server' }
$python = Join-Path $env:USERPROFILE '.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe'
$pythonEmbed = Join-Path $work 'downloads\python-3.12.10-embed-amd64.zip'
$pythonUrl = 'https://www.python.org/ftp/python/3.12.10/python-3.12.10-embed-amd64.zip'
$pythonSha256 = '4acbed6dd1c744b0376e3b1cf57ce906f9dc9e95e68824584c8099a63025a3c3'
$versions = Get-Content -LiteralPath (Join-Path $repoRoot 'COMPONENT_VERSIONS.json') -Raw -Encoding UTF8 | ConvertFrom-Json
$serverVersion = [string]$versions.serverReleaseVersion
$fileVersion = "$serverVersion.0"
$env:SCBL_DEDICATED_VERSION = [string]$versions.serverComponents.dedicated

if (-not (Test-Path -LiteralPath $python -PathType Leaf)) {
    throw "Bundled Python was not found: $python"
}
$isFull = $Component -eq 'full'
New-Item -ItemType Directory -Force -Path $publish, $output | Out-Null

if ($isFull -or $Component -eq 'python') {
    New-Item -ItemType Directory -Force -Path (Split-Path $pythonEmbed) | Out-Null
    if (-not (Test-Path -LiteralPath $pythonEmbed -PathType Leaf) -or (Get-FileHash $pythonEmbed -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pythonSha256) {
        Remove-Item -LiteralPath $pythonEmbed -Force -ErrorAction SilentlyContinue
        Invoke-WebRequest -Uri $pythonUrl -OutFile $pythonEmbed -UseBasicParsing
    }
    if ((Get-FileHash $pythonEmbed -Algorithm SHA256).Hash.ToLowerInvariant() -ne $pythonSha256) {
        throw 'Python embeddable package SHA256 mismatch.'
    }
}

Push-Location $repoRoot
try {
    & dotnet run --project 'server\windows\SCBL.Server.Tests\SCBL.Server.Tests.csproj' -c Release
    if ($LASTEXITCODE -ne 0) { throw 'Windows server tests failed.' }

    if (($isFull -or $Component -eq 'dedicated') -and $SkipDedicatedTests) {
        $toolRoot = Join-Path (Split-Path -Parent $repoRoot) '.toolchains'
        $env:CARGO_HOME = Join-Path $toolRoot 'cargo'
        $env:RUSTUP_HOME = Join-Path $toolRoot 'rustup'
        $cargo = Join-Path $env:CARGO_HOME 'bin\cargo.exe'
        if (-not (Test-Path -LiteralPath $cargo)) { throw 'Task-local cargo was not found.' }
        & $cargo build --locked -p dedicated_server --release
        if ($LASTEXITCODE -ne 0) { throw 'Dedicated build failed.' }
    } elseif ($isFull -or $Component -eq 'dedicated') {
        & (Join-Path $repoRoot 'scripts\build-rust-components.ps1') -Release -DedicatedOnly
        if ($LASTEXITCODE -ne 0) { throw 'Dedicated tests/build failed.' }
    }

    $projects = @(
        @{ Project = 'SCBL.Server.Manager'; Output = 'manager' },
        @{ Project = 'SCBL.Server.Host'; Output = 'host' },
        @{ Project = 'SCBL.Server.Update'; Output = 'updater' }
    )
    if (-not $isFull -and $Component -ne 'bootstrap') {
        $projects = @($projects | Where-Object { $_.Output -eq $Component })
    }
    foreach ($item in $projects) {
        $projectPath = Join-Path $repoRoot "server\windows\$($item.Project)\$($item.Project).csproj"
        $destination = Join-Path $publish $item.Output
        if (Test-Path -LiteralPath $destination) { Remove-Item -LiteralPath $destination -Recurse -Force }
        & dotnet publish $projectPath -c Release -r win-x64 --self-contained true -o $destination /p:Version=$serverVersion /p:FileVersion=$fileVersion /p:AssemblyVersion=$fileVersion /p:PublishSingleFile=true /p:IncludeNativeLibrariesForSelfExtract=true /p:DebugType=None /p:DebugSymbols=false
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $($item.Project)" }
    }

    $packageArguments = @('server\windows\package_windows_server.py', '--root', $repoRoot, '--output-dir', $output)
    if ($isFull -or $Component -in @('bootstrap', 'manager')) { $packageArguments += @('--manager', (Join-Path $publish 'manager\SCBL.Server.exe')) }
    if ($isFull -or $Component -in @('bootstrap', 'host')) { $packageArguments += @('--host', (Join-Path $publish 'host\SCBL.Server.Host.exe')) }
    if ($isFull -or $Component -in @('bootstrap', 'updater')) { $packageArguments += @('--updater', (Join-Path $publish 'updater\SCBL.Server.Update.exe')) }
    if ($isFull -or $Component -eq 'dedicated') {
        $dedicated = Join-Path $repoRoot 'target\release\dedicated_server.exe'
        if (-not (Test-Path -LiteralPath $dedicated -PathType Leaf)) { throw "Dedicated output was not found: $dedicated" }
        $packageArguments += @('--dedicated', $dedicated)
    }
    if ($isFull -or $Component -eq 'python') { $packageArguments += @('--python-embed', $pythonEmbed) }
    if ($Component -eq 'bootstrap') { $packageArguments += @('--components', 'manager', 'host', 'updater') }
    elseif (-not $isFull) { $packageArguments += @('--components', $Component) }
    & $python @packageArguments
    if ($LASTEXITCODE -ne 0) { throw 'Windows package assembly failed.' }
} finally {
    Pop-Location
}

Write-Host "Windows server packages: $output" -ForegroundColor Green

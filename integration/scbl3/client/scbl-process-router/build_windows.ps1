$ErrorActionPreference = "Stop"

$ProjectRoot = $PSScriptRoot
$RepoRoot = Split-Path -Parent (Split-Path -Parent $ProjectRoot)
$ToolRoot = Join-Path (Split-Path -Parent $RepoRoot) ".toolchains"
$GoCommand = Get-Command go.exe -ErrorAction SilentlyContinue
$Go = if ($GoCommand) { $GoCommand.Source } else { Join-Path $ToolRoot "go-1.23.12\go\bin\go.exe" }
$ComponentVersionsPath = Join-Path $RepoRoot "COMPONENT_VERSIONS.json"

if (!(Test-Path -LiteralPath $Go -PathType Leaf)) {
    throw "Go was not found. Install Go 1.23 or place go1.23.12 in the sibling .toolchains directory."
}
if (!(Test-Path -LiteralPath $ComponentVersionsPath -PathType Leaf)) {
    throw "Component version manifest was not found: $ComponentVersionsPath"
}

try {
    $ComponentVersions = Get-Content -LiteralPath $ComponentVersionsPath -Raw -Encoding UTF8 | ConvertFrom-Json
}
catch {
    throw "Component version manifest is not valid JSON: $ComponentVersionsPath ($($_.Exception.Message))"
}
$RouteGuardVersion = $ComponentVersions.routeGuardVersion
if (!($RouteGuardVersion -is [string]) -or $RouteGuardVersion -notmatch '^(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)\.(0|[1-9][0-9]*)$') {
    throw "routeGuardVersion must be a three-part semantic version in ${ComponentVersionsPath}: $RouteGuardVersion"
}

Push-Location $ProjectRoot
try {
    if (!(Test-Path -LiteralPath (Join-Path $ProjectRoot "go.mod"))) {
        throw "go.mod not found in $ProjectRoot"
    }

    $WinDivertDll = Join-Path $ProjectRoot "WinDivert.dll"
    $WinDivertDriver = Join-Path $ProjectRoot "WinDivert64.sys"
    if (!(Test-Path -LiteralPath $WinDivertDll) -or !(Test-Path -LiteralPath $WinDivertDriver)) {
        $DownloadWinDivert = Join-Path $ProjectRoot "download_windivert.ps1"
        if (!(Test-Path -LiteralPath $DownloadWinDivert)) {
            throw "WinDivert files are missing and the download script was not found: $DownloadWinDivert"
        }

        Write-Host "WinDivert runtime is missing. Downloading the pinned runtime ..."
        & $DownloadWinDivert
        if (-not $?) { throw "WinDivert download failed" }
    }

    if (!(Test-Path -LiteralPath $WinDivertDll)) { throw "WinDivert.dll was not prepared: $WinDivertDll" }
    if (!(Test-Path -LiteralPath $WinDivertDriver)) { throw "WinDivert64.sys was not prepared: $WinDivertDriver" }

    Write-Host "Restoring Go modules without rewriting go.mod/go.sum ..."
    & $Go mod download
    if ($LASTEXITCODE -ne 0) { throw "go mod download failed" }

    Write-Host "Building scbl-process-router.exe ..."
    $OldGOOS = $env:GOOS
    $OldGOARCH = $env:GOARCH
    try {
        $env:GOOS = "windows"
        $env:GOARCH = "amd64"
        $LdFlags = "-s -w -X main.routerVersion=$RouteGuardVersion"
        & $Go build -mod=readonly -trimpath -buildvcs=false -ldflags $LdFlags -o "scbl-process-router.exe" .
        if ($LASTEXITCODE -ne 0) { throw "go build failed" }
    }
    finally {
        $env:GOOS = $OldGOOS
        $env:GOARCH = $OldGOARCH
    }

    $Output = Join-Path $ProjectRoot "scbl-process-router.exe"
    if (!(Test-Path -LiteralPath $Output)) { throw "Output file was not created: $Output" }

    $ReportedVersion = (& $Output -version | Out-String).Trim()
    if ($LASTEXITCODE -ne 0) { throw "Route Guard version self-check failed with exit code $LASTEXITCODE" }
    if ($ReportedVersion -cne $RouteGuardVersion) {
        throw "Route Guard version self-check failed: expected '$RouteGuardVersion', actual '$ReportedVersion'"
    }

    Write-Host "Route Guard outputs verified:"
    Write-Host "  $Output"
    Write-Host "  version=$ReportedVersion"
    Write-Host "  $WinDivertDll"
    Write-Host "  $WinDivertDriver"
}
finally {
    Pop-Location
}

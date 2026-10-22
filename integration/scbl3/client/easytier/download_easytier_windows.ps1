param(
    [string]$Version = $env:EASYTIER_VERSION,
    [ValidateSet('x86_64','i686','arm64')]
    [string]$Arch = 'x86_64',
    [string]$PackagePath = $env:EASYTIER_WINDOWS_PACKAGE
)

$ErrorActionPreference = 'Stop'
$Root = $PSScriptRoot
$RepositoryRoot = (Resolve-Path (Join-Path $Root "..\..")).Path
. (Join-Path $RepositoryRoot "scripts\lib\scbl-hash.ps1")
$ComponentVersionsPath = Join-Path $RepositoryRoot 'COMPONENT_VERSIONS.json'
if ([string]::IsNullOrWhiteSpace($Version)) {
    $ComponentVersions = Get-Content -LiteralPath $ComponentVersionsPath -Raw -Encoding UTF8 | ConvertFrom-Json
    $Version = [string]$ComponentVersions.easyTierVersion
}
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "easyTierVersion must be a three-part version in COMPONENT_VERSIONS.json: $Version"
}
$Bin = Join-Path $Root 'bin'
$Cache = Join-Path $Root 'cache'
New-Item -ItemType Directory -Force -Path $Bin, $Cache | Out-Null

function Get-EasyTierRelease {
    param([string]$RequestedVersion)

    $Headers = @{ 'User-Agent' = 'SCBL-EasyTier-Build/2' }
    if ([string]::IsNullOrWhiteSpace($RequestedVersion)) {
        $Uri = 'https://api.github.com/repos/EasyTier/EasyTier/releases/latest'
    }
    else {
        $RequestedVersion = $RequestedVersion.Trim()
        if (!$RequestedVersion.StartsWith('v')) { $RequestedVersion = 'v' + $RequestedVersion }
        $Uri = 'https://api.github.com/repos/EasyTier/EasyTier/releases/tags/' + $RequestedVersion
    }
    Write-Host "Checking the official EasyTier release: $Uri"
    $Release = Invoke-RestMethod -UseBasicParsing -Headers $Headers -Uri $Uri
    $Tag = [string]$Release.tag_name
    if ($Tag -notmatch '^v[0-9]+\.[0-9]+\.[0-9]+$') {
        throw "Unexpected EasyTier release tag: $Tag"
    }
    return @{ Release = $Release; Tag = $Tag }
}

$ReleaseInfo = Get-EasyTierRelease $Version
$Version = [string]$ReleaseInfo.Tag
$AssetName = "easytier-windows-$Arch-$Version.zip"
$Asset = @($ReleaseInfo.Release.assets | Where-Object { $_.name -eq $AssetName }) | Select-Object -First 1
if (!$Asset) { throw "The official EasyTier release does not contain $AssetName." }
$Digest = [string]$Asset.digest
if ($Digest -notmatch '^sha256:([0-9a-fA-F]{64})$') {
    throw "The official EasyTier asset has no usable SHA256 digest: $AssetName"
}
$ExpectedSha256 = $Matches[1].ToLowerInvariant()
$ExpectedSize = [long]$Asset.size
if ($ExpectedSize -le 0) { throw "The official EasyTier asset has an invalid size: $AssetName" }

$Marker = Join-Path $Bin ('.scbl-prepared-{0}-{1}' -f $Version, $Arch)
$CorePath = Join-Path $Bin 'easytier-core.exe'
$CliPath = Join-Path $Bin 'easytier-cli.exe'
$PacketPath = Join-Path $Bin 'Packet.dll'
$WintunPath = Join-Path $Bin 'wintun.dll'
$VersionPath = Join-Path $Bin 'easytier-version.txt'

if ((Test-Path -LiteralPath $Marker) -and
    (Test-Path -LiteralPath $CorePath) -and
    (Test-Path -LiteralPath $CliPath) -and
    (Test-Path -LiteralPath $PacketPath) -and
    (Test-Path -LiteralPath $WintunPath) -and
    (Test-Path -LiteralPath $VersionPath)) {
    $MarkerValue = (Get-Content -LiteralPath $Marker -Raw -ErrorAction SilentlyContinue).Trim()
    if ($MarkerValue -eq "$Version|$Arch|$ExpectedSha256") {
        Write-Host "Reusing verified EasyTier runtime: $Version / $Arch"
        & $CorePath --version
        & $CliPath --version
        exit 0
    }
}

if ([string]::IsNullOrWhiteSpace($PackagePath)) {
    $PackagePath = Join-Path $Cache $AssetName
}

$PackageValid = $false
if (Test-Path -LiteralPath $PackagePath) {
    $ActualSize = (Get-Item -LiteralPath $PackagePath).Length
    $ActualHash = Get-ScblSha256 -LiteralPath $PackagePath
    $PackageValid = $ActualSize -eq $ExpectedSize -and $ActualHash -eq $ExpectedSha256
    if (!$PackageValid) {
        if (![string]::IsNullOrWhiteSpace($env:EASYTIER_WINDOWS_PACKAGE)) {
            throw "Provided EasyTier package does not match the official asset digest."
        }
        Remove-Item -LiteralPath $PackagePath -Force
    }
}

if (!$PackageValid) {
    $Url = [string]$Asset.browser_download_url
    Write-Host "Downloading verified EasyTier package: $Url"
    Invoke-WebRequest -UseBasicParsing -Headers @{ 'User-Agent' = 'SCBL-EasyTier-Build/2' } -Uri $Url -OutFile $PackagePath
    $ActualSize = (Get-Item -LiteralPath $PackagePath).Length
    $ActualHash = Get-ScblSha256 -LiteralPath $PackagePath
    if ($ActualSize -ne $ExpectedSize -or $ActualHash -ne $ExpectedSha256) {
        Remove-Item -LiteralPath $PackagePath -Force -ErrorAction SilentlyContinue
        throw "Downloaded EasyTier package failed official size/SHA256 verification."
    }
}
else {
    Write-Host "Reusing verified EasyTier package: $PackagePath"
}

$Temp = Join-Path ([System.IO.Path]::GetTempPath()) ('scbl-easytier-win-' + [guid]::NewGuid().ToString('N'))
try {
    Expand-Archive -LiteralPath $PackagePath -DestinationPath $Temp -Force
    $Core = Get-ChildItem $Temp -Recurse -File -Filter 'easytier-core.exe' | Select-Object -First 1
    $Cli = Get-ChildItem $Temp -Recurse -File -Filter 'easytier-cli.exe' | Select-Object -First 1
    $Packet = Get-ChildItem $Temp -Recurse -File -Filter 'Packet.dll' | Select-Object -First 1
    $Wintun = Get-ChildItem $Temp -Recurse -File -Filter 'wintun.dll' | Select-Object -First 1
    if (!$Core -or !$Cli -or !$Packet -or !$Wintun) {
        throw 'Official package does not contain the complete EasyTier Windows runtime.'
    }

    Remove-Item -Recurse -Force $Bin -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force -Path $Bin | Out-Null
    Copy-Item -Force $Core.FullName $CorePath
    Copy-Item -Force $Cli.FullName $CliPath
    Get-ChildItem $Temp -Recurse -File | Where-Object { $_.Extension -in '.dll', '.sys' } | ForEach-Object {
        Copy-Item -Force $_.FullName (Join-Path $Bin $_.Name)
    }

    @"
EasyTier upstream binaries
Version: $Version
Architecture: $Arch
Project: EasyTier/EasyTier
License: LGPL-3.0
Official asset SHA256: $ExpectedSha256
The binaries are used unmodified as independent processes.
"@ | Set-Content -LiteralPath (Join-Path $Bin 'THIRD_PARTY_NOTICES_EASYTIER.txt') -Encoding UTF8

    $Version.TrimStart('v') | Set-Content -LiteralPath $VersionPath -Encoding ASCII -NoNewline
    "$Version|$Arch|$ExpectedSha256" | Set-Content -LiteralPath $Marker -Encoding ASCII -NoNewline
    & $CorePath --version
    & $CliPath --version
    Write-Host "EasyTier prepared and verified: $Version -> $Bin"
}
finally {
    Remove-Item -Recurse -Force $Temp -ErrorAction SilentlyContinue
}

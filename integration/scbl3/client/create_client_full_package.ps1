param(
    [string]$Version = "",
    [string]$OutputDir = (Join-Path -Path $PSScriptRoot -ChildPath "dist"),
    [string]$PublishRoot = "",
    [switch]$Fast
)

$ErrorActionPreference = "Stop"
$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
. (Join-Path $RepositoryRoot "scripts\lib\scbl-hash.ps1")

function Get-ScblSourceVersion {
    $VersionFile = Join-Path -Path $PSScriptRoot -ChildPath "..\VERSION_CLIENT"
    if (!(Test-Path -LiteralPath $VersionFile)) { throw "Client version file was not found: $VersionFile" }
    $Value = (Get-Content -LiteralPath $VersionFile -Raw -Encoding UTF8).Trim()
    if ($Value -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw "VERSION_CLIENT must contain a three-part numeric version." }
    return $Value
}

function Write-Step([string]$Message) { Write-Host "[SCBL] $Message" }

$SourceVersion = Get-ScblSourceVersion
$Version = $Version.Trim().TrimStart('v', 'V')
if ([string]::IsNullOrWhiteSpace($Version)) { $Version = $SourceVersion }
if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw "Version must use three numeric parts only." }
if ($Version -ne $SourceVersion) { throw "Requested version $Version does not match source version $SourceVersion." }

$Publish = if ([string]::IsNullOrWhiteSpace($PublishRoot)) {
    Join-Path $PSScriptRoot "ScblPublicLauncher\publish-single"
} else {
    [IO.Path]::GetFullPath($PublishRoot)
}
if (!(Test-Path -LiteralPath $Publish -PathType Container)) {
    throw "Publish root was not found: $Publish"
}
$Publish = (Resolve-Path -LiteralPath $Publish).Path
$BootstrapHook = Join-Path $Publish "components\hooks\uplay_r1_loader.dll"
$BootstrapHookSidecar = "$BootstrapHook.sha256"
$Required = @(
    (Join-Path $Publish "SCBL.exe"),
    (Join-Path $Publish "SCBL.Updater.exe"),
    (Join-Path $Publish "components\network\easytier-core.exe"),
    (Join-Path $Publish "components\network\easytier-cli.exe"),
    (Join-Path $Publish "components\network\Packet.dll"),
    (Join-Path $Publish "components\network\wintun.dll"),
    (Join-Path $Publish "components\route-guard\scbl-process-router.exe"),
    (Join-Path $Publish "components\route-guard\WinDivert.dll"),
    (Join-Path $Publish "components\route-guard\WinDivert64.payload.sys"),
    $BootstrapHook,
    $BootstrapHookSidecar,
    (Join-Path $Publish "components\hooks\mod\scbl_gameplay.dll"),
    (Join-Path $Publish "components\hooks\mod\scbl_gameplay.dll.sha256")
)
foreach ($File in $Required) {
    if (!(Test-Path -LiteralPath $File)) { throw "Publish output is incomplete. Missing: $File" }
}

$BootstrapExpectedMatch = [regex]::Match((Get-Content -LiteralPath $BootstrapHookSidecar -Raw -Encoding ASCII), '(?i)\b[0-9a-f]{64}\b')
if (!$BootstrapExpectedMatch.Success) { throw "Bootstrap Hooks checksum sidecar is invalid." }
$BootstrapExpected = $BootstrapExpectedMatch.Value.ToLowerInvariant()
$BootstrapActual = Get-ScblSha256 -LiteralPath $BootstrapHook
if ($BootstrapActual -ne $BootstrapExpected) {
    throw "Bootstrap Hooks checksum mismatch. expected=$BootstrapExpected actual=$BootstrapActual"
}

$ComponentVersionsPath = Join-Path $PSScriptRoot "..\COMPONENT_VERSIONS.json"
$ComponentVersionsSource = Get-Content -LiteralPath $ComponentVersionsPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ([string]$ComponentVersionsSource.clientVersion -ne $SourceVersion -or
    [string]$ComponentVersionsSource.launcherVersion -ne $SourceVersion) {
    throw "VERSION_CLIENT, clientVersion, and launcherVersion must be identical."
}
$EasyTierVersionFile = Join-Path $PSScriptRoot "easytier\bin\easytier-version.txt"
$EasyTierUpstreamVersion = if (Test-Path -LiteralPath $EasyTierVersionFile) {
    (Get-Content -LiteralPath $EasyTierVersionFile -Raw -Encoding ASCII).Trim()
} else {
    [string]$ComponentVersionsSource.easyTierVersion
}
$EasyTierVersion = if ($null -ne $ComponentVersionsSource.easyTierComponentVersion -and
    ![string]::IsNullOrWhiteSpace([string]$ComponentVersionsSource.easyTierComponentVersion)) {
    [string]$ComponentVersionsSource.easyTierComponentVersion
} else {
    $EasyTierUpstreamVersion
}
$ComponentVersions = [ordered]@{
    launcher = [string]$ComponentVersionsSource.launcherVersion
    hooks = [string]$ComponentVersionsSource.hooksVersion
    'route-guard' = [string]$ComponentVersionsSource.routeGuardVersion
    easytier = $EasyTierVersion
    updater = [string]$ComponentVersionsSource.updaterVersion
    'client-files' = [string]$ComponentVersionsSource.clientFilesVersion
}
foreach ($Entry in $ComponentVersions.GetEnumerator()) {
    if ($Entry.Value -notmatch '^[A-Za-z0-9][A-Za-z0-9._+-]{0,79}$' -or $Entry.Value -notmatch '\d') {
        throw "Invalid packaged component version: $($Entry.Key)=$($Entry.Value)"
    }
}

New-Item -ItemType Directory -Force -Path $OutputDir | Out-Null
$Zip = Join-Path $OutputDir ("SCBL-Client-v{0}-win-x86.zip" -f $Version)
if (Test-Path -LiteralPath $Zip) { Remove-Item -LiteralPath $Zip -Force }

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$Compression = if ($Fast) { [System.IO.Compression.CompressionLevel]::Fastest } else { [System.IO.Compression.CompressionLevel]::Optimal }
$ExcludedRoots = @('data', 'temp', 'logs', 'updates', 'backup', 'backup_saves')
$ExcludedFiles = @('launcher_settings.json', 'update_manifest.json', 'client_update_manifest.json', 'client_package_manifest.json', 'tools/SCBL.Updater.exe', 'tools/SCBL.Updater.payload.exe', 'tools/WinDivert64.sys')
$TrimChars = [char[]]@([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar)
$PublishPrefix = $Publish.TrimEnd($TrimChars) + [System.IO.Path]::DirectorySeparatorChar

$PackageFiles = New-Object System.Collections.Generic.List[object]
$PackageSources = @{}
$PackageFileHashes = @{}
foreach ($File in Get-ChildItem -LiteralPath $Publish -Recurse -File | Sort-Object FullName) {
    $Relative = $File.FullName.Substring($PublishPrefix.Length).Replace([char]92, [char]47)
    $Top = ($Relative -split '/', 2)[0]
    if ($ExcludedRoots -contains $Top) { continue }
    if ($ExcludedFiles -contains $Relative) { continue }
    $PackageFiles.Add([ordered]@{
        path = $Relative
        size = $File.Length
        sha256 = Get-ScblSha256 -LiteralPath $File.FullName
    })
    $PackageSources[$Relative] = $File.FullName
    $PackageFileHashes[$Relative] = Get-ScblSha256 -LiteralPath $File.FullName
}

# Seed the full package with cryptographically verified installed receipts. This
# lets a fresh full package prove that its unpacked components already match the
# component channel, instead of downloading every component again on first run.
$ComponentReceipts = [ordered]@{}
$ComponentMetadataRoot = Join-Path ([IO.Path]::GetTempPath()) ("scbl-full-receipts-" + [Guid]::NewGuid().ToString("N"))
try {
    & (Join-Path $PSScriptRoot "..\scripts\release\assemble-client-components.ps1") `
        -PublishRoot $Publish `
        -OutputRoot $ComponentMetadataRoot
    if (!$?) { throw "Component receipt assembly failed." }

    foreach ($Component in @('launcher','hooks','route-guard','easytier','updater','client-files')) {
        $MetadataPath = Join-Path $ComponentMetadataRoot "$Component\component.json"
        if (!(Test-Path -LiteralPath $MetadataPath -PathType Leaf)) {
            throw "Component metadata is missing for full-package receipt: $Component"
        }
        $Metadata = Get-Content -LiteralPath $MetadataPath -Raw -Encoding UTF8 | ConvertFrom-Json
        $InstalledPaths = @()
        $DeletePaths = $null
        switch ($Component) {
            'launcher' { $InstalledPaths = @('SCBL.exe') }
            'hooks' { $InstalledPaths = @('components/hooks/uplay_r1_loader.dll') }
            'route-guard' { $InstalledPaths = @('components/route-guard/scbl-process-router.exe','components/route-guard/WinDivert.dll','components/route-guard/WinDivert64.payload.sys') }
            'easytier' { $InstalledPaths = @('components/network/easytier-core.exe','components/network/easytier-cli.exe','components/network/Packet.dll','components/network/wintun.dll') }
            'updater' { $InstalledPaths = @('SCBL.Updater.exe') }
            'client-files' {
                $DistributionRoot = Join-Path $PSScriptRoot 'distribution'
                $RulesPath = Join-Path $DistributionRoot 'client-files.rules.json'
                $Rules = Get-Content -LiteralPath $RulesPath -Raw -Encoding UTF8 | ConvertFrom-Json
                if ([int]$Rules.schemaVersion -ne 1) { throw "Unsupported client file rules schema." }
                $DeletePaths = @($Rules.delete | ForEach-Object { [string]$_ })
                $DistributionPrefix = $DistributionRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
                $InstalledPaths = @(Get-ChildItem -LiteralPath $DistributionRoot -Recurse -File |
                    Where-Object { !$_.FullName.Equals($RulesPath, [StringComparison]::OrdinalIgnoreCase) } |
                    ForEach-Object { $_.FullName.Substring($DistributionPrefix.Length).Replace('\','/') })
                $InstalledPaths += @(
                    'components/hooks/mod/scbl_gameplay.dll',
                    'components/hooks/mod/scbl_gameplay.dll.sha256'
                )
            }
        }
        $InstalledHashes = [ordered]@{}
        foreach ($Relative in $InstalledPaths) {
            if (!$PackageFileHashes.ContainsKey($Relative)) {
                throw "Full package receipt target is missing from the package: $Component -> $Relative"
            }
            $InstalledHashes[$Relative] = [string]$PackageFileHashes[$Relative]
        }
        $Receipt = [ordered]@{
            version = [string]$Metadata.version
            sha256 = [string]$Metadata.sha256
            files = $InstalledHashes
        }
        if ($null -ne $DeletePaths) { $Receipt.delete = $DeletePaths }
        $ComponentReceipts[$Component] = $Receipt
    }
}
finally {
    Remove-Item -LiteralPath $ComponentMetadataRoot -Recurse -Force -ErrorAction SilentlyContinue
}

$PackageManifest = [ordered]@{
    schemaVersion = 1
    clientVersion = $Version
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    bootstrapHooksSha256 = $BootstrapActual
    componentVersions = $ComponentVersions
    componentReceipts = $ComponentReceipts
    files = $PackageFiles
}
$ManifestJson = $PackageManifest | ConvertTo-Json -Depth 6

Write-Step "Creating ZIP from independently built and verified component outputs..."
$Stream = [System.IO.File]::Open($Zip, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
try {
    $Archive = New-Object System.IO.Compression.ZipArchive($Stream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        foreach ($Item in $PackageFiles) {
            $SourcePath = $PackageSources[[string]$Item.path]
            $Entry = $Archive.CreateEntry($Item.path, $Compression)
            $Input = [System.IO.File]::OpenRead($SourcePath)
            try {
                $Output = $Entry.Open()
                try { $Input.CopyTo($Output) }
                finally { $Output.Dispose() }
            }
            finally { $Input.Dispose() }
        }

        $ManifestEntry = $Archive.CreateEntry('client_package_manifest.json', $Compression)
        $Writer = New-Object System.IO.StreamWriter($ManifestEntry.Open(), [System.Text.UTF8Encoding]::new($false))
        try { $Writer.Write($ManifestJson) }
        finally { $Writer.Dispose() }
    }
    finally { $Archive.Dispose() }
}
finally { $Stream.Dispose() }

if (!(Test-Path -LiteralPath $Zip) -or (Get-Item -LiteralPath $Zip).Length -le 0) { throw "Client ZIP was not created." }
$VerifyArchive = [System.IO.Compression.ZipFile]::OpenRead($Zip)
try {
    $Names = @($VerifyArchive.Entries | ForEach-Object { $_.FullName })
    if ($Names | Where-Object { $_ -match '^tools/' }) { throw "Release ZIP must not contain the legacy flat tools directory." }
    if ($Names | Where-Object { $_ -match '^(data|temp|logs|updates|backup|backup_saves)/' }) {
        throw "Release ZIP must not contain generated client state directories."
    }
    foreach ($RequiredEntry in @(
        'SCBL.exe',
        'SCBL.Updater.exe',
        'components/route-guard/WinDivert64.payload.sys',
        'components/hooks/uplay_r1_loader.dll',
        'components/hooks/uplay_r1_loader.dll.sha256',
        'components/hooks/mod/scbl_gameplay.dll',
        'components/hooks/mod/scbl_gameplay.dll.sha256',
        'client_package_manifest.json')) {
        if ($Names -notcontains $RequiredEntry) { throw "Release ZIP is missing: $RequiredEntry" }
    }
}
finally { $VerifyArchive.Dispose() }

$ZipHash = Get-ScblSha256 -LiteralPath $Zip
"$ZipHash  $([System.IO.Path]::GetFileName($Zip))" | Set-Content -LiteralPath "$Zip.sha256" -Encoding ASCII
Write-Step "Client full package assembled without recompiling components: $Zip"
Write-Step "SHA256: $ZipHash"

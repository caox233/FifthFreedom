param(
    [Parameter(Mandatory=$true)][string]$PublishRoot,
    [Parameter(Mandatory=$true)][string]$OutputRoot,
    [string]$Commit = "",
    [string[]]$Components = @()
)

$ErrorActionPreference = "Stop"
$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).Path
. (Join-Path $RepositoryRoot "scripts\lib\scbl-hash.ps1")
$PublishRoot = (Resolve-Path $PublishRoot).Path
$Versions = Get-Content -LiteralPath (Join-Path $RepositoryRoot "COMPONENT_VERSIONS.json") -Raw -Encoding UTF8 | ConvertFrom-Json
$ClientVersion = (Get-Content -LiteralPath (Join-Path $RepositoryRoot "VERSION_CLIENT") -Raw -Encoding UTF8).Trim()
if ([string]$Versions.clientVersion -ne $ClientVersion -or
    [string]$Versions.launcherVersion -ne $ClientVersion) {
    throw "VERSION_CLIENT, clientVersion, and launcherVersion must be identical."
}
$CatalogPath = Join-Path $RepositoryRoot 'server\manager\scblctl\client_component_catalog.json'
$Catalog = Get-Content -LiteralPath $CatalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ([int]$Catalog.schemaVersion -ne 1 -or $null -eq $Catalog.components) {
    throw "Invalid client component catalog: $CatalogPath"
}

function Get-ComponentSpec {
    param([Parameter(Mandatory=$true)][string]$Name)
    $Property = $Catalog.components.PSObject.Properties | Where-Object { $_.Name -ceq $Name } | Select-Object -First 1
    if ($null -eq $Property) { throw "Unknown client component: $Name" }
    $Spec = $Property.Value
    $FileName = [string]$Spec.fileName
    $UpdateMode = [string]$Spec.updateMode
    $Section = [string]$Spec.section
    if ([string]::IsNullOrWhiteSpace($FileName) -or
        [IO.Path]::GetFileName($FileName) -ne $FileName -or
        [string]::IsNullOrWhiteSpace($UpdateMode) -or
        $Section -notin @('components', 'files')) {
        throw "Invalid client component catalog record: $Name"
    }
    return $Spec
}

if ($Components.Count -eq 0) {
    $Components = @($Catalog.components.PSObject.Properties | ForEach-Object { $_.Name })
}
$Components = @($Components | Select-Object -Unique)
foreach ($Component in $Components) { [void](Get-ComponentSpec $Component) }
$EasyTierVersionFile = Join-Path $RepositoryRoot "client\easytier\bin\easytier-version.txt"
$EasyTierVersion = if (Test-Path -LiteralPath $EasyTierVersionFile) {
    (Get-Content -LiteralPath $EasyTierVersionFile -Raw -Encoding ASCII).Trim()
} else {
    [string]$Versions.easyTierVersion
}
if ($EasyTierVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') {
    throw "Prepared EasyTier version marker is invalid: $EasyTierVersion"
}
$EasyTierComponentVersion = if ($null -ne $Versions.easyTierComponentVersion -and
    ![string]::IsNullOrWhiteSpace([string]$Versions.easyTierComponentVersion)) {
    [string]$Versions.easyTierComponentVersion
} else {
    $EasyTierVersion
}

function New-DeterministicZip {
    param([string]$Destination, [hashtable]$Files)
    Add-Type -AssemblyName System.IO.Compression
    $Stream = [System.IO.File]::Open($Destination, [System.IO.FileMode]::Create, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
    try {
        $Archive = [System.IO.Compression.ZipArchive]::new($Stream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
        try {
            foreach ($Name in @($Files.Keys | Sort-Object)) {
                $Entry = $Archive.CreateEntry($Name, [System.IO.Compression.CompressionLevel]::Optimal)
                $Entry.LastWriteTime = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)
                $Input = [System.IO.File]::OpenRead($Files[$Name])
                $Output = $Entry.Open()
                try { $Input.CopyTo($Output) }
                finally { $Output.Dispose(); $Input.Dispose() }
            }
        }
        finally { $Archive.Dispose() }
    }
    finally { $Stream.Dispose() }
}

function Get-SafeClientRelativePath {
    param([Parameter(Mandatory=$true)][string]$Value)
    $Normalized = $Value.Trim().Replace('\', '/')
    if ([string]::IsNullOrWhiteSpace($Normalized) -or
        [IO.Path]::IsPathRooted($Normalized) -or
        $Normalized.StartsWith('/') -or
        $Normalized.Contains(':') -or
        @($Normalized.Split('/') | Where-Object { $_ -eq '..' -or $_ -eq '.' -or [string]::IsNullOrWhiteSpace($_) }).Count -gt 0) {
        throw "Unsafe client file path: $Value"
    }
    return $Normalized
}

function Write-Component {
    param(
        [string]$Name,
        [string]$Version,
        [string]$Source
    )
    $Spec = Get-ComponentSpec $Name
    $FileName = [string]$Spec.fileName
    $UpdateMode = [string]$Spec.updateMode
    if ($Version -notmatch '^[A-Za-z0-9][A-Za-z0-9._+-]{0,79}$' -or $Version -notmatch '\d') {
        throw "Invalid component version: $Name=$Version"
    }
    $Directory = Join-Path $OutputRoot $Name
    New-Item -ItemType Directory -Force -Path $Directory | Out-Null
    $Target = Join-Path $Directory $FileName
    Copy-Item -LiteralPath $Source -Destination $Target -Force
    $Hash = Get-ScblSha256 -LiteralPath $Target
    "$Hash  $FileName" | Set-Content -LiteralPath "$Target.sha256" -Encoding ASCII
    $ComponentMetadata = [ordered]@{
        schemaVersion = 2
        component = $Name
        version = $Version
        commit = $Commit
        file = $FileName
        sha256 = $Hash
        size = (Get-Item -LiteralPath $Target).Length
        updateMode = $UpdateMode
    } | ConvertTo-Json -Depth 5
    [IO.File]::WriteAllText(
        (Join-Path $Directory "component.json"),
        $ComponentMetadata + "`n",
        [Text.UTF8Encoding]::new($false))
}

New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$HooksRoot = Join-Path $PublishRoot "components\hooks"
$NetworkRoot = Join-Path $PublishRoot "components\network"
$RouteGuardRoot = Join-Path $PublishRoot "components\route-guard"

foreach ($Component in $Components) {
    Remove-Item -LiteralPath (Join-Path $OutputRoot $Component) -Recurse -Force -ErrorAction SilentlyContinue
}

if ($Components -contains 'launcher') {
    Write-Component "launcher" ([string]$Versions.launcherVersion) (Join-Path $PublishRoot "SCBL.exe")
}
if ($Components -contains 'hooks') {
    Write-Component "hooks" ([string]$Versions.hooksVersion) (Join-Path $HooksRoot "uplay_r1_loader.dll")
}
if ($Components -contains 'updater') {
    Write-Component "updater" ([string]$Versions.updaterVersion) (Join-Path $PublishRoot "SCBL.Updater.exe")
}

$Work = Join-Path ([System.IO.Path]::GetTempPath()) ("scbl-components-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force -Path $Work | Out-Null
try {
    if ($Components -contains 'route-guard') {
        $RouteZip = Join-Path $Work ([string](Get-ComponentSpec 'route-guard').fileName)
        New-DeterministicZip $RouteZip @{
            "scbl-process-router.exe" = (Join-Path $RouteGuardRoot "scbl-process-router.exe")
            "WinDivert.dll" = (Join-Path $RouteGuardRoot "WinDivert.dll")
            "WinDivert64.sys" = (Join-Path $RouteGuardRoot "WinDivert64.payload.sys")
        }
        Write-Component "route-guard" ([string]$Versions.routeGuardVersion) $RouteZip
    }

    if ($Components -contains 'easytier') {
        $EasyTierZip = Join-Path $Work ([string](Get-ComponentSpec 'easytier').fileName)
        New-DeterministicZip $EasyTierZip @{
            "easytier-core.exe" = (Join-Path $NetworkRoot "easytier-core.exe")
            "easytier-cli.exe" = (Join-Path $NetworkRoot "easytier-cli.exe")
            "Packet.dll" = (Join-Path $NetworkRoot "Packet.dll")
            "wintun.dll" = (Join-Path $NetworkRoot "wintun.dll")
        }
        Write-Component "easytier" $EasyTierComponentVersion $EasyTierZip
    }

    if ($Components -contains 'client-files') {
        $ClientFilesZip = Join-Path $Work ([string](Get-ComponentSpec 'client-files').fileName)
        $DistributionRoot = Join-Path $RepositoryRoot "client\distribution"
        $RulesPath = Join-Path $DistributionRoot "client-files.rules.json"
        if (!(Test-Path -LiteralPath $RulesPath -PathType Leaf)) {
            throw "Client file rules are missing: $RulesPath"
        }
        $Rules = Get-Content -LiteralPath $RulesPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ([int]$Rules.schemaVersion -ne 1) { throw "Unsupported client file rules schema." }

        $BundleFiles = @{}
        $FileRecords = @()
        $DistributionPrefix = $DistributionRoot.TrimEnd('\', '/') + [IO.Path]::DirectorySeparatorChar
        foreach ($SourceFile in Get-ChildItem -LiteralPath $DistributionRoot -Recurse -File | Sort-Object FullName) {
            if ($SourceFile.FullName.Equals($RulesPath, [StringComparison]::OrdinalIgnoreCase)) { continue }
            $Relative = Get-SafeClientRelativePath ($SourceFile.FullName.Substring($DistributionPrefix.Length))
            $Digest = Get-ScblSha256 -LiteralPath $SourceFile.FullName
            $BundleFiles["files/$Relative"] = $SourceFile.FullName
            $FileRecords += [ordered]@{
                path = $Relative
                size = $SourceFile.Length
                sha256 = $Digest
            }
        }
        foreach ($Relative in @(
            'components/hooks/mod/scbl_gameplay.dll',
            'components/hooks/mod/scbl_gameplay.dll.sha256'
        )) {
            $SourceFile = Join-Path $PublishRoot $Relative
            if (!(Test-Path -LiteralPath $SourceFile -PathType Leaf)) {
                throw "Required MOD gameplay payload is missing: $SourceFile"
            }
            $Digest = Get-ScblSha256 -LiteralPath $SourceFile
            $BundleFiles["files/$Relative"] = $SourceFile
            $FileRecords += [ordered]@{
                path = $Relative
                size = (Get-Item -LiteralPath $SourceFile).Length
                sha256 = $Digest
            }
        }
        $DeleteRecords = @()
        foreach ($DeletePath in @($Rules.delete)) {
            $Relative = Get-SafeClientRelativePath ([string]$DeletePath)
            if (@($FileRecords | Where-Object { $_.path -eq $Relative }).Count -gt 0) {
                throw "Client file cannot be installed and deleted in the same bundle: $Relative"
            }
            if ($DeleteRecords -notcontains $Relative) { $DeleteRecords += $Relative }
        }
        if ($FileRecords.Count -eq 0 -and $DeleteRecords.Count -eq 0) {
            throw "Client file bundle has no install or delete operations."
        }
        $BundleManifestPath = Join-Path $Work "client-files-manifest.json"
        $BundleManifest = [ordered]@{
            schemaVersion = 1
            files = $FileRecords
            delete = $DeleteRecords
        } | ConvertTo-Json -Depth 6
        [IO.File]::WriteAllText($BundleManifestPath, $BundleManifest, [Text.UTF8Encoding]::new($false))
        $BundleFiles["client-files-manifest.json"] = $BundleManifestPath
        New-DeterministicZip $ClientFilesZip $BundleFiles
        Write-Component "client-files" ([string]$Versions.clientFilesVersion) $ClientFilesZip
    }
}
finally {
    Remove-Item -LiteralPath $Work -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "Client components assembled: $($Components -join ', ') -> $OutputRoot"

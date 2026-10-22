param(
    [Parameter(Mandatory=$true)][string]$ComponentRoot,
    [Parameter(Mandatory=$true)][string]$Output,
    [Parameter(Mandatory=$true)][string[]]$Components
)

$ErrorActionPreference = 'Stop'
$RepositoryRoot = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
. (Join-Path $RepositoryRoot 'scripts\lib\scbl-hash.ps1')
$CatalogPath = Join-Path $RepositoryRoot 'server\manager\scblctl\client_component_catalog.json'
$Catalog = Get-Content -LiteralPath $CatalogPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ([int]$Catalog.schemaVersion -ne 1 -or $null -eq $Catalog.components) {
    throw "Invalid client component catalog: $CatalogPath"
}

function Get-ComponentSpec {
    param([Parameter(Mandatory=$true)][string]$Name)
    $Property = $Catalog.components.PSObject.Properties | Where-Object { $_.Name -ceq $Name } | Select-Object -First 1
    if ($null -eq $Property) { throw "Unknown client component: $Name" }
    return $Property.Value
}

$ComponentRoot = (Resolve-Path -LiteralPath $ComponentRoot).Path
if ([System.IO.Path]::GetExtension($Output) -notin @('.zip', '.scblpatch')) {
    throw 'Client component patch output must end with .zip.'
}

$items = New-Object System.Collections.Generic.List[object]
foreach ($name in @($Components | Select-Object -Unique)) {
    $spec = Get-ComponentSpec $name
    $directory = Join-Path $ComponentRoot $name
    $metadataPath = Join-Path $directory 'component.json'
    if (!(Test-Path -LiteralPath $metadataPath)) { throw "Missing component metadata: $metadataPath" }
    $metadata = Get-Content -LiteralPath $metadataPath -Raw -Encoding UTF8 | ConvertFrom-Json
    if ([string]$metadata.component -ne $name) { throw "Component metadata mismatch: $name" }
    if ([string]$metadata.file -cne [string]$spec.fileName -or
        [string]$metadata.updateMode -cne [string]$spec.updateMode) {
        throw "Component contract mismatch: $name"
    }
    $file = Join-Path $directory ([string]$metadata.file)
    if (!(Test-Path -LiteralPath $file)) { throw "Missing component file: $file" }
    $actualHash = Get-ScblSha256 -LiteralPath $file
    if ($actualHash -ne ([string]$metadata.sha256).ToLowerInvariant()) { throw "Component hash mismatch: $name" }
    $items.Add([ordered]@{
        component = $name
        version = [string]$metadata.version
        path = "artifacts/$name/$([string]$metadata.file)"
        file = [string]$metadata.file
        updateMode = [string]$metadata.updateMode
        size = (Get-Item -LiteralPath $file).Length
        sha256 = $actualHash
        source = $file
    })
}

$manifest = [ordered]@{
    schemaVersion = 1
    packageType = 'scbl-client-components-patch'
    generatedAt = [DateTimeOffset]::UtcNow.ToString('o')
    components = @($items | ForEach-Object {
        [ordered]@{
            component = $_.component
            version = $_.version
            path = $_.path
            file = $_.file
            updateMode = $_.updateMode
            size = $_.size
            sha256 = $_.sha256
        }
    })
}

$parent = Split-Path -Parent $Output
if (![string]::IsNullOrWhiteSpace($parent)) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
if (Test-Path -LiteralPath $Output) { Remove-Item -LiteralPath $Output -Force }
Add-Type -AssemblyName System.IO.Compression
$stream = [System.IO.File]::Open($Output, [System.IO.FileMode]::CreateNew, [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
try {
    $archive = [System.IO.Compression.ZipArchive]::new($stream, [System.IO.Compression.ZipArchiveMode]::Create, $false)
    try {
        $manifestEntry = $archive.CreateEntry('scbl-client-patch.json', [System.IO.Compression.CompressionLevel]::Optimal)
        $writer = [System.IO.StreamWriter]::new($manifestEntry.Open(), [System.Text.UTF8Encoding]::new($false))
        try { $writer.Write(($manifest | ConvertTo-Json -Depth 7)) } finally { $writer.Dispose() }
        foreach ($item in $items) {
            $entry = $archive.CreateEntry([string]$item.path, [System.IO.Compression.CompressionLevel]::Optimal)
            $input = [System.IO.File]::OpenRead([string]$item.source)
            $outputStream = $entry.Open()
            try { $input.CopyTo($outputStream) } finally { $outputStream.Dispose(); $input.Dispose() }
        }
    }
    finally { $archive.Dispose() }
}
finally { $stream.Dispose() }

$hash = Get-ScblSha256 -LiteralPath $Output
"$hash  $([System.IO.Path]::GetFileName($Output))" | Set-Content -LiteralPath "$Output.sha256" -Encoding ASCII
Write-Host "Client component patch created: $Output"
Write-Host "Components: $($Components -join ', ')"
Write-Host "SHA256: $hash"

param(
    [string]$SourceDll = "",
    [string]$PublishRoot = (Join-Path $PSScriptRoot "ScblPublicLauncher\publish-single")
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $RepoRoot "scripts\lib\scbl-hash.ps1")

if ([string]::IsNullOrWhiteSpace($SourceDll)) {
    $SourceDll = Join-Path $RepoRoot "target\i686-pc-windows-msvc\release\hooks.dll"
}
$SourceDll = [System.IO.Path]::GetFullPath($SourceDll)

if (!(Test-Path -LiteralPath $SourceDll -PathType Leaf)) {
    Write-Host "Local Hooks release is missing; building it from client/hooks..."
    & (Join-Path $RepoRoot "scripts\build-rust-components.ps1") -Release -HooksOnly
    if (-not $?) { throw "Local Hooks build failed." }
}
if (!(Test-Path -LiteralPath $SourceDll -PathType Leaf)) {
    throw "Local Hooks output is missing: $SourceDll"
}

$Header = [System.IO.File]::ReadAllBytes($SourceDll)
if ($Header.Length -lt 2 -or $Header[0] -ne 0x4D -or $Header[1] -ne 0x5A) {
    throw "Local Hooks output is not a Windows PE file: $SourceDll"
}
$ProtocolMarker = 'SCBL_HOOKS_CONFIG=scbl.toml.v1'
$HasProtocolMarker = [System.Text.Encoding]::ASCII.GetString($Header).Contains($ProtocolMarker)
if (!$HasProtocolMarker) {
    throw "Local Hooks output does not support the current scbl.toml protocol: $SourceDll"
}

$Destination = Join-Path $PublishRoot "components\hooks"
New-Item -ItemType Directory -Force -Path $Destination | Out-Null
$TargetDll = Join-Path $Destination "uplay_r1_loader.dll"
$TargetChecksum = Join-Path $Destination "uplay_r1_loader.dll.sha256"
$TemporaryTarget = "$TargetDll.new"
$ActualHash = Get-ScblSha256 -LiteralPath $SourceDll

try {
    Copy-Item -Force $SourceDll $TemporaryTarget
    if ((Get-ScblSha256 -LiteralPath $TemporaryTarget) -ne $ActualHash) {
        throw "Bootstrap Hooks temporary copy hash mismatch."
    }
    Move-Item -Force $TemporaryTarget $TargetDll
    "$ActualHash  uplay_r1_loader.dll" | Set-Content -LiteralPath $TargetChecksum -Encoding ASCII
}

finally {
    Remove-Item -LiteralPath $TemporaryTarget -Force -ErrorAction SilentlyContinue
}

$GameplaySource = Join-Path $RepoRoot "target\i686-pc-windows-msvc\release\scbl_gameplay.dll"
if (!(Test-Path -LiteralPath $GameplaySource -PathType Leaf)) {
    throw "Gameplay DLL output is missing: $GameplaySource"
}
$GameplayBytes = [IO.File]::ReadAllBytes($GameplaySource)
$GameplayText = [Text.Encoding]::ASCII.GetString($GameplayBytes)
if ($GameplayBytes.Length -lt 2 -or $GameplayBytes[0] -ne 0x4D -or $GameplayBytes[1] -ne 0x5A -or
    !$GameplayText.Contains('scbl_plugin_initialize') -or !$GameplayText.Contains('scbl_plugin_shutdown')) {
    throw "Gameplay output is not a compatible Windows plugin DLL: $GameplaySource"
}
$GameplayDestination = Join-Path $Destination "mod"
New-Item -ItemType Directory -Force -Path $GameplayDestination | Out-Null
$GameplayTarget = Join-Path $GameplayDestination "scbl_gameplay.dll"
$GameplayHash = Get-ScblSha256 -LiteralPath $GameplaySource
Copy-Item -Force $GameplaySource $GameplayTarget
if ((Get-ScblSha256 -LiteralPath $GameplayTarget) -ne $GameplayHash) {
    throw "Gameplay DLL copy hash mismatch."
}
"$GameplayHash  scbl_gameplay.dll" | Set-Content -LiteralPath "$GameplayTarget.sha256" -Encoding ASCII

# Do not let an incremental build retain the pre-2.0 package layout and ship
# a second Hooks copy. The game-directory deployment target remains unchanged.
$LegacyBootstrapRoot = Join-Path $PublishRoot "bootstrap-components"
if (Test-Path -LiteralPath $LegacyBootstrapRoot) {
    Remove-Item -LiteralPath $LegacyBootstrapRoot -Recurse -Force
}

Write-Host "Bootstrap Hooks prepared from SCBL client/hooks: $TargetDll"
Write-Host "SHA256: $ActualHash"
Write-Host "Gameplay DLL prepared: $GameplayTarget"
Write-Host "SHA256: $GameplayHash"

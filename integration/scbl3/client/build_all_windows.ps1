param(
    [string]$HooksDll = "",
    [switch]$Fast,
    [switch]$Clean,
    [switch]$Package,
    [switch]$Auto,
    [switch]$LauncherOnly,
    [switch]$HooksOnly,
    [switch]$UpdaterOnly,
    [switch]$RouterOnly,
    [switch]$RuntimeOnly,
    [string]$OutputDir = (Join-Path -Path $PSScriptRoot -ChildPath "dist")
)

$ErrorActionPreference = "Stop"
if ($Fast) { $env:SCBL_FAST_BUILD = "1" }
if ($Clean) { $env:SCBL_CLEAN_BUILD = "1" }

$Root = $PSScriptRoot
$RepositoryRoot = (Resolve-Path (Join-Path $Root "..")).Path
. (Join-Path $RepositoryRoot "scripts\lib\scbl-hash.ps1")
$Publish = Join-Path $Root "ScblPublicLauncher\publish-single"
$Components = Join-Path $Publish "components"
$HooksComponent = Join-Path $Components "hooks"
$HooksModComponent = Join-Path $HooksComponent "mod"
$NetworkComponent = Join-Path $Components "network"
$RouteGuardComponent = Join-Path $Components "route-guard"
$Timings = [ordered]@{}

function Invoke-Step {
    param(
        [Parameter(Mandatory=$true)][string]$Name,
        [Parameter(Mandatory=$true)][string]$ScriptPath,
        [hashtable]$Arguments = @{}
    )
    $Elapsed = Measure-Command {
        & $ScriptPath @Arguments
        if (-not $?) { throw "Step failed: $ScriptPath" }
    }
    $Timings[$Name] = [math]::Round($Elapsed.TotalSeconds, 1)
}

function Get-AutoChangedFiles {
    $Files = New-Object System.Collections.Generic.HashSet[string]([System.StringComparer]::OrdinalIgnoreCase)
    if (!(Get-Command git -ErrorAction SilentlyContinue)) {
        throw "Auto build requires Git so changed components can be identified safely."
    }
    try {
        $RepoRoot = (& git -C $PSScriptRoot rev-parse --show-toplevel 2>$null).Trim()
        if ($LASTEXITCODE -ne 0 -or [string]::IsNullOrWhiteSpace($RepoRoot)) {
            throw "The repository root could not be resolved."
        }
        foreach ($Args in @(
            @('-C', $RepoRoot, 'diff', '--name-only'),
            @('-C', $RepoRoot, 'diff', '--cached', '--name-only'),
            @('-C', $RepoRoot, 'ls-files', '--others', '--exclude-standard')
        )) {
            $Output = @(& git @Args 2>$null)
            if ($LASTEXITCODE -ne 0) {
                throw "Git change detection failed: git $($Args -join ' ')"
            }
            foreach ($Line in $Output) {
                $Value = ([string]$Line).Trim().Replace('\','/')
                if (![string]::IsNullOrWhiteSpace($Value)) { [void]$Files.Add($Value) }
            }
        }
    }
    catch {
        throw "Auto build could not determine the changed component set: $($_.Exception.Message)"
    }
    return @($Files)
}

function Copy-AvailableOutputs {
    New-Item -ItemType Directory -Force -Path $Publish, $HooksComponent, $HooksModComponent, $NetworkComponent, $RouteGuardComponent | Out-Null
    Remove-Item -LiteralPath (Join-Path $Publish "tools") -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -LiteralPath (Join-Path $Publish "WINDIVERT_NOTICE.txt") -Force -ErrorAction SilentlyContinue

    $EasyTierBin = Join-Path $Root "easytier\bin"
    if ($PrepareRuntime -and (Test-Path -LiteralPath $EasyTierBin)) {
        Get-ChildItem $EasyTierBin -File |
            Where-Object { !$_.Name.StartsWith('.scbl-prepared-', [System.StringComparison]::OrdinalIgnoreCase) } |
            ForEach-Object { Copy-Item -Force $_.FullName (Join-Path $NetworkComponent $_.Name) }
    }
    $EasyTierLicense = Join-Path (Split-Path $Root -Parent) "THIRD_PARTY_LICENSES\EasyTier-LGPL-3.0.txt"
    if ($PrepareRuntime -and (Test-Path -LiteralPath $EasyTierLicense)) { Copy-Item -Force $EasyTierLicense (Join-Path $NetworkComponent "EasyTier-LGPL-3.0.txt") }

    $RouterSource = Join-Path $Root "scbl-process-router\scbl-process-router.exe"
    $WinDivertDll = Join-Path $Root "scbl-process-router\WinDivert.dll"
    $WinDivertSys = Join-Path $Root "scbl-process-router\WinDivert64.sys"
    if ($BuildRouter -and (Test-Path -LiteralPath $RouterSource)) { Copy-Item -Force $RouterSource (Join-Path $RouteGuardComponent "scbl-process-router.exe") }
    if ($BuildRouter -and (Test-Path -LiteralPath $WinDivertDll)) { Copy-Item -Force $WinDivertDll (Join-Path $RouteGuardComponent "WinDivert.dll") }
    if ($BuildRouter -and (Test-Path -LiteralPath $WinDivertSys)) {
        Copy-Item -Force $WinDivertSys (Join-Path $RouteGuardComponent "WinDivert64.payload.sys")
    }

    $WinDivertNotice = Join-Path $Root "WINDIVERT_NOTICE.txt"
    if ($BuildRouter -and (Test-Path -LiteralPath $WinDivertNotice)) { Copy-Item -Force $WinDivertNotice (Join-Path $RouteGuardComponent "WINDIVERT_NOTICE.txt") }

    $UpdaterBuild = Join-Path $Root "SCBL.Updater\publish\SCBL.Updater.exe"
    if (($BuildUpdater -or $Package) -and (Test-Path -LiteralPath $UpdaterBuild)) {
        Copy-Item -Force $UpdaterBuild (Join-Path $Publish "SCBL.Updater.exe")
    }

    if ($BuildHooks) {
        $HooksBuild = Join-Path (Split-Path $Root -Parent) "target\i686-pc-windows-msvc\release\hooks.dll"
        if (!(Test-Path -LiteralPath $HooksBuild)) { throw "Hooks build output is missing: $HooksBuild" }
        $HooksTarget = Join-Path $HooksComponent "uplay_r1_loader.dll"
        Copy-Item -Force $HooksBuild $HooksTarget
        $HooksHash = Get-ScblSha256 -LiteralPath $HooksTarget
        "$HooksHash  uplay_r1_loader.dll" | Set-Content -LiteralPath "$HooksTarget.sha256" -Encoding ASCII

        $GameplayBuild = Join-Path (Split-Path $Root -Parent) "target\i686-pc-windows-msvc\release\scbl_gameplay.dll"
        if (!(Test-Path -LiteralPath $GameplayBuild)) { throw "Gameplay DLL build output is missing: $GameplayBuild" }
        $GameplayTarget = Join-Path $HooksModComponent "scbl_gameplay.dll"
        Copy-Item -Force $GameplayBuild $GameplayTarget
        $GameplayHash = Get-ScblSha256 -LiteralPath $GameplayTarget
        "$GameplayHash  scbl_gameplay.dll" | Set-Content -LiteralPath "$GameplayTarget.sha256" -Encoding ASCII

        foreach ($LegacyPlugin in @('scbl_mod_runtime.dll','scbl_diagnostics.dll','scbl_diagnostics_runtime.dll')) {
            Remove-Item -LiteralPath (Join-Path $HooksModComponent $LegacyPlugin) -Force -ErrorAction SilentlyContinue
            Remove-Item -LiteralPath (Join-Path $HooksModComponent "$LegacyPlugin.sha256") -Force -ErrorAction SilentlyContinue
        }
    }

    Remove-Item -Force (Join-Path $Publish "launcher_settings.example.json") -ErrorAction SilentlyContinue
    Remove-Item -Force (Join-Path $Publish "launcher_background.mp4") -ErrorAction SilentlyContinue
    Get-ChildItem -LiteralPath $NetworkComponent -Filter ".scbl-prepared-*" -File -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

$VersionFile = Join-Path $Root "..\VERSION_CLIENT"
if (!(Test-Path -LiteralPath $VersionFile)) { throw "Client version file was not found: $VersionFile" }
$ScblVersion = (Get-Content -LiteralPath $VersionFile -Raw -Encoding UTF8).Trim()
if ($ScblVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+$') { throw "VERSION_CLIENT must contain a three-part numeric version." }
$ComponentVersionsPath = Join-Path $Root "..\COMPONENT_VERSIONS.json"
$ComponentVersions = Get-Content -LiteralPath $ComponentVersionsPath -Raw -Encoding UTF8 | ConvertFrom-Json
if ([string]$ComponentVersions.clientVersion -ne $ScblVersion -or
    [string]$ComponentVersions.launcherVersion -ne $ScblVersion) {
    throw "VERSION_CLIENT, clientVersion, and launcherVersion must be identical."
}

$BuildLauncher = $false
$BuildHooks = $false
$BuildUpdater = $false
$BuildRouter = $false
$PrepareRuntime = $false
$ExplicitComponent = $LauncherOnly -or $HooksOnly -or $UpdaterOnly -or $RouterOnly -or $RuntimeOnly

if (!$Auto -and !$ExplicitComponent -and !$Package) {
    throw "No build scope selected. Use -Auto, one or more *Only switches, or -Package for an explicit full build."
}

if ($Auto -or $ExplicitComponent) {
    $BuildLauncher = $false
    $BuildUpdater = $false
    $BuildRouter = $false
    $PrepareRuntime = $false
}

if ($ExplicitComponent) {
    $BuildLauncher = $LauncherOnly
    $BuildHooks = $HooksOnly
    $BuildUpdater = $UpdaterOnly
    $BuildRouter = $RouterOnly
    $PrepareRuntime = $RuntimeOnly
}
elseif ($Auto) {
    foreach ($Path in @(Get-AutoChangedFiles)) {
        switch -Regex ($Path) {
            '^client/ScblPublicLauncher/' { $BuildLauncher = $true; continue }
            '^client/hooks/' { $BuildHooks = $true; continue }
            '^client/SCBL\.Updater/' { $BuildUpdater = $true; continue }
            '^client/scbl-process-router/' { $BuildRouter = $true; continue }
            '^client/easytier/' { $PrepareRuntime = $true; continue }
            '^client/SCBL\.Version\.props$' { $BuildLauncher = $true; $BuildUpdater = $true; $BuildRouter = $true; continue }
            '^shared/api/' { $BuildLauncher = $true; $BuildHooks = $true; continue }
            '^Cargo\.(toml|lock)$' { $BuildHooks = $true; continue }
            '^rust-toolchain\.toml$' { $BuildHooks = $true; continue }
            '^\.cargo/' { $BuildHooks = $true; continue }
            '^client/build_launcher_incremental\.ps1$' { $BuildLauncher = $true; continue }
            '^client/prepare_bootstrap_hooks\.ps1$' { continue }
            '^client/create_client_full_package\.ps1$' { continue }
            '^client/build_all_windows\.ps1$' { continue }
            '^client/WINDIVERT_NOTICE\.txt$' { $BuildRouter = $true; continue }
            '^THIRD_PARTY_LICENSES/' { $PrepareRuntime = $true; continue }
            '^VERSION_CLIENT$' { $BuildLauncher = $true; $BuildUpdater = $true; continue }
        }
    }
    if (!$BuildLauncher -and !$BuildHooks -and !$BuildUpdater -and !$BuildRouter -and !$PrepareRuntime) {
        Write-Host "Auto mode found no Windows component changes; no compilation is required."
    }
}

if ($Package) {
    $BuildLauncher = $true
    $BuildHooks = $true
    $BuildUpdater = $true
    $BuildRouter = $true
    $PrepareRuntime = $true
}

Write-Host ("Build plan: launcher={0}, hooks={1}, updater={2}, router={3}, runtime={4}, package={5}" -f $BuildLauncher, $BuildHooks, $BuildUpdater, $BuildRouter, $PrepareRuntime, $Package)

if ($BuildLauncher -or $BuildHooks -or $BuildUpdater -or $BuildRouter -or $PrepareRuntime) {
    Invoke-Step "stop-runtime" (Join-Path $Root "stop_runtime_processes.ps1")
}
if ($PrepareRuntime) {
    Invoke-Step "easytier" (Join-Path $Root "easytier\download_easytier_windows.ps1")
}
if ($BuildRouter) {
    Invoke-Step "route-guard" (Join-Path $Root "scbl-process-router\build_windows.ps1")
}
if ($BuildLauncher) {
    $LauncherArgs = @{ SkipRuntimeStop = $true }
    if ($Fast) { $LauncherArgs.Fast = $true }
    if ($Clean) { $LauncherArgs.Clean = $true }
    Invoke-Step "launcher" (Join-Path $Root "build_launcher_incremental.ps1") $LauncherArgs
}
if ($BuildUpdater) {
    Invoke-Step "updater" (Join-Path $Root "SCBL.Updater\build_windows.ps1")
}
if ($BuildHooks) {
    $HooksScript = Join-Path (Split-Path $Root -Parent) "scripts\build-rust-components.ps1"
    $HooksElapsed = Measure-Command {
        & $HooksScript -Release -HooksOnly
        if (-not $?) { throw "Step failed: $HooksScript" }
    }
    $Timings["hooks"] = [math]::Round($HooksElapsed.TotalSeconds, 1)
}

$AssemblyElapsed = Measure-Command { Copy-AvailableOutputs }
$Timings["assemble"] = [math]::Round($AssemblyElapsed.TotalSeconds, 1)

$Required = New-Object System.Collections.Generic.List[string]
if ($BuildLauncher -or $Package) { $Required.Add((Join-Path $Publish "SCBL.exe")) }
if ($BuildHooks -or $Package) {
    $Required.Add((Join-Path $HooksComponent "uplay_r1_loader.dll"))
    $Required.Add((Join-Path $HooksComponent "uplay_r1_loader.dll.sha256"))
    $Required.Add((Join-Path $HooksModComponent "scbl_gameplay.dll"))
    $Required.Add((Join-Path $HooksModComponent "scbl_gameplay.dll.sha256"))
}
if ($BuildUpdater -or $Package) {
    $Required.Add((Join-Path $Publish "SCBL.Updater.exe"))
}
if ($BuildRouter -or $Package) {
    $Required.Add((Join-Path $RouteGuardComponent "scbl-process-router.exe"))
    $Required.Add((Join-Path $RouteGuardComponent "WinDivert.dll"))
    $Required.Add((Join-Path $RouteGuardComponent "WinDivert64.payload.sys"))
}
if ($PrepareRuntime -or $Package) {
    $Required.Add((Join-Path $NetworkComponent "easytier-core.exe"))
    $Required.Add((Join-Path $NetworkComponent "easytier-cli.exe"))
    $Required.Add((Join-Path $NetworkComponent "Packet.dll"))
    $Required.Add((Join-Path $NetworkComponent "wintun.dll"))
}
foreach ($File in $Required) { if (!(Test-Path -LiteralPath $File)) { throw "Missing output: $File" } }

if ($Package) {
    $BootstrapArgs = @{ PublishRoot = $Publish }
    if (![string]::IsNullOrWhiteSpace($HooksDll)) { $BootstrapArgs.SourceDll = $HooksDll }
    Invoke-Step "bootstrap-hooks" (Join-Path $Root "prepare_bootstrap_hooks.ps1") $BootstrapArgs

    $PackageArgs = @{ Version = $ScblVersion; OutputDir = $OutputDir }
    if ($Fast) { $PackageArgs.Fast = $true }
    Invoke-Step "package" (Join-Path $Root "create_client_full_package.ps1") $PackageArgs
}

Write-Host "Build finished: $Publish"
Write-Host "Step timings (seconds):"
$Timings.GetEnumerator() | ForEach-Object { Write-Host ("  {0,-18} {1,8}" -f $_.Key, $_.Value) }

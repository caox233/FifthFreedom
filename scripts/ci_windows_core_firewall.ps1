# Disposable hosted-runner containment for one exact staging application path.
# This is an AppID/path rule, not PID isolation or a product deployment policy.
param(
    [Parameter(Mandatory=$true)][ValidateSet('Prepare','Arm','Restore')][string]$Mode,
    [Parameter(Mandatory=$true)][string]$Manifest,
    [Parameter(Mandatory=$true)][string]$State,
    [Parameter(Mandatory=$true)][string]$Report,
    [string]$DrainReport = ''
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
function Require($Condition, [string]$Code) { if (!$Condition) { throw $Code } }
function Integer($Value) { return $Value -is [int] -or $Value -is [long] }
function Write-NewJson($Path, $Value) {
    $bytes = [Text.Encoding]::UTF8.GetBytes(($Value | ConvertTo-Json -Depth 8 -Compress))
    $file = [IO.File]::Open($Path, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
    try { $file.Write($bytes,0,$bytes.Length); $file.Flush($true) } finally { $file.Dispose() }
}
function Hash-Text($Value) {
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData(
        [Text.Encoding]::UTF8.GetBytes(($Value | ConvertTo-Json -Depth 10 -Compress)))).ToLowerInvariant()
}
function Read-Json($Path) {
    $item = Get-Item -LiteralPath $Path
    Require (!$item.PSIsContainer -and !($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -and
        $item.Length -gt 0 -and $item.Length -le 65536) 'FIREWALL_INPUT_INVALID'
    return Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
}
function Check-Fields($Value, $Names) {
    $actual = ($Value.PSObject.Properties.Name | Sort-Object) -join ','
    $expected = ($Names | Sort-Object) -join ','
    Require ($actual -ceq $expected) 'FIREWALL_INPUT_INVALID'
}
function Read-Profiles {
    $profiles = @(Get-NetFirewallProfile -PolicyStore ActiveStore | Sort-Object Name)
    Require ($profiles.Count -eq 3 -and ($profiles.Name -join ',') -ceq 'Domain,Private,Public') 'FIREWALL_POLICY_UNAVAILABLE'
    foreach ($profile in $profiles) {
        Require ([string]$profile.Enabled -ceq 'True' -and [string]$profile.AllowLocalFirewallRules -cne 'False') 'FIREWALL_POLICY_UNAVAILABLE'
    }
    return @($profiles | Select-Object Name,Enabled,AllowLocalFirewallRules,DefaultInboundAction,DefaultOutboundAction)
}
function Read-Snapshot {
    # Sorted fixed property projections only. The raw local policy is never exported.
    return [ordered]@{
        profiles = @(Read-Profiles)
        rules = @(Get-NetFirewallRule -PolicyStore PersistentStore | Sort-Object Name |
            Select-Object Name,Enabled,Profile,Direction,Action,EdgeTraversalPolicy,LooseSourceMapping,LocalOnlyMapping)
        apps = @(Get-NetFirewallApplicationFilter -PolicyStore PersistentStore | Sort-Object InstanceID | Select-Object InstanceID,Program,Package)
        ports = @(Get-NetFirewallPortFilter -PolicyStore PersistentStore | Sort-Object InstanceID | Select-Object InstanceID,Protocol,LocalPort,RemotePort,IcmpType,DynamicTarget)
        addresses = @(Get-NetFirewallAddressFilter -PolicyStore PersistentStore | Sort-Object InstanceID | Select-Object InstanceID,LocalAddress,RemoteAddress)
        services = @(Get-NetFirewallServiceFilter -PolicyStore PersistentStore | Sort-Object InstanceID | Select-Object InstanceID,Service)
        interfaces = @(Get-NetFirewallInterfaceFilter -PolicyStore PersistentStore | Sort-Object InstanceID | Select-Object InstanceID,InterfaceAlias)
        interface_types = @(Get-NetFirewallInterfaceTypeFilter -PolicyStore PersistentStore | Sort-Object InstanceID | Select-Object InstanceID,InterfaceType)
    }
}
function Check-Owned($Condition, [string]$Field, [string]$Code = 'FIREWALL_RULE_CHANGED') {
    # A fixed field label identifies a failed readback without publishing policy
    # values, executable paths, rule names or raw PowerShell exceptions.
    $result.rule_check = $Field
    Require $Condition $Code
}
function Read-Owned($Plan, [string]$Direction, [string]$Store, [bool]$Required) {
    $result.rule_store = $Store; $result.rule_direction = $Direction
    $result.application_path_case_insensitive_match = $null
    $result.application_package_kind = $null
    $name = 'ff-core-' + $Plan.probe_id + '-' + $Direction.ToLowerInvariant()
    # Enumerating first makes an absent owned name distinguishable from a failed query.
    $result.rule_check = 'ENUMERATE'
    $rules = @(Get-NetFirewallRule -PolicyStore $Store | Where-Object { $_.Name -ceq $name })
    if (!$Required -and $rules.Count -eq 0) { $result.rule_check = 'ABSENT'; return $null }
    Check-Owned ($rules.Count -eq 1) 'RULE_COUNT'
    $rule = $rules[0]
    Check-Owned ($rule.Group -ceq ('ff-core-' + $Plan.probe_id)) 'GROUP'
    Check-Owned ($rule.DisplayName -ceq $name) 'DISPLAY_NAME'
    Check-Owned ([string]$rule.Enabled -ceq 'True') 'ENABLED'
    Check-Owned ([string]$rule.Profile -ceq 'Any') 'PROFILE'
    Check-Owned ([string]$rule.Direction -ceq $Direction) 'DIRECTION'
    Check-Owned ([string]$rule.Action -ceq 'Block') 'ACTION'
    Check-Owned ([string]$rule.EdgeTraversalPolicy -ceq 'Block') 'EDGE_TRAVERSAL'
    Check-Owned ([string]$rule.LooseSourceMapping -ceq 'False') 'LOOSE_SOURCE_MAPPING'
    Check-Owned ([string]$rule.LocalOnlyMapping -ceq 'False') 'LOCAL_ONLY_MAPPING'
    if ($Store -ceq 'ActiveStore') {
        Check-Owned ([string]$rule.PrimaryStatus -ceq 'OK') 'PRIMARY_STATUS' 'FIREWALL_POLICY_UNAVAILABLE'
        Check-Owned ([string]$rule.EnforcementStatus -ceq 'Full') 'ENFORCEMENT_STATUS' 'FIREWALL_POLICY_UNAVAILABLE'
    }
    $result.rule_check = 'FILTER_QUERY'
    $app = @($rule | Get-NetFirewallApplicationFilter)
    $port = @($rule | Get-NetFirewallPortFilter)
    $address = @($rule | Get-NetFirewallAddressFilter)
    $service = @($rule | Get-NetFirewallServiceFilter)
    $interface = @($rule | Get-NetFirewallInterfaceFilter)
    $type = @($rule | Get-NetFirewallInterfaceTypeFilter)
    Check-Owned ($app.Count -eq 1) 'APPLICATION_COUNT'
    $result.application_path_case_insensitive_match = [string]::Equals(
        [string]$app[0].Program, [string]$Plan.core_path, [StringComparison]::OrdinalIgnoreCase)
    # Deliberately retain the failing strict predicate until actual Windows
    # readback identifies the field. The diagnostic does not relax admission.
    Check-Owned ($app[0].Program -ceq $Plan.core_path) 'APPLICATION_PATH'
    $packageProperty = $app[0].PSObject.Properties['Package']
    $packageValue = $null
    if ($null -ne $packageProperty) { $packageValue = $packageProperty.Value }
    $result.application_package_kind = if ($null -eq $packageProperty) { 'MISSING' }
        elseif ($null -eq $packageValue) { 'NULL' }
        elseif ($packageValue -isnot [string]) { 'OTHER_TYPE' }
        elseif ($packageValue.Length -eq 0) { 'EMPTY' }
        elseif ($packageValue -ceq 'Any') { 'ANY' }
        elseif ($packageValue -ieq 'Any') { 'ANY_CASE_VARIANT' }
        elseif ($packageValue -cmatch '^S-[0-9]+(-[0-9]+)+$') { 'SID' }
        else { 'OTHER_STRING' }
    Check-Owned ($app[0].Package -ceq 'Any') 'APPLICATION_PACKAGE'
    Check-Owned ($port.Count -eq 1) 'PORT_COUNT'
    Check-Owned ([string]$port[0].Protocol -ceq 'Any') 'PROTOCOL'
    Check-Owned ([string]$port[0].LocalPort -ceq 'Any') 'LOCAL_PORT'
    Check-Owned ([string]$port[0].RemotePort -ceq 'Any') 'REMOTE_PORT'
    Check-Owned ($address.Count -eq 1) 'ADDRESS_COUNT'
    Check-Owned ([string]$address[0].LocalAddress -ceq 'Any') 'LOCAL_ADDRESS'
    Check-Owned ([string]$address[0].RemoteAddress -ceq 'Any') 'REMOTE_ADDRESS'
    Check-Owned ($service.Count -eq 1) 'SERVICE_COUNT'
    Check-Owned ($service[0].Service -ceq 'Any') 'SERVICE'
    Check-Owned ($interface.Count -eq 1) 'INTERFACE_COUNT'
    Check-Owned ([string]$interface[0].InterfaceAlias -ceq 'Any') 'INTERFACE_ALIAS'
    Check-Owned ($type.Count -eq 1) 'INTERFACE_TYPE_COUNT'
    Check-Owned ([string]$type[0].InterfaceType -ceq 'Any') 'INTERFACE_TYPE'
    $result.rule_check = 'VERIFIED'
    return $rule
}
$result = [ordered]@{ schema=1; mode=$Mode; succeeded=$false; active_rules_verified=$false;
    armed=$false; owned_rules_removed=$false; original_policy_unchanged=$false; exact_drain_verified=$false;
    application_path_rules=$true; pid_isolation=$false; packet_behavior_verified=$false; policy_enabled_or_changed=$false;
    rule_check=$null; rule_store=$null; rule_direction=$null; application_path_case_insensitive_match=$null; application_package_kind=$null }
try {
    Require ($env:GITHUB_ACTIONS -ceq 'true' -and $env:RUNNER_ENVIRONMENT -ceq 'github-hosted' -and
        $env:ImageOS -cin @('win25','win25-vs2026')) 'FIREWALL_HOST_REQUIRED'
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    Require ($principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) 'FIREWALL_HOST_REQUIRED'
    Require ((Get-Service -Name MpsSvc).Status -eq 'Running') 'FIREWALL_POLICY_UNAVAILABLE'
    $inputManifest = Read-Json $Manifest
    Check-Fields $inputManifest @('schema','probe_id','core_path','core_bytes','core_sha256','source_tree_sha256','build_recipe_sha256','inert_profile_sha256')
    Require ((Integer $inputManifest.schema) -and $inputManifest.schema -eq 1 -and
        (Integer $inputManifest.core_bytes) -and $inputManifest.core_bytes -gt 0 -and $inputManifest.core_bytes -le 268435456 -and
        $inputManifest.source_tree_sha256 -cmatch '^[0-9a-f]{64}$' -and
        $inputManifest.build_recipe_sha256 -cmatch '^[0-9a-f]{64}$' -and
        $inputManifest.inert_profile_sha256 -cmatch '^[0-9a-f]{64}$' -and $inputManifest.probe_id -cmatch '^[0-9a-f]{32}$' -and
        $inputManifest.core_sha256 -cmatch '^[0-9a-f]{64}$') 'FIREWALL_INPUT_INVALID'
    $corePath = [IO.Path]::GetFullPath($inputManifest.core_path)
    $temp = [IO.Path]::GetFullPath($env:RUNNER_TEMP).TrimEnd('\')
    Require ($corePath -ceq ($temp + '\ff-core-runtime-' + $inputManifest.probe_id + '\easytier-core.exe')) 'FIREWALL_INPUT_INVALID'
    $manifestHash = (Get-FileHash -LiteralPath $Manifest -Algorithm SHA256).Hash.ToLowerInvariant()
    $result.probe_id = $inputManifest.probe_id
    if ($Mode -eq 'Prepare') {
        Require (!(Test-Path -LiteralPath $State) -and !(Test-Path -LiteralPath ($State + '.armed'))) 'FIREWALL_STATE_EXISTS'
        $core = Get-Item -LiteralPath $corePath
        Require (!$core.PSIsContainer -and !($core.Attributes -band [IO.FileAttributes]::ReparsePoint) -and
            $core.Length -eq $inputManifest.core_bytes -and
            (Get-FileHash -LiteralPath $corePath -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $inputManifest.core_sha256) 'FIREWALL_IMAGE_CHANGED'
        $plan = [ordered]@{ schema=1; probe_id=$inputManifest.probe_id; core_path=$corePath;
            core_sha256=$inputManifest.core_sha256; manifest_sha256=$manifestHash; original_policy_sha256=(Hash-Text (Read-Snapshot)) }
        foreach ($direction in @('Inbound','Outbound')) {
            Require ($null -eq (Read-Owned $plan $direction PersistentStore $false)) 'FIREWALL_STATE_EXISTS'
        }
        # Durable plan precedes either mutation, including the partial-Prepare failure path.
        Write-NewJson $State $plan
        foreach ($direction in @('Inbound','Outbound')) {
            $name = 'ff-core-' + $plan.probe_id + '-' + $direction.ToLowerInvariant()
            New-NetFirewallRule -PolicyStore PersistentStore -Name $name -DisplayName $name -Group ('ff-core-' + $plan.probe_id) `
                -Program $corePath -Direction $direction -Action Block -Enabled True -Profile Any -Protocol Any `
                -LocalAddress Any -RemoteAddress Any -LocalPort Any -RemotePort Any -EdgeTraversalPolicy Block | Out-Null
            $null = Read-Owned $plan $direction PersistentStore $true
            $null = Read-Owned $plan $direction ActiveStore $true
        }
        $result.active_rules_verified = $true
    } else {
        $plan = Read-Json $State
        Check-Fields $plan @('schema','probe_id','core_path','core_sha256','manifest_sha256','original_policy_sha256')
        Require ((Integer $plan.schema) -and $plan.schema -eq 1 -and $plan.probe_id -ceq $inputManifest.probe_id -and $plan.core_path -ceq $corePath -and
            $plan.core_sha256 -ceq $inputManifest.core_sha256 -and $plan.manifest_sha256 -ceq $manifestHash -and
            $plan.original_policy_sha256 -cmatch '^[0-9a-f]{64}$') 'FIREWALL_INPUT_INVALID'
        if ($Mode -eq 'Arm') {
            $null = Read-Profiles
            foreach ($direction in @('Inbound','Outbound')) { $null = Read-Owned $plan $direction ActiveStore $true }
            # Exclusive marker must be durable before the caller starts the managed harness.
            Write-NewJson ($State + '.armed') ([ordered]@{ schema=1; manifest_sha256=$manifestHash })
            $result.armed = $true; $result.active_rules_verified = $true
        } else {
            if (Test-Path -LiteralPath ($State + '.armed')) {
                $armed = Read-Json ($State + '.armed')
                Check-Fields $armed @('schema','manifest_sha256')
                Require ((Integer $armed.schema) -and $armed.schema -eq 1 -and $armed.manifest_sha256 -ceq $manifestHash) 'FIREWALL_INPUT_INVALID'
                Require ($DrainReport -ne '' -and (Test-Path -LiteralPath $DrainReport)) 'FIREWALL_DRAIN_UNCONFIRMED'
                $drain = Read-Json $DrainReport
                Require ($drain.schema -ceq 'ff-owned-core-runtime-v1' -and $drain.probe_id -ceq $plan.probe_id -and
                    $drain.core_sha256 -ceq $plan.core_sha256 -and $drain.source_tree_sha256 -ceq $inputManifest.source_tree_sha256 -and
                    $drain.build_recipe_sha256 -ceq $inputManifest.build_recipe_sha256 -and
                    $drain.inert_profile_sha256 -ceq $inputManifest.inert_profile_sha256 -and
                    $drain.instance -cmatch '^[0-9a-f]{8}(-[0-9a-f]{4}){3}-[0-9a-f]{12}$' -and
                    $drain.exact_owned_job_drained -is [bool] -and $drain.exact_owned_job_drained) 'FIREWALL_DRAIN_UNCONFIRMED'
                $result.armed = $true; $result.exact_drain_verified = $true
            }
            # Validate both rules before deleting either; never alter an unfamiliar changed rule.
            $owned = @()
            foreach ($direction in @('Inbound','Outbound')) {
                $rule = Read-Owned $plan $direction PersistentStore $false
                if ($null -ne $rule) { $owned += $rule }
            }
            foreach ($rule in $owned) { $rule | Remove-NetFirewallRule }
            foreach ($direction in @('Inbound','Outbound')) {
                Require ($null -eq (Read-Owned $plan $direction PersistentStore $false) -and
                    $null -eq (Read-Owned $plan $direction ActiveStore $false)) 'FIREWALL_RESTORE_FAILED'
            }
            $result.owned_rules_removed = $true
            Require ((Hash-Text (Read-Snapshot)) -ceq $plan.original_policy_sha256) 'FIREWALL_BASELINE_CHANGED'
            $result.original_policy_unchanged = $true
        }
    }
    $result.succeeded = $true
} catch {
    $codes = @('FIREWALL_INPUT_INVALID','FIREWALL_POLICY_UNAVAILABLE','FIREWALL_RULE_CHANGED','FIREWALL_HOST_REQUIRED',
        'FIREWALL_STATE_EXISTS','FIREWALL_IMAGE_CHANGED','FIREWALL_DRAIN_UNCONFIRMED','FIREWALL_RESTORE_FAILED','FIREWALL_BASELINE_CHANGED')
    $result.error_code = if ($_.Exception.Message -cin $codes) { $_.Exception.Message } else { 'FIREWALL_OPERATION_FAILED' }
}
try { Write-NewJson $Report $result } catch { Write-Output '::error::FIREWALL_RECEIPT_FAILED'; exit 1 }
if (!$result.succeeded) { Write-Output ('::error::' + $result.error_code); exit 1 }
Write-Output ('FIREWALL_' + $Mode.ToUpperInvariant() + '_VERIFIED')

# Pure, bounded diagnostics only. These functions do not decide rule admission.
# MSFT_NetFirewallRule documents EnforcementStatus as uint16[]; Full is 1.
# https://learn.microsoft.com/en-us/windows/win32/fwp/wmi/wfascimprov/msft-netfirewallrule
function Get-FirewallEnforcementObservation($Rule) {
    $observation = [ordered]@{ shape='MISSING'; count=0; item_type='NONE'; status='MISSING'; items=@() }
    if ($null -eq $Rule) { return $observation }
    $property = $Rule.PSObject.Properties['EnforcementStatus']
    if ($null -eq $property) { return $observation }
    # Direct assignment preserves empty/singleton arrays and the actual type.
    $value = $property.Value
    if ($null -eq $value) {
        $observation.shape='NULL'; $observation.status='NULL'; return $observation
    }
    $observation.shape='SCALAR'; $observation.count=1
    if ($value -is [array]) {
        $observation.shape='ARRAY'; $observation.count=[Math]::Min($value.Length,65)
        if ($value.Length -eq 0) { $observation.status='EMPTY'; return $observation }
        # At most eight fixed enum/type labels; no unknown provider value escapes.
        for ($index=0; $index -lt [Math]::Min($value.Length,8); $index++) {
            $observation.items += @(Get-FirewallEnforcementItem $value[$index])
        }
        if ($value.Length -gt 1) { $observation.status='MULTIPLE'; return $observation }
    } else {
        $observation.items = @(Get-FirewallEnforcementItem $value)
    }
    $observation.item_type=$observation.items[0].item_type
    $observation.status=$observation.items[0].status
    return $observation
}
function Get-FirewallEnforcementItem($Value) {
    $item = [ordered]@{ item_type='OTHER'; status='OTHER_TYPE' }
    $names = @('Invalid','Full','FirewallOffInProfile','CategoryOff','DisabledObject','InactiveProfile',
        'LocalAddressResolutionEmpty','RemoteAddressResolutionEmpty','LocalPortResolutionEmpty',
        'RemotePortResolutionEmpty','InterfaceResolutionEmpty','ApplicationResolutionEmpty',
        'RemoteMachineEmpty','RemoteUserEmpty','LocalGlobalOpenPortsDisallowed',
        'LocalAuthorizedApplicationsDisallowed','LocalFirewallRulesDisallowed','LocalConsecRulesDisallowed',
        'NotTargetPlatform','OptimizedOut','LocalUserEmpty','TransportMachinesEmpty',
        'TunnelMachinesEmpty','TupleResolutionEmpty')
    if ($null -eq $Value) { $item.item_type='NULL'; $item.status='NULL_ITEM' }
    elseif ($Value -is [string] -or $Value -is [Enum]) {
        $item.item_type=if ($Value -is [string]) { 'STRING' } else { 'ENUM' }
        $label = [string]$Value
        $item.status=if ($label -cin $names -or $label -ceq 'NotApplicable') { $label } else { 'OTHER_STRING' }
    } elseif ($Value -is [uint16] -or $Value -is [uint32] -or $Value -is [int]) {
        $item.item_type=if ($Value -is [uint16]) { 'UINT16' } elseif ($Value -is [uint32]) { 'UINT32' } else { 'INT32' }
        $item.status=if ($Value -ge 0 -and $Value -lt $names.Length) { $names[$Value] } else { 'OTHER_NUMBER' }
    }
    return $item
}

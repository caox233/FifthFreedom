# Pure, bounded diagnostics only. These functions do not decide rule admission.
# MSFT_NetFirewallRule documents EnforcementStatus as uint16[]; Full is 1.
# https://learn.microsoft.com/en-us/windows/win32/fwp/wmi/wfascimprov/msft-netfirewallrule
function Get-FirewallEnforcementObservation($Rule) {
    $observation = [ordered]@{ shape='MISSING'; count=0; item_type='NONE'; status='MISSING' }
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
        if ($value.Length -gt 1) { $observation.status='MULTIPLE'; return $observation }
        $value = $value[0]
    }
    $names = @('Invalid','Full','FirewallOffInProfile','CategoryOff','DisabledObject','InactiveProfile',
        'LocalAddressResolutionEmpty','RemoteAddressResolutionEmpty','LocalPortResolutionEmpty',
        'RemotePortResolutionEmpty','InterfaceResolutionEmpty','ApplicationResolutionEmpty',
        'RemoteMachineEmpty','RemoteUserEmpty','LocalGlobalOpenPortsDisallowed',
        'LocalAuthorizedApplicationsDisallowed','LocalFirewallRulesDisallowed','LocalConsecRulesDisallowed',
        'NotTargetPlatform','OptimizedOut','LocalUserEmpty','TransportMachinesEmpty',
        'TunnelMachinesEmpty','TupleResolutionEmpty')
    $observation.status='OTHER_TYPE'
    if ($null -eq $value) { $observation.item_type='NULL'; $observation.status='NULL_ITEM' }
    elseif ($value -is [string] -or $value -is [Enum]) {
        $observation.item_type=if ($value -is [string]) { 'STRING' } else { 'ENUM' }
        $label = [string]$value
        $observation.status=if ($label -cin $names -or $label -ceq 'NotApplicable') { $label } else { 'OTHER_STRING' }
    } elseif ($value -is [uint16] -or $value -is [uint32] -or $value -is [int]) {
        $observation.item_type=if ($value -is [uint16]) { 'UINT16' } elseif ($value -is [uint32]) { 'UINT32' } else { 'INT32' }
        $observation.status=if ($value -ge 0 -and $value -lt $names.Length) { $names[$value] } else { 'OTHER_NUMBER' }
    } else { $observation.item_type='OTHER' }
    return $observation
}

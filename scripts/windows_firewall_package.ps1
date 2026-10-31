# Pure inspection of the actual application-filter property. No firewall cmdlet,
# process, file or network operation is performed by these functions.
function Get-FirewallPackageKind($Filter) {
    if ($null -eq $Filter) { return 'MISSING' }
    $property = $Filter.PSObject.Properties['Package']
    if ($null -eq $property) { return 'MISSING' }
    $value = $property.Value
    if ($null -eq $value) { return 'NULL' }
    if ($value -isnot [string]) { return 'OTHER_TYPE' }
    if ($value.Length -eq 0) { return 'EMPTY' }
    if ($value -ceq 'Any') { return 'ANY' }
    if ($value -ieq 'Any') { return 'ANY_CASE_VARIANT' }
    if ($value -cmatch '^S-[0-9]+(-[0-9]+)+$') { return 'SID' }
    return 'OTHER_STRING'
}

function Test-UnrestrictedFirewallPackage($Filter) {
    # New-NetFirewallRule omits -Package for this classic application rule.
    # Actual Windows2025 readback returns a present NULL property (default None).
    # Retain the former literal Any representation; unknown/missing properties,
    # SIDs and all other types/strings remain rejected.
    return (Get-FirewallPackageKind $Filter) -cin @('NULL', 'ANY')
}

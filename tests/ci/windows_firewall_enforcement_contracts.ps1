# Actual production diagnostic classifier; no firewall API or admission proof.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/windows_firewall_enforcement.ps1')
enum EnforcementFixture { Invalid=0; Full=1; InactiveProfile=5 }
$cases = @(
    @{Name='missing property'; Rule=[pscustomobject]@{}; Shape='MISSING'; Count=0; Type='NONE'; Status='MISSING'},
    @{Name='present null'; Rule=[pscustomobject]@{EnforcementStatus=$null}; Shape='NULL'; Count=0; Type='NONE'; Status='NULL'},
    @{Name='scalar Full string'; Rule=[pscustomobject]@{EnforcementStatus='Full'}; Shape='SCALAR'; Count=1; Type='STRING'; Status='Full'},
    @{Name='single uint16 Full'; Rule=[pscustomobject]@{EnforcementStatus=[uint16[]]@(1)}; Shape='ARRAY'; Count=1; Type='UINT16'; Status='Full'},
    @{Name='empty array preserved'; Rule=[pscustomobject]@{EnforcementStatus=@()}; Shape='ARRAY'; Count=0; Type='NONE'; Status='EMPTY'},
    @{Name='multiple statuses preserved'; Rule=[pscustomobject]@{EnforcementStatus=[uint16[]]@(1,5)}; Shape='ARRAY'; Count=2; Type='NONE'; Status='MULTIPLE'},
    @{Name='null array item'; Rule=[pscustomobject]@{EnforcementStatus=@($null)}; Shape='ARRAY'; Count=1; Type='NULL'; Status='NULL_ITEM'},
    @{Name='single Full string array'; Rule=[pscustomobject]@{EnforcementStatus=@('Full')}; Shape='ARRAY'; Count=1; Type='STRING'; Status='Full'},
    @{Name='scalar Full enum'; Rule=[pscustomobject]@{EnforcementStatus=[EnforcementFixture]::Full}; Shape='SCALAR'; Count=1; Type='ENUM'; Status='Full'},
    @{Name='unknown string withheld'; Rule=[pscustomobject]@{EnforcementStatus='private fixture'}; Shape='SCALAR'; Count=1; Type='STRING'; Status='OTHER_STRING'},
    @{Name='boolean is not numeric Full'; Rule=[pscustomobject]@{EnforcementStatus=$true}; Shape='SCALAR'; Count=1; Type='OTHER'; Status='OTHER_TYPE'},
    @{Name='large count bounded'; Rule=[pscustomobject]@{EnforcementStatus=@(1)*100}; Shape='ARRAY'; Count=65; Type='NONE'; Status='MULTIPLE'},
    @{Name='inactive remains inactive'; Rule=[pscustomobject]@{EnforcementStatus=[uint16[]]@(5)}; Shape='ARRAY'; Count=1; Type='UINT16'; Status='InactiveProfile'},
    @{Name='not applicable remains distinct'; Rule=[pscustomobject]@{EnforcementStatus='NotApplicable'}; Shape='SCALAR'; Count=1; Type='STRING'; Status='NotApplicable'},
    @{Name='zero remains invalid'; Rule=[pscustomobject]@{EnforcementStatus=[uint16[]]@(0)}; Shape='ARRAY'; Count=1; Type='UINT16'; Status='Invalid'},
    @{Name='case variant is unknown'; Rule=[pscustomobject]@{EnforcementStatus='full'}; Shape='SCALAR'; Count=1; Type='STRING'; Status='OTHER_STRING'}
)
$report = [ordered]@{schema=1; kind='pure-firewall-enforcement-diagnostics'; passed=$false;
    required_checks=22; passed_checks=0; skipped_checks=0; failed_case=$null;
    firewall_api_executed=$false; admission_exercised=$false; core_executed=$false; driver_loaded=$false}
try {
    foreach ($case in $cases) {
        $report.failed_case=$case.Name
        $actual=Get-FirewallEnforcementObservation $case.Rule
        if ($actual.shape -cne $case.Shape -or $actual.count -ne $case.Count -or
            $actual.item_type -cne $case.Type -or $actual.status -cne $case.Status -or
            $actual.items.Count -ne [Math]::Min($case.Count,8)) {
            throw 'FIREWALL_ENFORCEMENT_DIAGNOSTIC_FAILED'
        }
        $report.passed_checks++
        Write-Output ('PASS ' + $case.Name)
    }
    $report.failed_case='mixed numeric statuses retain fixed member labels'
    $mixed=Get-FirewallEnforcementObservation ([pscustomobject]@{EnforcementStatus=[uint16[]]@(1,5)})
    if ($mixed.items[0].item_type -cne 'UINT16' -or $mixed.items[0].status -cne 'Full' -or
        $mixed.items[1].item_type -cne 'UINT16' -or $mixed.items[1].status -cne 'InactiveProfile') {
        throw 'FIREWALL_ENFORCEMENT_DIAGNOSTIC_FAILED'
    }
    $report.passed_checks++; Write-Output ('PASS ' + $report.failed_case)
    # Constructed CIM objects only: no provider query, firewall API or admission.
    foreach ($case in @(
        @{Name='CIM absent property remains missing'; Values=$null; Query='PROPERTY_MISSING'; Status=$null},
        @{Name='CIM single Full numeric remains typed'; Values=[uint16[]]@(1); Query='FOUND'; Status='Full'},
        @{Name='CIM multiple Full values remain multiple'; Values=[uint16[]]@(1,1); Query='FOUND'; Status='MULTIPLE'},
        @{Name='CIM mixed status remains distinct'; Values=[uint16[]]@(1,5); Query='FOUND'; Status='MULTIPLE'}
    )) {
        $report.failed_case=$case.Name
        $instance=[Microsoft.Management.Infrastructure.CimInstance]::new('FirewallDiagnosticFixture')
        try {
            if ($null -ne $case.Values) {
                $property=[Microsoft.Management.Infrastructure.CimProperty]::Create('EnforcementStatus',$case.Values,
                    [Microsoft.Management.Infrastructure.CimType]::UInt16Array,[Microsoft.Management.Infrastructure.CimFlags]::None)
                $instance.CimInstanceProperties.Add($property)
            }
            $actual=Get-FirewallEnforcementCimObservation $instance
            if ($actual.query -cne $case.Query) { throw 'FIREWALL_CIM_DIAGNOSTIC_FAILED' }
            if ($case.Query -ceq 'FOUND') {
                if ($actual.cim_type -cne 'UInt16Array' -or $actual.value.status -cne $case.Status -or
                    $actual.value.count -ne $case.Values.Length -or $actual.value.items[0].item_type -cne 'UINT16' -or
                    $actual.value.items[0].status -cne 'Full') { throw 'FIREWALL_CIM_DIAGNOSTIC_FAILED' }
                if ($case.Values[-1] -eq 5 -and $actual.value.items[-1].status -cne 'InactiveProfile') {
                    throw 'FIREWALL_CIM_DIAGNOSTIC_FAILED'
                }
            } elseif ($null -ne $actual.value -or $actual.cim_type -cne 'NONE') { throw 'FIREWALL_CIM_DIAGNOSTIC_FAILED' }
            $report.passed_checks++; Write-Output ('PASS ' + $report.failed_case)
        } finally { $instance.Dispose() }
    }
    $report.failed_case='unknown mixed member cannot expose raw provider string'
    $mixed=Get-FirewallEnforcementObservation ([pscustomobject]@{EnforcementStatus=@('Full','private fixture')})
    if ($mixed.items[0].item_type -cne 'STRING' -or $mixed.items[0].status -cne 'Full' -or
        $mixed.items[1].item_type -cne 'STRING' -or $mixed.items[1].status -cne 'OTHER_STRING' -or
        ($mixed | ConvertTo-Json -Depth 5 -Compress) -cmatch 'private') {
        throw 'FIREWALL_ENFORCEMENT_DIAGNOSTIC_FAILED'
    }
    $report.passed_checks++; Write-Output ('PASS ' + $report.failed_case)
    $report.failed_case=$null; $report.passed=$report.passed_checks -eq $report.required_checks
} catch { Write-Output 'FIREWALL_ENFORCEMENT_DIAGNOSTIC_FAILED' }
$output=Join-Path $PSScriptRoot '../../artifacts/ci/windows-firewall-enforcement-contracts.json'
$null=[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
$bytes=[Text.Encoding]::UTF8.GetBytes(($report | ConvertTo-Json -Depth 4 -Compress))
$file=[IO.File]::Open($output,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
try { $file.Write($bytes,0,$bytes.Length); $file.Flush($true) } finally { $file.Dispose() }
if (!$report.passed) { exit 1 }
Write-Output 'RESULT 22 passed; 0 skipped'
exit 0

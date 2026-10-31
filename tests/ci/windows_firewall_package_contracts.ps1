# Execute the production pure predicate, without invoking a firewall cmdlet.
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot '../../scripts/windows_firewall_package.ps1')
$cases = @(
    @{ Name='present-null classic package accepted'; Filter=[pscustomobject]@{Package=$null}; Kind='NULL'; Expected=$true },
    @{ Name='retained literal Any accepted'; Filter=[pscustomobject]@{Package='Any'}; Kind='ANY'; Expected=$true },
    @{ Name='missing property rejected'; Filter=[pscustomobject]@{Unrelated=$null}; Kind='MISSING'; Expected=$false },
    @{ Name='missing filter rejected'; Filter=$null; Kind='MISSING'; Expected=$false },
    @{ Name='empty string rejected'; Filter=[pscustomobject]@{Package=''}; Kind='EMPTY'; Expected=$false },
    @{ Name='whitespace rejected'; Filter=[pscustomobject]@{Package=' '}; Kind='OTHER_STRING'; Expected=$false },
    @{ Name='case variant rejected'; Filter=[pscustomobject]@{Package='any'}; Kind='ANY_CASE_VARIANT'; Expected=$false },
    @{ Name='specific package SID rejected'; Filter=[pscustomobject]@{Package='S-1-15-2-1'}; Kind='SID'; Expected=$false },
    @{ Name='unknown package string rejected'; Filter=[pscustomobject]@{Package='fixture-package'}; Kind='OTHER_STRING'; Expected=$false },
    @{ Name='singleton array is not a string'; Filter=[pscustomobject]@{Package=@('Any')}; Kind='OTHER_TYPE'; Expected=$false },
    @{ Name='empty array is not null'; Filter=[pscustomobject]@{Package=@()}; Kind='OTHER_TYPE'; Expected=$false },
    @{ Name='numeric value rejected'; Filter=[pscustomobject]@{Package=0}; Kind='OTHER_TYPE'; Expected=$false },
    @{ Name='boolean value rejected'; Filter=[pscustomobject]@{Package=$false}; Kind='OTHER_TYPE'; Expected=$false },
    @{ Name='object value rejected'; Filter=[pscustomobject]@{Package=[pscustomobject]@{Value='Any'}}; Kind='OTHER_TYPE'; Expected=$false }
)
$report = [ordered]@{ schema=1; kind='pure-firewall-package-contracts'; passed=$false;
    required_checks=15; passed_checks=0; skipped_checks=0; failed_case=$null;
    firewall_api_executed=$false; core_executed=$false; driver_loaded=$false }
try {
    foreach ($case in $cases) {
        $report.failed_case = $case.Name
        $kind = Get-FirewallPackageKind $case.Filter
        $actual = Test-UnrestrictedFirewallPackage $case.Filter
        if ($kind -cne $case.Kind -or $actual -isnot [bool] -or $actual -ne $case.Expected) {
            throw 'FIREWALL_PACKAGE_CONTRACT_FAILED'
        }
        $report.passed_checks++
        Write-Output ('PASS ' + $case.Name)
    }
    $report.failed_case = 'legacy predicate rejects observed present-null'
    $observed = [pscustomobject]@{Package=$null}
    if ($observed.Package -ceq 'Any') { throw 'FIREWALL_PACKAGE_NEGATIVE_CONTROL_FAILED' }
    $report.passed_checks++
    Write-Output ('PASS ' + $report.failed_case)
    $report.failed_case = $null
    $report.passed = $report.passed_checks -eq $report.required_checks
} catch {
    # Publish only the fixed case label. No provider/filter value is printed.
    Write-Output 'FIREWALL_PACKAGE_CONTRACT_FAILED'
}
$output = Join-Path $PSScriptRoot '../../artifacts/ci/windows-firewall-package-contracts.json'
$null = [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($output))
$bytes = [Text.Encoding]::UTF8.GetBytes(($report | ConvertTo-Json -Depth 4 -Compress))
$file = [IO.File]::Open($output, [IO.FileMode]::CreateNew, [IO.FileAccess]::Write, [IO.FileShare]::None)
try { $file.Write($bytes,0,$bytes.Length); $file.Flush($true) } finally { $file.Dispose() }
if (!$report.passed) { exit 1 }
Write-Output 'RESULT 15 passed; 0 skipped'
exit 0

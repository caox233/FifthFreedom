# Read-only inventory. No service/device/driver modification occurs here.
param([ValidateSet('Before','After')][string]$Phase = 'Before')
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$stage = 'HOST'
try {
    if ($env:GITHUB_ACTIONS -cne 'true' -or $env:RUNNER_ENVIRONMENT -cne 'github-hosted' -or $env:ImageOS -cnotin @('win25','win25-vs2026')) { throw 'HOST' }
    $principal = [Security.Principal.WindowsPrincipal]::new([Security.Principal.WindowsIdentity]::GetCurrent())
    if (!$principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'HOST' }
    $stage = 'PNP'
    # No -PresentOnly: stale/nonpresent devices also block the first load.
    $devices = @(Get-PnpDevice -ErrorAction Stop)
    if ($devices.Count -eq 0 -or $devices.Count -gt 20000) { throw 'PNP' }
    $wintunDevices = 0
    $unrelated = [Collections.Generic.List[string]]::new()
    foreach ($device in $devices) {
        if ([string]::IsNullOrWhiteSpace($device.InstanceId)) { throw 'PNP' }
        $isWintun = $device.InstanceId -match '(?i)wintun' -or $device.FriendlyName -match '(?i)wintun'
        if ($device.Class -eq 'Net' -or $isWintun) {
            # Enumerate properties rather than interpreting a failed missing-key
            # query as absence. An enumeration failure is always fatal.
            $properties = @(Get-PnpDeviceProperty -InstanceId $device.InstanceId -ErrorAction Stop)
            if ($properties.Count -eq 0) { throw 'PNP' }
            foreach ($property in $properties) {
                if ($property.KeyName -in @('DEVPKEY_Device_HardwareIds','DEVPKEY_Device_Service')) {
                    foreach ($value in @($property.Data)) {
                        if ($null -ne $value -and $value -isnot [string]) { throw 'PNP' }
                        if ([string]$value -match '(?i)wintun') { $isWintun = $true }
                    }
                }
            }
            if ($isWintun) { $wintunDevices++ }
            else { $unrelated.Add([string]$device.InstanceId) }
        }
    }
    $stage = 'DRIVER_STORE'
    $drivers = @(Get-WindowsDriver -Online -All -ErrorAction Stop)
    if ($drivers.Count -eq 0 -or $drivers.Count -gt 20000) { throw 'DRIVER_STORE' }
    $wintunPackages = 0
    foreach ($driver in $drivers) {
        if ([string]::IsNullOrWhiteSpace($driver.OriginalFileName)) { throw 'DRIVER_STORE' }
        if ($driver.OriginalFileName -match '(?i)wintun') { $wintunPackages++ }
    }
    $stage = 'DRIVER_SERVICE'
    $services = @(Get-CimInstance Win32_SystemDriver -ErrorAction Stop)
    if ($services.Count -eq 0 -or $services.Count -gt 20000) { throw 'DRIVER_SERVICE' }
    $wintunServices = @($services | Where-Object { $_.Name -match '(?i)wintun' -or $_.PathName -match '(?i)wintun' }).Count
    $registryState = Test-Path -LiteralPath 'HKLM:\SYSTEM\CurrentControlSet\Services\Wintun' -ErrorAction Stop
    $driverFile = Test-Path -LiteralPath "$env:SystemRoot\System32\drivers\wintun.sys" -ErrorAction Stop
    $clean = $wintunDevices -eq 0 -and $wintunPackages -eq 0 -and $wintunServices -eq 0 -and !$registryState -and !$driverFile
    $stage = 'PREEXISTING_STATE'
    if ($Phase -eq 'Before' -and !$clean) { throw 'PREEXISTING_STATE' }
    # Hash only; do not publish network adapter names/instance IDs.
    $serialized = [Text.Encoding]::UTF8.GetBytes((@($unrelated | Sort-Object -Unique) -join "`n"))
    $digest = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($serialized)).ToLowerInvariant()
    [ordered]@{ schema=1; code='OK'; phase=$Phase; clean=$clean; wintun_devices=$wintunDevices;
        wintun_packages=$wintunPackages; wintun_services=$wintunServices; driver_registry=$registryState;
        driver_file=$driverFile; unrelated_pnp_sha256=$digest; unrelated_pnp_count=$unrelated.Count } | ConvertTo-Json -Compress
    exit 0
} catch {
    [ordered]@{ schema=1; code=$stage; phase=$Phase } | ConvertTo-Json -Compress
    exit 1
}

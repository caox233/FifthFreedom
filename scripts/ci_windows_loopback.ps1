param([Parameter(Mandatory = $true)][string]$Report)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

# Read-only observations on the disposable hosted runner. No service, HTTP.sys
# reservation, firewall, URL ACL or excluded-port setting is changed.
$result = [ordered]@{
    schema = 1
    query_complete = $false
    listener_80_present = $false
    listener_80_system = $false
    w3svc_running = $false
    msdepsvc_running = $false
    http_sys_msdeploy_agent_registered = $false
    tcp_exclusion_contains_80 = $false
    bind_result = 'NOT_ATTEMPTED'
    services_changed = $false
}
try {
    $listeners = @(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object LocalPort -eq 80)
    $result.listener_80_present = $listeners.Count -gt 0
    $result.listener_80_system = @($listeners | Where-Object OwningProcess -eq 4).Count -gt 0
    $services = @(Get-Service -ErrorAction Stop)
    $result.w3svc_running = @($services | Where-Object { $_.Name -eq 'W3SVC' -and $_.Status -eq 'Running' }).Count -gt 0
    $result.msdepsvc_running = @($services | Where-Object { $_.Name -eq 'MsDepSvc' -and $_.Status -eq 'Running' }).Count -gt 0
    $httpState = & netsh.exe http show servicestate view=requestq 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'READBACK_FAILED' }
    $result.http_sys_msdeploy_agent_registered = ($httpState -join "`n") -match '(?i)/MSDEPLOYAGENTSERVICE/'
    $exclusions = & netsh.exe interface ipv4 show excludedportrange protocol=tcp 2>$null
    if ($LASTEXITCODE -ne 0 -or ($exclusions -join "`n") -notmatch 'Start Port\s+End Port') { throw 'READBACK_FAILED' }
    foreach ($line in $exclusions) {
        if ($line -match '^\s*(\d+)\s+(\d+)\s*\*?\s*$') {
            if ([int]$Matches[1] -le 80 -and [int]$Matches[2] -ge 80) {
                $result.tcp_exclusion_contains_80 = $true
            }
        }
    }
    $result.query_complete = $true
}
catch { $result.query_complete = $false }

$socket = $null
try {
    $socket = [System.Net.Sockets.Socket]::new([System.Net.Sockets.AddressFamily]::InterNetwork,
        [System.Net.Sockets.SocketType]::Stream, [System.Net.Sockets.ProtocolType]::Tcp)
    $socket.ExclusiveAddressUse = $true
    $socket.Bind([System.Net.IPEndPoint]::new([System.Net.IPAddress]::Loopback, 80))
    $result.bind_result = 'BOUND_AND_CLOSED'
}
catch {
    $errorObject = $_.Exception
    $result.bind_result = 'OTHER'
    for ($depth = 0; $depth -lt 4; $depth++) {
        if ($errorObject -is [System.Net.Sockets.SocketException]) {
            $result.bind_result = switch ($errorObject.SocketErrorCode) {
                'AccessDenied' { 'WSAEACCES' }
                'AddressAlreadyInUse' { 'ADDRINUSE' }
                default { 'OTHER' }
            }
            break
        }
        if ($null -eq $errorObject.InnerException) { break }
        $errorObject = $errorObject.InnerException
    }
}
finally { if ($null -ne $socket) { $socket.Dispose() } }

try {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes(($result | ConvertTo-Json -Depth 4))
    $file = [System.IO.File]::Open($Report, [System.IO.FileMode]::CreateNew)
    try { $file.Write($bytes, 0, $bytes.Length) } finally { $file.Dispose() }
}
catch { Write-Output '::error::WINDOWS_LOOPBACK_REPORT_FAILED'; exit 1 }
$fields = @('query_complete', 'listener_80_present', 'listener_80_system', 'w3svc_running',
    'msdepsvc_running', 'http_sys_msdeploy_agent_registered', 'tcp_exclusion_contains_80', 'bind_result', 'services_changed')
$summary = ($fields | ForEach-Object { $_ + '=' + [string]$result[$_] }) -join '; '
Write-Output ('::notice title=Windows fixed loopback prerequisite::' + $summary)
if (-not $result.query_complete) { exit 1 }

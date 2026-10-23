param(
    [Parameter(Mandatory = $true)][ValidateSet('Prepare', 'Restore')][string]$Mode,
    [Parameter(Mandatory = $true)][string]$State,
    [Parameter(Mandatory = $true)][string]$Report
)
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$appcmd = 'C:\Windows\System32\inetsrv\appcmd.exe'
$siteFacts = [ordered]@{ stage = 'NOT_STARTED'; appcmd_available = $false; readback_succeeded = $false;
    xml_parsed = $false; default_name_present = $false; default_name_unique = $false; id_is_one = $false;
    state_started = $false; state_stopped = $false; exact_http80_binding = $false;
    has_http80_binding = $false; has_https443_binding = $false; other_site_80 = $false;
    additional_binding = $false; site_count = 'UNREAD'; binding_count = 'UNREAD' }
function Small-Count($Value) { if ($Value -ge 0 -and $Value -le 8) { return [string]$Value }; return 'OVER8' }

function Write-NewJson($Path, $Value) {
    $bytes = [System.Text.Encoding]::UTF8.GetBytes(($Value | ConvertTo-Json -Depth 4))
    $file = [System.IO.File]::Open($Path, [System.IO.FileMode]::CreateNew)
    try { $file.Write($bytes, 0, $bytes.Length) } finally { $file.Dispose() }
}
function Read-DefaultSite {
    $siteFacts.stage = 'READ_APPCMD'
    $siteFacts.appcmd_available = Test-Path -LiteralPath $appcmd -PathType Leaf
    $raw = & $appcmd list site /xml 2>$null
    if ($LASTEXITCODE -ne 0) { throw 'IIS_READBACK_FAILED' }
    $siteFacts.readback_succeeded = $true
    $siteFacts.stage = 'PARSE_XML'
    [xml]$document = $raw -join "`n"
    $siteFacts.xml_parsed = $true
    $sites = @($document.appcmd.SITE)
    $siteFacts.site_count = Small-Count $sites.Count
    $siteFacts.stage = 'CHECK_NAME'
    $matchingSites = @($sites | Where-Object { $_.'SITE.NAME' -eq 'Default Web Site' })
    $siteFacts.default_name_present = $matchingSites.Count -gt 0
    $siteFacts.default_name_unique = $matchingSites.Count -eq 1
    if ($matchingSites.Count -ne 1) { throw 'IIS_DEFAULT_SITE_UNVERIFIED' }
    $site = $matchingSites[0]
    $siteFacts.stage = 'CHECK_ATTRIBUTES'
    $siteFacts.id_is_one = [string]$site.'SITE.ID' -eq '1'
    $siteFacts.state_started = [string]$site.state -eq 'Started'
    $siteFacts.state_stopped = [string]$site.state -eq 'Stopped'
    $bindings = @(([string]$site.bindings).Split(','))
    $siteFacts.binding_count = Small-Count $bindings.Count
    $siteFacts.additional_binding = $bindings.Count -ne 1
    $siteFacts.exact_http80_binding = [string]$site.bindings -eq 'http/*:80:'
    $siteFacts.has_http80_binding = @($bindings | Where-Object { $_ -match '^http/.*:80:' }).Count -gt 0
    $siteFacts.has_https443_binding = @($bindings | Where-Object { $_ -match '^https/.*:443:' }).Count -gt 0
    if (-not $siteFacts.id_is_one -or -not $siteFacts.exact_http80_binding -or
        -not ($siteFacts.state_started -or $siteFacts.state_stopped)) { throw 'IIS_DEFAULT_SITE_UNVERIFIED' }
    $siteFacts.stage = 'CHECK_OTHER_SITES'
    # A different configured site on port 80 is outside this preparation scope.
    foreach ($other in $sites) {
        if ($other.'SITE.NAME' -ne 'Default Web Site' -and [string]$other.bindings -match ':80:') {
            $siteFacts.other_site_80 = $true
            throw 'IIS_OTHER_SITE_UNVERIFIED'
        }
    }
    $siteFacts.stage = 'VERIFIED'
    return [string]$site.state
}
function Test-OwnedPort {
    $socket = $null
    try {
        $socket = [System.Net.Sockets.Socket]::new([System.Net.Sockets.AddressFamily]::InterNetwork,
            [System.Net.Sockets.SocketType]::Stream, [System.Net.Sockets.ProtocolType]::Tcp)
        $socket.ExclusiveAddressUse = $true
        $socket.Bind([System.Net.IPEndPoint]::new([System.Net.IPAddress]::Loopback, 80))
        return $true
    }
    catch { return $false }
    finally { if ($null -ne $socket) { $socket.Dispose() } }
}

$result = [ordered]@{ schema = 1; mode = $Mode; succeeded = $false; default_site_confirmed = $false;
    default_site_stopped = $false; default_site_restored = $false; owned_bind_verified = $false;
    unknown_services_changed = $false; port_rules_changed = $false }
try {
    if ($env:RUNNER_ENVIRONMENT -ne 'github-hosted' -or $env:GITHUB_ACTIONS -ne 'true') {
        throw 'HOSTED_ONLY'
    }
    if ($Mode -eq 'Prepare') {
        if (Test-Path -LiteralPath $State) { throw 'STATE_EXISTS' }
        if (Test-OwnedPort) {
            $result.owned_bind_verified = $true
        }
        else {
            if ((Read-DefaultSite) -ne 'Started') { throw 'IIS_DEFAULT_SITE_UNVERIFIED' }
            $result.default_site_confirmed = $true
            # Record the exact original state before the reversible operation.
            Write-NewJson $State ([ordered]@{ schema = 1; original_started = $true; default_site_id = 1;
                exact_default_http80_binding = $true })
            $discard = & $appcmd stop site '/site.name:Default Web Site' 2>$null
            if ($LASTEXITCODE -ne 0 -or (Read-DefaultSite) -ne 'Stopped') { throw 'IIS_STOP_FAILED' }
            $result.default_site_stopped = $true
            for ($attempt = 0; $attempt -lt 20; $attempt++) {
                if (Test-OwnedPort) { $result.owned_bind_verified = $true; break }
                Start-Sleep -Milliseconds 250
            }
            if (-not $result.owned_bind_verified) { throw 'FIXED_PORT_STILL_UNAVAILABLE' }
        }
    }
    elseif (Test-Path -LiteralPath $State) {
        $plan = Get-Content -LiteralPath $State -Raw | ConvertFrom-Json
        if ($plan.schema -ne 1 -or $plan.original_started -isnot [bool] -or -not $plan.original_started -or
            $plan.default_site_id -ne 1 -or $plan.exact_default_http80_binding -isnot [bool] -or
            -not $plan.exact_default_http80_binding) { throw 'RESTORE_PLAN_INVALID' }
        $current = Read-DefaultSite
        $result.default_site_confirmed = $true
        if ($current -eq 'Stopped') {
            $discard = & $appcmd start site '/site.name:Default Web Site' 2>$null
            if ($LASTEXITCODE -ne 0) { throw 'IIS_RESTORE_FAILED' }
        }
        if ((Read-DefaultSite) -ne 'Started') { throw 'IIS_RESTORE_FAILED' }
        $result.default_site_restored = $true
    }
    $result.succeeded = $true
}
catch {
    $result.succeeded = $false
    $allowed = @('HOSTED_ONLY', 'STATE_EXISTS', 'IIS_READBACK_FAILED', 'IIS_DEFAULT_SITE_UNVERIFIED',
        'IIS_OTHER_SITE_UNVERIFIED', 'IIS_STOP_FAILED', 'FIXED_PORT_STILL_UNAVAILABLE',
        'RESTORE_PLAN_INVALID', 'IIS_RESTORE_FAILED')
    $result.error_code = if ($_.Exception.Message -in $allowed) { $_.Exception.Message } else { 'IIS_OPERATION_UNCLASSIFIED' }
}
$result.site_facts = $siteFacts
try { Write-NewJson $Report $result }
catch { Write-Output '::error::WINDOWS_IIS_RECEIPT_FAILED'; exit 1 }
$fields = @('mode', 'succeeded', 'default_site_confirmed', 'default_site_stopped',
    'default_site_restored', 'owned_bind_verified', 'unknown_services_changed', 'port_rules_changed')
$summary = ($fields | ForEach-Object { $_ + '=' + [string]$result[$_] }) -join '; '
Write-Output ('::notice title=Scoped hosted IIS preparation::' + $summary)
$shape = ($siteFacts.Keys | ForEach-Object { $_ + '=' + [string]$siteFacts[$_] }) -join '; '
Write-Output ('::notice title=Fixed IIS source shape::' + $shape)
if ($result.Contains('error_code')) { Write-Output ('::error::' + $result.error_code) }
if (-not $result.succeeded) { Write-Output '::error::WINDOWS_DEFAULT_IIS_PREPARATION_FAILED'; exit 1 }

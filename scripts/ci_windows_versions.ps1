param(
    [Parameter(Mandatory = $true)][string]$Launcher,
    [Parameter(Mandatory = $true)][string]$Hook,
    [Parameter(Mandatory = $true)][string]$Server
)

$ErrorActionPreference = 'Stop'
if (-not $IsWindows) { throw 'WINDOWS_RESOURCE_INSPECTION_REQUIRED' }

# FileVersionInfo reads PE resources. It never loads the DLL or starts a process.
$inputs = [ordered]@{ launcher = $Launcher; main_hook = $Hook; server = $Server }
$rows = [ordered]@{}
foreach ($role in $inputs.Keys) {
    $item = Get-Item -LiteralPath $inputs[$role]
    if ($item.PSIsContainer -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) {
        throw 'WINDOWS_RESOURCE_INPUT_INVALID'
    }
    $version = [Diagnostics.FileVersionInfo]::GetVersionInfo($item.FullName)
    if ($version.ProductName -cne 'Fifth Freedom') { throw 'WINDOWS_PRODUCT_NAME_MISMATCH' }
    if ($role -eq 'server' -and $version.OriginalFilename -cne 'FifthFreedom.Server.exe') {
        throw 'WINDOWS_SERVER_ORIGINAL_FILENAME_MISMATCH'
    }
    $rows[$role] = [ordered]@{
        product_name = $version.ProductName
        file_description = $version.FileDescription
        original_filename = $version.OriginalFilename
        bytes = $item.Length
        sha256 = (Get-FileHash -LiteralPath $item.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    }
}
[ordered]@{ schema = 1; inspected_without_execution = $true; components = $rows } |
    ConvertTo-Json -Depth 5 -Compress

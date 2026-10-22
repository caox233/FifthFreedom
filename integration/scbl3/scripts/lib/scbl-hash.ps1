function Get-ScblSha256 {
    [CmdletBinding()]
    param([Parameter(Mandatory=$true)][string]$LiteralPath)

    $Resolved = [IO.Path]::GetFullPath($LiteralPath)
    if (![IO.File]::Exists($Resolved)) {
        throw "SHA256 input file does not exist: $Resolved"
    }

    $Stream = [IO.File]::Open($Resolved, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::Read)
    $Algorithm = [Security.Cryptography.SHA256]::Create()
    try {
        return ([BitConverter]::ToString($Algorithm.ComputeHash($Stream))).Replace('-', '').ToLowerInvariant()
    }
    finally {
        $Algorithm.Dispose()
        $Stream.Dispose()
    }
}

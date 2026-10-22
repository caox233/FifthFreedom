using System.Security.Cryptography;
using System.Text;

using System;
namespace SplinterCellCNLauncher.Services;

public static class AccountIdService
{
    public static string CreateStableAccountId(string username)
    {
        // Registration needs a stable Ubisoft-compatible identifier before the
        // first successful login returns the authoritative server account ID.
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(username.Trim().ToLowerInvariant()));
        return new Guid(hash.AsSpan(0, 16)).ToString();
    }
}

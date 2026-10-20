using System;
using System.Security.Cryptography;
using System.Text;

namespace ConnectionHub.Services;

/// <summary>Encrypts secrets with Windows DPAPI, bound to the current Windows user.</summary>
public static class CredentialProtector
{
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("ConnectionHub.v1");

    public static string? Protect(string? plain)
    {
        if (string.IsNullOrEmpty(plain)) return null;
        var data = ProtectedData.Protect(Encoding.UTF8.GetBytes(plain), Entropy, DataProtectionScope.CurrentUser);
        return Convert.ToBase64String(data);
    }

    public static string? Unprotect(string? protectedValue)
    {
        if (string.IsNullOrEmpty(protectedValue)) return null;
        try
        {
            var data = ProtectedData.Unprotect(Convert.FromBase64String(protectedValue), Entropy, DataProtectionScope.CurrentUser);
            return Encoding.UTF8.GetString(data);
        }
        catch (Exception)
        {
            // Data was encrypted by another user or machine.
            return null;
        }
    }
}

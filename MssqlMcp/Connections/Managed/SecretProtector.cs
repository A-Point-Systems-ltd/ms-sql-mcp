using System.Security.Cryptography;
using System.Text;

namespace Mssql.McpServer.Connections.Managed;

/// <summary>Encrypts managed-connection passwords at rest.</summary>
public interface ISecretProtector
{
    string Protect(string secret);

    /// <summary>Returns null when the blob cannot be decrypted (another Windows user or machine, or corrupt).</summary>
    string? TryUnprotect(string blob);
}

/// <summary>
/// Windows DPAPI, CurrentUser scope: only the Windows account that saved a password can decrypt it, on that machine
/// (roaming profiles aside). Anything running as that account can decrypt it too - the same trust as VS Code SecretStorage.
/// </summary>
public sealed class DpapiSecretProtector : ISecretProtector
{
    // Fixed entropy scopes the blobs to this app: another DPAPI consumer of the same user cannot use them by accident.
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("APoint-ms-sql/managed-connections/v1");

    public string Protect(string secret)
    {
        if (!OperatingSystem.IsWindows())
        {
            throw new PlatformNotSupportedException("Saved passwords require Windows (DPAPI).");
        }

        return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), Entropy, DataProtectionScope.CurrentUser));
    }

    public string? TryUnprotect(string blob)
    {
        if (!OperatingSystem.IsWindows())
        {
            return null;
        }

        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(blob), Entropy, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException)
        {
            return null;
        }
    }
}

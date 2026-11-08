using System.Security.Cryptography;

namespace Entanglo.PInvoke;

/// <summary>Thin helpers around System.Security.Cryptography.ProtectedData (DPAPI).</summary>
public static class Dpapi
{
    public static byte[] Protect(byte[] data) =>
        ProtectedData.Protect(data, null, DataProtectionScope.CurrentUser);

    public static byte[] Unprotect(byte[] data) =>
        ProtectedData.Unprotect(data, null, DataProtectionScope.CurrentUser);
}

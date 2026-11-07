using System;
using System.Security.Cryptography;
using System.Text;

namespace LiveClash.Core
{
    /// <summary>SHA-256 as upper-case hex, for the "did this change?" hashes of the clash schedule (a record, a shared-parameter file). One place for the .NET 4.8 / .NET 8+ split.</summary>
    internal static class Sha256Hex
    {
        public static string Of(byte[] data)
        {
#if NET5_0_OR_GREATER
            return Convert.ToHexString(SHA256.HashData(data));
#else
            using (var sha = SHA256.Create())
            {
                var sb = new StringBuilder();
                foreach (var b in sha.ComputeHash(data)) sb.Append(b.ToString("X2", System.Globalization.CultureInfo.InvariantCulture));
                return sb.ToString();
            }
#endif
        }
    }
}

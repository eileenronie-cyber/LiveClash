using System.Security.Cryptography;
using System.Text;

namespace LiveClash.Core
{
    /// <summary>
    /// A short, deterministic, filename- and eyeball-safe tag for a string.
    ///
    /// Never an identity in its own right — only used to tell two things that share a label
    /// apart (<see cref="ModelIdentity"/>), or to let two machines compare "same content?"
    /// without comparing the whole thing (<see cref="RulesFingerprint"/>). 4 bytes of SHA-1 is
    /// nowhere near collision-proof, and does not need to be: the handful of models one person
    /// opens on one PC, or the handful of colleagues on one project, is not the birthday-problem
    /// regime this would need to worry about.
    /// </summary>
    public static class ShortHash
    {
        public static string Of(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            using (var sha = SHA1.Create())
            {
                var bytes = sha.ComputeHash(Encoding.UTF8.GetBytes(s));
                var sb = new StringBuilder(8);
                for (int i = 0; i < 4; i++) sb.Append(bytes[i].ToString("x2"));
                return sb.ToString();
            }
        }
    }
}

using System;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// The name that goes into a ledger's "by" column.
    ///
    /// Used to be <see cref="Environment.UserName"/>, the Windows login. Worksharing's own
    /// status APIs (<c>GetCheckoutStatus</c>, badges — a later step) report Revit USERNAMES,
    /// which are commonly different from the Windows login on a shared or cloud-hosted machine —
    /// "Borrowed by jdoe" and "Coordinated by ADMIN" would read as two different people when
    /// they are the same one. Caching <c>doc.Application.Username</c> the first time a document
    /// is seen keeps every ledger write comparable to what badges will show — it does not change
    /// mid-session, so one document is enough to learn it for the rest of the Revit process.
    /// </summary>
    public static class RevitUser
    {
        private static string _cached;

        /// <summary>Call whenever a document becomes the active one — LoadFor is the natural spot.</summary>
        public static void Note(Document doc)
        {
            try
            {
                if (doc == null || doc.Application == null) return;
                var u = doc.Application.Username;
                if (!string.IsNullOrEmpty(u)) _cached = u;
            }
            catch { }
        }

        /// <summary>
        /// The Revit username once a document has been seen this session; the Windows login
        /// before that (matching what every ledger recorded before this existed) or if Revit's
        /// own username is unavailable.
        /// </summary>
        public static string Current
        {
            get
            {
                if (!string.IsNullOrEmpty(_cached)) return _cached;
                try { return Environment.UserName; } catch { return ""; }
            }
        }

    }
}

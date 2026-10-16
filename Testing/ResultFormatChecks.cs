using System;
using LiveClash.Core;

namespace LiveClash.Testing
{
    /// <summary>
    /// The small formats a clash row and a ledger entry are known by: the result key (the identity
    /// every store, ledger and de-duplication uses), the row's display text, and the resolved
    /// ledger's header and display fields. Revit-free, so both tiers run them.
    /// </summary>
    public static class ResultFormatChecks
    {
        private const string P = "formats: ";

        public static void RunAll(Action<string, bool?> check)
        {
            check(P + "a same-document key puts the lower id first, so A-hits-B and B-hits-A are one clash",
                ClashResult.MakeKey(200, null, 100) == "100|0|200" && ClashResult.MakeKey(100, null, 200) == "100|0|200");
            check(P + "a same-document key for one id twice is still well formed", ClashResult.MakeKey(7, null, 7) == "7|0|7");
            check(P + "a linked key keeps host first and names the link instance, never reordered",
                ClashResult.MakeKey(200, 55, 100) == "200|55|100" && ClashResult.MakeKey(100, 55, 200) == "100|55|200");
            check(P + "a link instance id of 0 is still a linked key, not a same-document one",
                ClashResult.MakeKey(200, 0, 100) == "200|0|100");

            var row = new ClashResult { HostElementName = "Pipe 1", OtherElementName = "Beam 2", ModelName = "STR" };
            check(P + "a clash row reads 'host  →  other  [model]'", row.ToString() == "Pipe 1  →  Beam 2  [STR]");

            var when = new DateTime(2026, 10, 4, 6, 30, 0, DateTimeKind.Utc);
            var rec = new ResolvedRecord
            {
                WhenUtc = when, HostCategory = "Pipes", HostName = "CHW 150", OtherCategory = "Walls", OtherName = "Core 200"
            };
            check(P + "a resolved entry shows 'Category: Name' for both sides",
                rec.HostText == "Pipes: CHW 150" && rec.OtherText == "Walls: Core 200");
            check(P + "a resolved entry shows its time in local time, day month hour:minute",
                rec.WhenText == when.ToLocalTime().ToString("dd MMM HH:mm") && rec.WhenText.Length >= 12);

            var bare = ResolvedLogReplay.Header("model-a", "");
            var claimed = ResolvedLogReplay.Header("model-a", "abc123");
            var nl = Environment.NewLine;
            check(P + "the resolved ledger header names itself, its columns and its model",
                bare.StartsWith("# LiveClash resolved log - append-only" + nl)
                && bare.Contains("# key\tutc\tby\thostId\thostName\thostCat\totherId\totherName\totherCat\tmodel\t[event\tretractedUtc]" + nl)
                && bare.Contains("'Retracted'")
                && bare.EndsWith("# model=model-a" + nl));
            check(P + "a workshared model's header also carries its identity claim; a standalone one does not",
                claimed.EndsWith("# model=model-a" + nl + ModelIdentity.Line("abc123") + nl)
                && !bare.Contains(ModelIdentity.Line("abc123"))
                && ResolvedLogReplay.Header("model-a", null) == bare);
        }
    }
}

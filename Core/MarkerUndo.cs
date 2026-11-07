using System;
using System.Collections.Generic;

namespace LiveClash.Core
{
    /// <summary>
    /// Which Undo or Redo concerns the clash markers: one that undoes or redoes a LiveClash marker transaction (independent delta review
    /// 2026-11-01, D-M2). Pure; the names are those of <c>MarkerTx</c> and a Revit-tier check keeps the two lists in step.
    /// </summary>
    public static class MarkerUndo
    {
        public static readonly string[] Names =
        {
            "LiveClash: refresh clash markers",
            "LiveClash: set up clash markers",
            "LiveClash: write clash markers",
            "LiveClash: remove resolved clash markers",
            "LiveClash: remove all clash markers",
            "LiveClash: remove clash parameters",
            "LiveClash: show markers in my Focus view",
            "LiveClash: set clash status"
        };

        /// <summary>True when any of <paramref name="transactionNames"/> is one of the marker transactions (exact, ordinal).</summary>
        public static bool Concerns(IEnumerable<string> transactionNames)
        {
            if (transactionNames == null) return false;
            foreach (var name in transactionNames)
                if (name != null && Array.IndexOf(Names, name) >= 0) return true;
            return false;
        }
    }
}

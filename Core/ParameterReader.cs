using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    public enum ParamScope
    {
        /// <summary>Look on the instance, then fall back to its type.</summary>
        Either = 0,
        Instance = 1,
        Type = 2,
    }

    /// <summary>
    /// The single place a parameter value is turned into text.
    ///
    /// Both the Browse scan and the match test go through here, and they MUST: if
    /// they ever read a value differently, a value the picker offered would compare
    /// unequal to what the check reads back and would silently match nothing. That
    /// is the worst kind of bug, because it looks like the model rather than the
    /// tool.
    ///
    /// AsValueString() is what matches the properties palette for numbers, yes/no
    /// and ElementId-valued parameters — crucially System Type, which is an
    /// ElementId underneath, so reading raw storage yields a number rather than
    /// "Supply Air". Text parameters need AsString(), because AsValueString()
    /// returns null for them.
    /// </summary>
    [RevitBound]
    public static class ParameterReader
    {
        public static string Read(Element e, string parameterName, ParamScope scope)
        {
            if (e == null || string.IsNullOrEmpty(parameterName)) return null;

            Parameter inst = null;
            if (scope == ParamScope.Instance || scope == ParamScope.Either)
                inst = Lookup(e, parameterName);

            string instVal = inst == null ? null : ValueOf(inst, e);

            // Fall back to the type when the instance parameter is ABSENT *or present but
            // EMPTY*. "Family Name" and "Type Name" exist as blank instance shadows and
            // carry their real value only on the type — so an Either read that stopped at a
            // found-but-empty instance parameter came back empty, which is why the filter
            // could not pull elements by family/type name even though the audit could (it
            // reads ElementType.FamilyName/Name directly). Instance scope stays instance-only.
            if ((inst == null || string.IsNullOrEmpty(instVal))
                && (scope == ParamScope.Type || scope == ParamScope.Either))
            {
                var type = TypeOf(e);
                if (type != null)
                {
                    var tp = Lookup(type, parameterName);
                    if (tp != null)
                    {
                        var typeVal = ValueOf(tp, type);
                        if (!string.IsNullOrEmpty(typeVal)) return typeVal;
                    }
                }
            }

            return instVal;
        }

        public static Element TypeOf(Element e)
        {
            try
            {
                var tid = e.GetTypeId();
                if (tid == null || tid == ElementId.InvalidElementId) return null;
                return e.Document.GetElement(tid);
            }
            catch { return null; }
        }

        private static Parameter Lookup(Element e, string name)
        {
            // LookupParameter returns the first match by name, which is what the user
            // means when they pick a name off the palette.
            try { return e.LookupParameter(name); }
            catch { return null; }
        }

        public static string ValueOf(Parameter p, Element owner)
        {
            try
            {
                if (p.StorageType == StorageType.String)
                {
                    var s = p.AsString();
                    return s ?? p.AsValueString();
                }

                var vs = p.AsValueString();
                if (!string.IsNullOrEmpty(vs)) return vs;

                switch (p.StorageType)
                {
                    // Invariant: this text is what a picked value is saved as and compared against, and
                    // filters travel between seats (.lcfilter presets). A comma-decimal Windows locale
                    // would otherwise write "1,5" where a colleague's seat reads "1.5".
                    case StorageType.Integer: return p.AsInteger().ToString(System.Globalization.CultureInfo.InvariantCulture);
                    case StorageType.Double: return p.AsDouble().ToString(System.Globalization.CultureInfo.InvariantCulture);
                    case StorageType.ElementId:
                        var id = p.AsElementId();
                        if (id == null || id == ElementId.InvalidElementId) return "";
                        var t = owner != null ? owner.Document.GetElement(id) : null;
                        return t != null ? t.Name : IdUtil.Value(id).ToString(System.Globalization.CultureInfo.InvariantCulture);
                }
            }
            catch { }
            return null;
        }
    }
}

using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// ElementId.IntegerValue was deprecated in the Revit 2024 API in favour of
    /// the 64-bit ElementId.Value. This keeps one source tree building against both.
    ///
    /// Kept in its own file (not buried in ClashResult) so the small Revit-free-at-
    /// runtime helpers can be linked into the headless CI test project without
    /// dragging the whole result/geometry graph along.
    /// </summary>
    public static class IdUtil
    {
        public static long Value(ElementId id)
        {
            if (id == null) return 0L;
#if R24_PLUS
            return id.Value;
#else
            return id.IntegerValue;
#endif
        }

        /// <summary>
        /// The inverse: a raw id value back into an ElementId.
        ///
        /// THE ONLY PLACE THAT CONSTRUCTS ONE FROM A NUMBER. There were three copies of
        /// this #if, in DeferredQueue, RecordWindow and RecordRecheckHandler — and the
        /// third one was written without the guard, as a bare `new ElementId(host)`. On
        /// 2024+ that binds to the long overload and is correct; before 2024 ElementId
        /// has no long constructor, so the argument bound to the BuiltInParameter
        /// overload instead and the whole project stopped compiling for Revit 2022/2023.
        /// Three errors, all in one file, on a target the csproj branches for — and, at
        /// the time, one Commands.cs claimed outright ("everything this project targets
        /// (2022+)"). That claim is gone: the supported range is 2024-2026, what the gate
        /// builds. This helper stays regardless, because the csproj still branches below
        /// 2025 and an unguarded construction is a compile break waiting to happen.
        ///
        /// A helper beside <see cref="Value"/> makes the omission unrepresentable: the
        /// version split lives once, in the file that already owns it.
        /// </summary>
        public static ElementId From(long value)
        {
#if R24_PLUS
            return new ElementId(value);
#else
            return new ElementId((int)value);
#endif
        }
    }
}

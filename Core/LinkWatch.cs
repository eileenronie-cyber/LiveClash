using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// The single most recent link change worth telling the user about, plus the
    /// instance-id → name map needed to name one that gets DELETED before it can be
    /// asked its own name.
    ///
    /// The cache fixes (D1/D2, L1/L2, the LinkedResourceOpened fix) make the NEXT
    /// check see a changed link. They cannot correct what is already listed, and a
    /// link change can create clashes against elements nobody edited this session —
    /// this exists to make that visible instead of silent.
    ///
    /// Deliberately holds only the LATEST change, not a history: the panel notice is
    /// "something changed since your last check", not a log, and a second change
    /// while the first notice is still showing should replace it, not queue behind
    /// it — the newer one is the one still worth checking.
    ///
    /// Static and Revit-API-only — no UI reference. Every method is called from a
    /// Revit event handler already on the main thread; nothing here creates its own
    /// threading.
    ///
    /// THIS FILE IS THE REVIT-TOUCHING HALF. The notice, its wording and age, and the
    /// state transitions live in LinkWatchState.cs, which has no Autodesk dependency and
    /// is asserted headlessly (Testing\LinkWatchChecks.cs). This half only reads what
    /// Revit reports and calls those named transitions — keep it that way, so a change
    /// in what a notice MEANS stays testable.
    /// </summary>
    public static partial class LinkWatch
    {
        /// <summary>
        /// Rebuilds the instance-id → name map for <paramref name="doc"/> and clears any
        /// notice. Called from Adopt, so a notice never survives into a different model —
        /// including the burst of LinkedResourceOpened events Revit may raise for a
        /// model's own links while it opens, which are not a change worth reporting and
        /// would otherwise show a false notice the instant the model appears.
        /// </summary>
        public static void Reset(Document doc)
        {
            Adopt(BuildInstanceNames(doc));
        }

        /// <summary>
        /// Looks for a link change among elements a transaction touched or deleted.
        /// Called from OnDocumentChanged, ahead of the existing NoteChanged call — this
        /// does not change what NoteChanged does, only watches the same ids for something
        /// worth telling the user about.
        /// </summary>
        public static void Observe(Document doc, ICollection<ElementId> added,
            ICollection<ElementId> modified, ICollection<ElementId> deleted)
        {
            if (doc == null || !doc.IsValidObject) return;
            try
            {
                ObserveAddedOrModified(doc, added, false);
                ObserveAddedOrModified(doc, modified, true);

                if (deleted != null)
                    foreach (var id in deleted)
                        NoteDeleted(IdUtil.Value(id));
            }
            catch (Exception ex)
            {
                Log.Warn("Link-watch observation failed", ex);
            }
        }

        private static void ObserveAddedOrModified(Document doc, ICollection<ElementId> ids, bool wasModified)
        {
            if (ids == null) return;
            foreach (var id in ids)
            {
                Element el;
                try { el = doc.GetElement(id); } catch { continue; }

                var type = el as RevitLinkType;
                if (type != null)
                {
                    // Load's own companion DocumentChanged also names the TYPE, still
                    // LOADED — OnLinkedResourceOpened already reported that load, first
                    // and more specifically (it names the link from the event itself;
                    // this would only be able to name the type element). An UNLOADED
                    // status here is the one change this event is the SOLE way to hear
                    // about — Unload() raises no LinkedResourceOpened at all (measured).
                    LinkedFileStatus status;
                    try { status = type.GetLinkedFileStatus(); } catch { continue; }
                    if (status == LinkedFileStatus.Unloaded)
                        RecordUnloaded(SafeName(type));
                    continue;
                }

                var inst = el as RevitLinkInstance;
                if (inst != null)
                    NoteInstance(IdUtil.Value(inst.Id), NameOf(doc, inst), wasModified);
            }
        }

        /// <summary>
        /// The names of the link TYPES that point at <paramref name="linkedResourcePath"/> - the names the clash rows carry.
        ///
        /// WHY. The link-loaded event names the link from the FILE PATH, but a clash row's model is the link TYPE's name
        /// (LinkContext.DisplayName), and the two differ as soon as a type is renamed (Manage Links; common with cloud
        /// models, and with two types for one file). "What changed" compares by that name, so a path-derived one would match
        /// no row and read as "no change". Empty when the path cannot be matched to a type; the caller then keeps the
        /// path-derived name.
        /// </summary>
        internal static List<string> TypeNamesForPath(Document doc, string linkedResourcePath)
        {
            var names = new List<string>();
            if (doc == null || string.IsNullOrEmpty(linkedResourcePath)) return names;
            string want = FileKey(linkedResourcePath);
            if (want.Length == 0) return names;
            try
            {
                if (!doc.IsValidObject) return names;
                foreach (var el in new FilteredElementCollector(doc).OfClass(typeof(RevitLinkType)))
                {
                    try
                    {
                        var type = el as RevitLinkType;
                        if (type == null || string.IsNullOrEmpty(type.Name)) continue;
                        var reference = ExternalFileUtils.GetExternalFileReference(doc, type.Id);
                        if (reference == null) continue;
                        string path = ModelPathUtils.ConvertModelPathToUserVisiblePath(reference.GetAbsolutePath());
                        if (FileKey(path) != want) continue;
                        var name = TrimRvt(type.Name);
                        if (!names.Contains(name)) names.Add(name);
                    }
                    catch { /* one type that cannot be read must not hide the others */ }
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Link-watch: matching a linked file to its link type failed", ex);
            }
            return names;
        }

        /// <summary>A path reduced to what identifies the file: its name without folder or ".rvt", lower-case.</summary>
        internal static string FileKey(string path)
        {
            try { return TrimRvt(System.IO.Path.GetFileName((path ?? "").Replace('/', '\\')) ?? "").ToLowerInvariant(); }
            catch { return ""; }
        }

        private static string SafeName(RevitLinkType type)
        {
            try { if (!string.IsNullOrEmpty(type.Name)) return TrimRvt(type.Name); } catch { }
            return "A link";
        }

        private static string NameOf(Document doc, RevitLinkInstance inst)
        {
            try
            {
                var type = doc.GetElement(inst.GetTypeId()) as RevitLinkType;
                if (type != null && !string.IsNullOrEmpty(type.Name)) return TrimRvt(type.Name);
            }
            catch { }
            return "A link";
        }

        /// <summary>
        /// Every RevitLinkInstance in the document, by id, named from its type —
        /// regardless of loaded state, unlike LinkContext.CollectFrom (which skips an
        /// unloaded link because it has no Document to report). The one thing this map
        /// exists for is naming a DELETED instance, and an instance can be deleted while
        /// unloaded just as easily as while loaded.
        /// </summary>
        private static Dictionary<long, string> BuildInstanceNames(Document doc)
        {
            var map = new Dictionary<long, string>();
            if (doc == null || !doc.IsValidObject) return map;
            try
            {
                var instances = new FilteredElementCollector(doc)
                    .OfClass(typeof(RevitLinkInstance))
                    .Cast<RevitLinkInstance>();
                foreach (var inst in instances)
                    map[IdUtil.Value(inst.Id)] = NameOf(doc, inst);
            }
            catch (Exception ex)
            {
                Log.Warn("Link-watch: building the instance name map failed", ex);
            }
            return map;
        }
    }
}

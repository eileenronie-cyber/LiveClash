using System;
using System.Collections.Generic;
using System.IO;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// A name for a kind of provisional geometry, and whether finding it should
    /// suggest dropping it.
    /// </summary>
    public class AuditLabel
    {
        public string Name = "";

        /// <summary>Arrive pre-ticked in the audit window.</summary>
        public bool DropByDefault;

        // NOTE: a BuiltIn flag used to live here, documented as "shipped with the tool;
        // cannot be deleted, but can be renamed". Seed() set it on all four labels and
        // NOTHING EVER READ IT, so the protection it described was never enforced.
        // Removed rather than left reading like a rule that holds. If built-in labels
        // should really be undeletable, that is a guard on the delete path.

        public override string ToString() { return Name; }
    }

    /// <summary>
    /// What the audit called something, and what the USER decided to call it.
    ///
    /// The detectors guess from naming, and naming is the one thing that differs on
    /// every project — "BWIC" here, "PFC" or "provision" or "builders work"
    /// elsewhere. Worse, the right label is often a judgement the tool cannot make:
    /// this project's Pre Action Valve is a placeholder AND a fire-package hold AND
    /// something the fire subcontractor owns, and only the coordinator knows which of
    /// those matters.
    ///
    /// So the detector's answer is a default, not a verdict. Rename a label, invent
    /// your own, or reassign one type to a different label, and the choice is
    /// remembered against this model and survives the next run. Stored beside the
    /// per-model settings for the same reason those are per-model: the vocabulary
    /// that fits one project silently misfits the next.
    /// </summary>
    public class AuditLabelStore
    {
        private static readonly AuditLabelStore _instance = new AuditLabelStore();
        public static AuditLabelStore Instance { get { return _instance; } }

        private readonly object _gate = new object();
        private string _modelKey;

        /// <summary>This model's identity per <see cref="ModelIdentity"/>, or "" for a standalone
        /// or detached model.</summary>
        private string _identity = "";

        // The (title, identity) pair the collision guard last resolved a file for, and what it
        // resolved to — see ClashStatusStore.LoadFor's class comment for why LoadFor must skip
        // the guard's disk I/O when neither has changed since last time.
        private string _guardTitleSeen, _guardIdentitySeen, _guardResolvedKey;

        private readonly List<AuditLabel> _labels = new List<AuditLabel>();

        /// <summary>category|family|type  ->  label name.</summary>
        private readonly Dictionary<string, string> _assigned =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        // The names the detectors produce. Renaming one of these renames it
        // everywhere, because assignments are stored by name.
        public const string Hole = "Modelled hole";
        public const string Reservation = "Reserved space";
        public const string Placeholder = "Placeholder";
        public const string NoConnector = "No connector";

        private AuditLabelStore() { Seed(); }

        private void Seed()
        {
            _labels.Clear();
            _labels.Add(new AuditLabel { Name = Hole, DropByDefault = true });
            _labels.Add(new AuditLabel { Name = Reservation, DropByDefault = true });
            // A placeholder stands in for something real. Dropping it hides the real
            // thing when it lands, so it is never pre-ticked.
            _labels.Add(new AuditLabel { Name = Placeholder, DropByDefault = false });
            _labels.Add(new AuditLabel { Name = NoConnector, DropByDefault = false });
        }

        // NOTE: a Labels property returning a copy of _labels used to sit here with no
        // callers — everything goes through LabelNames or the store's own methods.
        public List<string> LabelNames
        {
            get
            {
                lock (_gate)
                {
                    var list = new List<string>(_labels.Count);
                    foreach (var l in _labels) list.Add(l.Name);
                    return list;
                }
            }
        }

        public bool DropByDefault(string label)
        {
            if (string.IsNullOrEmpty(label)) return false;
            lock (_gate)
                foreach (var l in _labels)
                    if (string.Equals(l.Name, label, StringComparison.OrdinalIgnoreCase))
                        return l.DropByDefault;
            return false;
        }

        /// <summary>Adds a label the user typed. Existing names are left alone.</summary>
        public AuditLabel Ensure(string name, bool dropByDefault)
        {
            if (string.IsNullOrEmpty(name)) return null;
            name = name.Trim();
            if (name.Length == 0) return null;

            lock (_gate)
            {
                foreach (var l in _labels)
                    if (string.Equals(l.Name, name, StringComparison.OrdinalIgnoreCase)) return l;

                var made = new AuditLabel { Name = name, DropByDefault = dropByDefault };
                _labels.Add(made);
                return made;
            }
        }

        public static string KeyOf(BuiltInCategory cat, string family, string type)
        {
            return (int)cat + "|" + (family ?? "") + "|" + (type ?? "");
        }

        /// <summary>The user's label for this type, or null to use the detector's.</summary>
        public string AssignedTo(BuiltInCategory cat, string family, string type)
        {
            var k = KeyOf(cat, family, type);
            lock (_gate)
            {
                string v;
                return _assigned.TryGetValue(k, out v) ? v : null;
            }
        }

        public void Assign(BuiltInCategory cat, string family, string type, string label)
        {
            var k = KeyOf(cat, family, type);
            lock (_gate)
            {
                if (string.IsNullOrEmpty(label)) _assigned.Remove(k);
                else _assigned[k] = label.Trim();
            }
        }

        // ---------------------------------------------------------------- storage

        private static string Folder
        {
            get
            {
                var dir = Path.Combine(
                    LocalState.Root,   // the real local application data, or a dev job's scratch root (DM2)
                    "LiveClash", "audit");
                try { Directory.CreateDirectory(dir); } catch { }
                return dir;
            }
        }

        public string PathFor(string key) { return Path.Combine(Folder, key + ".txt"); }

        /// <summary>The model these labels belong to, as <see cref="ModelKey"/>/the collision
        /// guard names it. Null before any document has been adopted.</summary>
        public string ModelName { get { lock (_gate) return _modelKey; } }

        /// <summary>The key (ClashEngine.DocKey) of the document these were last loaded for. A Save As carries them to the new
        /// name only if this is the document being saved (LedgerProvenance, round 3b of the 2026-10-31 review).</summary>
        public string LoadedForDocKey { get; private set; }

        /// <summary>
        /// Switch to a document's own labels. Cheap to call repeatedly, on purpose — see
        /// ClashStatusStore.LoadFor's class comment.
        /// </summary>
        public void LoadFor(Document doc)
        {
            RevitUser.Note(doc);
            if (doc != null) LoadedForDocKey = ClashEngine.DocKey(doc);

            string titleKey, identity;
            string[] titleLines;
            bool titleExists;
            string key = ResolveKey(doc, out titleKey, out identity, out titleExists, out titleLines);
            if (string.IsNullOrEmpty(key)) return;
            Load(key, identity, key == titleKey && titleExists ? titleLines : null);
        }

        /// <summary>The file key a document's labels live under; see ClashSettings.ResolveKey.</summary>
        private string ResolveKey(Document doc, out string titleKey, out string identity, out bool titleExists, out string[] titleLines)
        {
            titleKey = null;
            identity = null;
            titleExists = false;
            titleLines = null;
            try { if (doc != null && doc.IsValidObject) titleKey = ModelKey.FileSafe(doc); }
            catch (Exception ex) { Log.Warn("Audit labels: could not derive a model key", ex); }
            if (string.IsNullOrEmpty(titleKey)) return null;

            identity = ModelIdentity.Compute(doc);
            string key;

            if (titleKey == _guardTitleSeen && identity == _guardIdentitySeen)
            {
                key = _guardResolvedKey;
            }
            else
            {
                key = titleKey;

                // The collision guard: see ModelIdentity's header comment (and its limits).
                if (!string.IsNullOrEmpty(identity))
                {
                    var titlePath = PathFor(titleKey);
                    titleExists = File.Exists(titlePath);
                    if (titleExists) titleLines = ModelIdentity.ReadLines(titlePath);   // retried — see its own comment
                    string recorded = titleLines == null ? "" : ModelIdentity.RecordedIn(titleLines);

                    string claim;
                    key = ModelIdentity.Resolve(titleKey, titleExists, recorded, identity, out claim);
                    if (claim != null) ModelIdentity.Claim(titlePath, claim);
                }

                _guardTitleSeen = titleKey;
                _guardIdentitySeen = identity;
                _guardResolvedKey = key;
            }
            Log.NoteSensitive(titleKey);
            Log.NoteSensitive(key);
            return key;
        }

        /// <summary>
        /// The file's rules: a read that fails is not "no file", the defaults are never saved over a
        /// file that could not be read, and writes are atomic (review 2026-10-31, M9). See RewrittenFile.
        /// </summary>
        private RewrittenFile _file = new RewrittenFile("Audit labels");

        /// <summary>
        /// Point at <paramref name="key"/>'s file and read it. <paramref name="preRead"/> is that file's
        /// lines when the collision guard already read them (the common, no-collision case), so the
        /// same bytes are not read twice, the way ClashStatusStore/ResolvedLog already do.
        /// </summary>
        private void Load(string key, string identity, string[] preRead)
        {
            var path = PathFor(key);
            string[] lines;
            bool retry;
            lock (_gate)
            {
                if (key == _modelKey && !_file.NeedsOpen(path)) return;
                retry = key == _modelKey;
                _modelKey = key;
                _identity = identity ?? "";
                lines = _file.Open(path, preRead);
                // A retry that fails again keeps what is held; anything else starts from the seed.
                if (!retry || lines != null)
                {
                    _assigned.Clear();
                    Seed();
                }
            }

            if (lines == null)
            {
                if (_file.Unreadable)
                {
                    if (!retry) Log.Warn("Audit labels: could not read this model's file; using the defaults, NOT saving over it, retried on the next load.");
                }
                else Log.Info("Audit labels: no file yet for '" + key + "'; using the defaults.");
                return;
            }


            int labels = 0, assigns = 0;
            try
            {
                foreach (var raw in lines)
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] == '#') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    var k = line.Substring(0, eq);
                    var v = line.Substring(eq + 1);
                    var p = v.Split('\t');

                    if (k == "label" && p.Length >= 2)
                    {
                        bool drop;
                        _ = bool.TryParse(p[1], out drop);   // unreadable means not dropped
                        lock (_gate)
                        {
                            bool found = false;
                            foreach (var l in _labels)
                                if (string.Equals(l.Name, p[0], StringComparison.OrdinalIgnoreCase))
                                { l.DropByDefault = drop; found = true; break; }
                            if (!found) _labels.Add(new AuditLabel { Name = p[0], DropByDefault = drop });
                        }
                        labels++;
                    }
                    else if (k == "assign" && p.Length >= 4)
                    {
                        int cid;
                        if (!int.TryParse(p[0], out cid)) continue;
                        lock (_gate) _assigned[KeyOf((BuiltInCategory)cid, p[1], p[2])] = p[3];
                        assigns++;
                    }
                }
                Log.Info("Audit labels: " + labels + " label(s) and " + assigns
                         + " assignment(s) for '" + key + "'.");
            }
            catch (Exception ex)
            {
                Log.Warn("Audit labels: could not parse the labels for '" + key + "'", ex);
            }
        }

        public void Save()
        {
            try
            {
                // Built and written under ONE lock: a LoadFor for a DIFFERENT model landing in between
                // could otherwise pair this model's key with the NEXT model's (freshly re-seeded) labels
                // and identity. Atomic, and refused while this model's file could not be read
                // (RewrittenFile; review 2026-10-31, M9).
                lock (_gate)
                {
                    if (string.IsNullOrEmpty(_modelKey)) return;
                    if (_file.Path == PathFor(_modelKey)) _file.Save(LinesLocked(_identity));
                }
            }
            catch (Exception ex)
            {
                Log.Warn("Audit labels: could not write the file", ex);
            }
        }

        /// <summary>
        /// The active model was saved under a new name (Save As): its labels go with it, as the
        /// settings do (ClashSettings.FollowSaveAs; review 2026-10-31, M4). Falls back to an ordinary
        /// load of the new name when they cannot be carried.
        /// </summary>
        [RevitBound]
        public void FollowSaveAs(Document doc)
        {
            if (doc != null) LoadedForDocKey = ClashEngine.DocKey(doc);
            string titleKey, identity;
            string[] titleLines;
            bool titleExists;
            string key = ResolveKey(doc, out titleKey, out identity, out titleExists, out titleLines);
            if (string.IsNullOrEmpty(key)) return;
            lock (_gate)
            {
                if (key == _modelKey) return;
                if (!string.IsNullOrEmpty(_modelKey))
                {
                    // SP7 (round 8): a file that could not be read is copied as it is (and the user told if even that fails), not abandoned.
                    bool carried = _file.Unreadable
                        ? SaveAsCarry.CarryUnreadable(_file, PathFor(key), identity, "audit labels", ClashSettings.SayToPanel)
                        : SaveAsCarry.CarryParsed(_file, PathFor(key), LinesLocked(identity), "audit labels", ClashSettings.SayToPanel);
                    if (carried)
                    {
                        _modelKey = key;
                        _identity = identity ?? "";
                        Log.Info("Audit labels: carried to the model's new name after Save As.");
                        return;
                    }
                }
            }
            Load(key, identity, key == titleKey && titleExists ? titleLines : null);
        }

        /// <summary>The file's lines. Call under _gate.</summary>
        private List<string> LinesLocked(string identity)
        {
            var lines = new List<string>();
            lines.Add("# LiveClash audit labels — tab separated");
            foreach (var l in _labels)
                lines.Add("label=" + Clean(l.Name) + "\t" + l.DropByDefault);

            foreach (var kv in _assigned)
            {
                // key is catId|family|type; store the parts separately so a
                // family name containing a pipe cannot corrupt the line.
                var parts = kv.Key.Split(new[] { '|' }, 3);
                if (parts.Length < 3) continue;
                lines.Add("assign=" + parts[0] + "\t" + Clean(parts[1]) + "\t"
                          + Clean(parts[2]) + "\t" + Clean(kv.Value));
            }
            if (!string.IsNullOrEmpty(identity)) lines.Add(ModelIdentity.Line(identity));
            return lines;
        }

        /// <summary>Tabs are the separator, so they cannot appear in a value.</summary>
        private static string Clean(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
        }
    }
}

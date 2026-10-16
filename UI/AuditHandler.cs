using System;
using System.Collections.Generic;
using System.Diagnostics;
using Autodesk.Revit.UI;
using LiveClash.Core;

namespace LiveClash.UI
{
    /// <summary>
    /// Runs the provisional-geometry audit and applies whatever the user ticks.
    ///
    /// Both halves need Revit API context: the scan reads every element in the
    /// document, and applying the result rebuilds the updater's triggers. A WPF
    /// click handler is not API context, so this is an ExternalEvent.
    /// </summary>
    public class AuditHandler : IExternalEventHandler
    {
        public ClashPanel Panel { get; set; }

        public void Execute(UIApplication app)
        {
            try
            {
                var uidoc = app.ActiveUIDocument;
                var doc = uidoc != null ? uidoc.Document : null;
                if (doc == null || !doc.IsValidObject)
                {
                    if (Panel != null) Panel.SetStatus("Audit needs an open model.");
                    return;
                }

                AuditLabelStore.Instance.LoadFor(doc);

                var sw = Stopwatch.StartNew();
                var findings = ProvisionalAudit.Run(doc);
                sw.Stop();

                int elements = 0;
                foreach (var f in findings) elements += f.InstanceCount;
                Log.Info("Provisional audit: " + findings.Count + " finding(s) covering "
                         + elements + " element(s) in " + sw.ElapsedMilliseconds + " ms.");

                if (findings.Count == 0)
                {
                    if (Panel != null)
                        Panel.SetStatus("Audit found nothing provisional — no modelled openings, "
                            + "reserved space, placeholders or connectorless plant. ("
                            + sw.ElapsedMilliseconds + " ms)");
                    return;
                }

                // Hand the window the live Drop list so a tick reflects what is
                // actually excluded rather than what the label would default to. Drops
                // are GLOBAL now (ClashSettings.AuditDrops), applied to every set by the
                // engine, so this is the one list to show and edit.
                var win = new AuditWindow(findings,
                    ClashSettings.Current.TargetFilter.ParseExcludeKeywords(),
                    new List<TypeKey>(ClashSettings.Current.AuditDrops));
                new System.Windows.Interop.WindowInteropHelper(win).Owner =
                    Process.GetCurrentProcess().MainWindowHandle;

                if (win.ShowDialog() != true || (win.Dropped.Count == 0 && win.Undropped.Count == 0))
                {
                    if (Panel != null)
                        Panel.SetStatus("Audit: " + findings.Count + " finding(s), no change.");
                    return;
                }

                Apply(win.Dropped, win.Undropped);
            }
            catch (System.Exception ex)
            {
                Log.Error("Provisional audit failed", ex);
                if (Panel != null) Panel.SetStatus("Audit failed — see " + Log.Path);
            }
        }

        /// <summary>
        /// Drop the ticked family+type pairs GLOBALLY — one list on ClashSettings that
        /// the engine applies to every set, on both the source and target side.
        ///
        /// This used to write the active set's two filters. That was two half-traps at
        /// once. Writing only the target filter left a dropped family a SOURCE, so it
        /// went on finding the same pairs from its own side (mirroring onto the source
        /// side took one model from 2,785 removed clashes to 4,201). And writing the
        /// ACTIVE set only meant any OTHER enabled set kept re-reporting the exact pair,
        /// because results merge and de-dup — a leak the moment two sets overlap.
        ///
        /// A global list closes both: a family+type that is never a real obstruction is
        /// noise on every side of every set, which is precisely what the audit asserts.
        /// "Drop this" means stop reporting it, everywhere. Anything less is a trap.
        /// </summary>
        private void Apply(List<TypeKey> names, List<TypeKey> remove)
        {
            var s = ClashSettings.Current;

            int added = ApplyTo(s.AuditDrops, names, remove);

            s.Save();
            ClashEngine.InvalidateAll();
            ClashUpdater.AddTriggers();
            ClashResultStore.Instance.Clear();
            if (Panel != null) Panel.SetEmptyReason(ClashPanel.EmptyReason.DropsChanged);

            int removed = remove == null ? 0 : remove.Count;
            Log.Info("Audit: +" + added + " / -" + removed + " family+type(s), global; "
                     + s.AuditDrops.Count + " pair(s) now dropped across every set.");

            if (Panel != null)
                Panel.SetStatus("Audit drop list updated (+" + added + " / -" + removed
                    + "). " + s.AuditDrops.Count + " family+type pair(s) dropped in every set, "
                    + "both sides. Run a full check to re-evaluate.");
        }

        /// <summary>
        /// Rewrite one filter's Drop line. Rebuilt from its parsed entries rather than
        /// appended to as a string, because removal needs the list form — and
        /// ParseExcludeKeywords is what the engine itself reads, so round-tripping
        /// through it keeps the UI and the engine agreeing.
        /// </summary>
        /// <returns>How many names were newly added.</returns>
        /// <summary>
        /// Fold the window's ticks into a filter's exact drop pairs.
        ///
        /// This used to write family NAMES into ExcludeKeyword, which matched by
        /// SUBSTRING: one tick on a Pre Action Valve dropped 106 elements -- two 425 m3
        /// fire water tanks, three pumps, and 62 cabinets from the unrelated family
        /// ABC_MEC_Generic_FireHoseReelCabinet among them. Pairs are compared whole, so
        /// a tick now drops exactly the row that was ticked.
        ///
        /// No Family Name ParamRef is needed any more either: the pair test reads the
        /// element's own type rather than searching nominated parameters.
        /// </summary>
        private static int ApplyTo(List<TypeKey> target, List<TypeKey> names, List<TypeKey> remove)
        {
            if (target == null) return 0;

            var dropSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var r in remove ?? new List<TypeKey>()) dropSet.Add(KeyOf(r));

            var keep = new List<TypeKey>();
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var k in target)
            {
                var id = KeyOf(k);
                if (dropSet.Contains(id)) continue;      // asked to stop dropping this
                if (!existing.Add(id)) continue;
                keep.Add(k);
            }

            int added = 0;
            foreach (var n in names ?? new List<TypeKey>())
            {
                // Type is what identifies a finding; Family legitimately comes back empty
                // for system-family categories with no family grouping (pipes, ducts,
                // conduits, cable trays) -- rejecting those here used to silently drop the
                // tick while Apply() still reported success and cleared every result.
                // Found reviewing the whole codebase, 2026-09-27.
                if (n == null || string.IsNullOrEmpty(n.Type)) continue;
                if (!existing.Add(KeyOf(n))) continue;

                // Copy so the stored list never shares a mutable object with the window.
                keep.Add(new TypeKey { Family = n.Family, Type = n.Type });
                added++;
            }

            target.Clear();
            target.AddRange(keep);
            return added;
        }

        /// <summary>Identity of a pair, on a separator no family or type name can contain.</summary>
        private static string KeyOf(TypeKey k)
        {
            return k == null ? "" : (k.Family ?? "") + (char)31 + (k.Type ?? "");
        }

        public string GetName() { return "LiveClash.Audit"; }
    }
}

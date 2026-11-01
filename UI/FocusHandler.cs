using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LiveClash.Core;

namespace LiveClash.UI
{
    /// <summary>
    /// Frames a clash in 3D with a SECTION BOX, so a pair you cannot Selection-Box —
    /// both sides in links, or a link element that BX would leave out — can still be
    /// viewed. A section box is a view property, not a selection, so it works whatever
    /// can be picked, and link geometry renders in the host 3D view so both sides show.
    ///
    /// It uses a dedicated "LiveClash — Clash Focus" 3D view rather than the one you are
    /// working in, so your model view's section box is never clobbered. Setting the box
    /// (and creating the view) is a model change, so this runs in a transaction — the
    /// panel click arrives outside API context, so it is marshalled here by an
    /// ExternalEvent, exactly like selection.
    /// </summary>
    public class FocusHandler : IExternalEventHandler
    {
        public ClashPanel Panel { get; set; }
        public List<ClashResult> Targets { get; set; }

        private const string ViewName = FocusOutcome.ViewName;

        public void Execute(UIApplication app)
        {
            // The document whose Focus transaction is open or rolling back; null once committed.
            Document committing = null;
            bool committed = false;
            string framedName = null;      // the view's actual name: per user in a workshared model
            try
            {
                var uidoc = app.ActiveUIDocument;
                var doc = HandlerGuard.Pick(uidoc);
                if (doc == null || !doc.IsValidObject) return;
                if (HandlerGuard.Refuses(doc, Panel, "frame the clash")) return;

                var targets = Targets;
                if (targets == null || targets.Count == 0) return;

                var box = ClashFocus.BuildFocusBox(doc, targets);
                if (box == null)
                {
                    if (Panel != null) Panel.SetStatus("Could not frame the clash — no readable geometry on either side.");
                    return;
                }

                View3D view = null;
                ElementId editedId = null;      // the view this click edits, kept so a refusal can be pinned on it
                var failures = new FocusFailures();
                TransactionStatus status;
                committing = doc;
                using (var t = new Transaction(doc, "LiveClash: focus clash"))
                {
                    // E10: a Revit error raised by this change (a focus view another user has borrowed,
                    // say) comes back as a panel message and rolls back. Without a preprocessor it went
                    // to Revit's modal failure dialog, and the commit's status was never read, so the
                    // panel said "Framed" whatever happened.
                    var options = t.GetFailureHandlingOptions();
                    options.SetFailuresPreprocessor(failures);
                    options.SetClearAfterRollback(true);
                    t.SetFailureHandlingOptions(options);

                    t.Start();
                    view = EnsureFocusView(doc);
                    if (view != null) editedId = view.Id;
                    if (view != null)
                    {
                        view.IsSectionBoxActive = true;
                        view.SetSectionBox(box);
                    }
                    status = t.Commit();
                }

                if (status != TransactionStatus.Committed)
                {
                    ForgetIfRolledBack(doc);
                    bool passedOver = NoteRefused(doc, editedId);
                    Log.Warn("Focus clash rolled back (" + status + "): "
                             + (failures.Errors.Count == 0 ? "no error text" : string.Join(" | ", failures.Errors.ToArray())));
                    if (Panel != null)
                        Panel.SetStatus(FocusOutcome.Describe(Refusal(failures.Errors, passedOver)));
                    return;
                }
                committing = null;
                committed = true;
                if (editedId != null) { try { FocusRefusals.NoteSuccess(ClashEngine.DocKey(doc), IdUtil.Value(editedId)); } catch { } }   // a success forgives one earlier refusal
                framedName = view == null ? null : SafeName(view);
                if (framedName == "?") framedName = null;   // unreadable: the message falls back to the plain name

                if (view == null)
                {
                    if (Panel != null) Panel.SetStatus("Could not create the focus view — no 3D view type available.");
                    return;
                }

                // Activating a view is not a model change, so it happens after the commit. Revit does not allow it
                // from every context (the active view cannot change during some events), and the setter's exception
                // used to be swallowed, so the message said "Framed" to a user who was still in their own view.
                // Read the active view back, tell the user when it did not take, and log why.
                Exception switchError = null;
                try { uidoc.ActiveView = view; } catch (Exception ex) { switchError = ex; }
                Exception readError;
                var activeState = ActiveStateOf(uidoc, view, out readError);
                bool notSwitched = activeState == FocusOutcome.ActiveState.Other;
                bool activeUnknown = activeState == FocusOutcome.ActiveState.Unknown;   // DL-h: not read, so not claimed
                if (notSwitched)
                    Log.Warn(FocusOutcome.SwitchLog(switchError == null ? null : switchError.GetType().Name,
                                                    switchError == null ? null : switchError.Message), switchError);
                else if (activeUnknown)
                    Log.Warn(FocusOutcome.UnreadableLog(readError == null ? null : readError.GetType().Name,
                                                        readError == null ? null : readError.Message), readError);

                bool selected;
                bool anyLink = SelectTargets(uidoc, doc, targets, out selected);
                ZoomToBox(uidoc, view, box);

                if (Panel != null)
                    Panel.SetStatus(FocusOutcome.Describe(new FocusOutcome.Result { Committed = true, AnyLink = anyLink, Selected = selected, ViewName = framedName, NotSwitched = notSwitched, ActiveUnknown = activeUnknown }));
            }
            catch (Exception ex)
            {
                // A throw inside the transaction: disposing it rolled everything back. One after the
                // commit (none is expected) leaves the box set, and the message must not say otherwise.
                if (committing != null) ForgetIfRolledBack(committing);
                Log.Warn("Focus clash failed", ex);
                if (Panel != null)
                    Panel.SetStatus(FocusOutcome.Describe(new FocusOutcome.Result
                    {
                        Failure = ex.Message, LogPath = Log.Path, Committed = committed, ViewName = framedName
                    }));
            }
        }

        /// <summary>
        /// Turns every Revit error raised by Focus's transaction into text for the panel, and rolls the
        /// transaction back without Revit's dialog. Warnings are dismissed: framing a view raises none a
        /// person must act on, and a warning box over the focus view would only hide it.
        /// </summary>
        private sealed class FocusFailures : IFailuresPreprocessor
        {
            public readonly List<string> Errors = new List<string>();

            public FailureProcessingResult PreprocessFailures(FailuresAccessor accessor)
            {
                bool error = false;
                foreach (var m in accessor.GetFailureMessages())
                {
                    if (m.GetSeverity() == FailureSeverity.Warning) { accessor.DeleteWarning(m); continue; }
                    error = true;
                    string text = null;
                    try { text = m.GetDescriptionText(); } catch { }
                    if (!string.IsNullOrEmpty(text) && !Errors.Contains(text)) Errors.Add(text);
                }
                if (!error) return FailureProcessingResult.Continue;

                var options = accessor.GetFailureHandlingOptions();
                options.SetClearAfterRollback(true);
                accessor.SetFailureHandlingOptions(options);
                return FailureProcessingResult.ProceedWithRollBack;
            }
        }

        /// <summary>
        /// Is <paramref name="view"/> the active view now? Three answers (DL-h): yes, no, and "could not read it". The last
        /// used to count as yes, so an unreadable active view produced a claim the code had not checked. The exception
        /// the read threw comes back in <paramref name="readError"/>, for the log.
        /// </summary>
        private static FocusOutcome.ActiveState ActiveStateOf(UIDocument uidoc, View3D view, out Exception readError)
        {
            Exception err = null;
            long focusId = IdUtil.Value(view.Id);
            var state = FocusOutcome.Classify(() =>
            {
                try
                {
                    var active = uidoc.ActiveView;
                    return active == null ? (long?)null : IdUtil.Value(active.Id);
                }
                catch (Exception ex) { err = ex; throw; }
            }, focusId);
            readError = err;
            return state;
        }

        private static FocusOutcome.Result Refusal(List<string> errors, bool passedOver)
        {
            var r = new FocusOutcome.Result { Committed = false, PassedOver = passedOver };
            r.Refused.AddRange(errors);
            return r;
        }

        /// <summary>
        /// After a rollback: if the view the click edited still exists, it was there before the click (one made inside the
        /// rolled-back transaction is gone) and Revit MAY not let this user edit it - not borrowed, but not editable (the central moved on,
        /// say). One refusal may be a one-off error, and passing the view over at once broke the E10 check (`focus: ...the box moved to the new
        /// clash`): a refusal counts, and the view is passed over only on the SECOND in a row (FocusRefusals.NoteFailure), after which the next
        /// click makes another view instead of rolling back for ever. Returns true when this refusal passed the view over, so the message says so.
        /// The real trigger needs a second user on a workshared central and is not reproduced here; the symptom is (R8UiChecks).
        /// </summary>
        private static bool NoteRefused(Document doc, ElementId editedId)
        {
            try
            {
                if (editedId == null || doc == null || !doc.IsValidObject) return false;
                var v = doc.GetElement(editedId) as View3D;
                if (v == null || !v.IsValidObject) return false;
                bool pinned = FocusRefusals.NoteFailure(ClashEngine.DocKey(doc), IdUtil.Value(editedId));
                Log.Info(pinned ? "Focus: Revit refused to edit an existing focus view twice in a row; the next click will make another instead of reusing it."
                                : "Focus: Revit refused to edit an existing focus view; if the next click is refused too, Focus will make another.");
                return pinned;
            }
            catch { return false; }
        }

        /// <summary>
        /// After a rollback, drop a remembered view id that no longer exists. A view made inside the
        /// rolled-back transaction was remembered before the commit; left there, its id could later be
        /// handed to another view, and Focus would then take that one over.
        /// </summary>
        private static void ForgetIfRolledBack(Document doc)
        {
            try
            {
                var docKey = ClashEngine.DocKey(doc);
                ElementId known;
                if (!_focusViews.TryGetValue(docKey, out known)) return;
                var v = doc.IsValidObject ? doc.GetElement(known) as View3D : null;
                if (v == null || !v.IsValidObject) _focusViews.Remove(docKey);
            }
            catch { }
        }

        /// <summary>
        /// The dedicated focus view, created once and reused.
        ///
        /// IDENTIFYING IT BY NAME ALONE LEAKS VIEWS. The rename can fail — the name may
        /// already belong to another view or a template — and swallowing that left a view
        /// called "3D View 1", which the next lookup could not find, so every Focus click
        /// created ANOTHER permanent view inside a transaction. Repeatedly double-clicking
        /// clashes would quietly fill the model with them.
        ///
        /// So the id of the view we made is remembered for the session and checked first;
        /// the name lookup is only the fallback that finds it again in a later session. If
        /// the rename fails the view is still usable and still tracked — the name is a
        /// convenience, not the identity.
        /// </summary>
        private static readonly Dictionary<string, ElementId> _focusViews =
            new Dictionary<string, ElementId>();

        /// <summary>
        /// Forget a document's focus view. Called from OnDocumentClosing, so an entry
        /// cannot outlive the document whose ElementId it holds.
        ///
        /// Nothing misbehaved without this — EnsureFocusView revalidates the id against
        /// the document before trusting it, so a stale entry is dropped on first use.
        /// But every other id-keyed cache in the tool is dropped on close (see
        /// ClashEngine.InvalidateAll), and this was the one that was not; a cache with no
        /// lifetime is how the next id-keyed thing gets added without one either.
        /// </summary>
        public static void Forget(Document doc)
        {
            if (doc == null) return;
            try { var key = ClashEngine.DocKey(doc); _focusViews.Remove(key); FocusRefusals.Forget(key); }
            catch { }
        }

        private static View3D EnsureFocusView(Document doc)
        {
            var docKey = ClashEngine.DocKey(doc);

            // 1. The one we made earlier this session, if it is still alive and still ours to edit.
            ElementId known;
            if (_focusViews.TryGetValue(docKey, out known))
            {
                try
                {
                    var v = doc.GetElement(known) as View3D;
                    if (v != null && v.IsValidObject && !v.IsTemplate && !OwnedByOther(doc, v) && !FocusRefusals.IsRefused(docKey, IdUtil.Value(v.Id))) return v;
                }
                catch { }
                _focusViews.Remove(docKey);      // deleted by the user, stale, borrowed by someone else, or one Revit refused to edit
            }

            // 2. One left by an earlier session, found by name: this user's own first, then the shared
            //    name. One another user has borrowed cannot take a section box (Revit refuses the
            //    commit), so it is passed over and this user gets their own.
            string mine = NameFor(doc);
            View3D shared = null;
            foreach (var v in new FilteredElementCollector(doc).OfClass(typeof(View3D)).Cast<View3D>())
            {
                if (v == null || v.IsTemplate) continue;
                if (string.Equals(v.Name, mine, StringComparison.Ordinal) && !OwnedByOther(doc, v) && !FocusRefusals.IsRefused(docKey, IdUtil.Value(v.Id)))
                {
                    _focusViews[docKey] = v.Id;
                    return v;
                }
                if (shared == null && string.Equals(v.Name, ViewName, StringComparison.Ordinal) && !OwnedByOther(doc, v) && !FocusRefusals.IsRefused(docKey, IdUtil.Value(v.Id)))
                    shared = v;
            }
            if (shared != null)
            {
                _focusViews[docKey] = shared.Id;
                return shared;
            }

            // 3. Make one.
            var vft = new FilteredElementCollector(doc)
                .OfClass(typeof(ViewFamilyType)).Cast<ViewFamilyType>()
                .FirstOrDefault(x => x.ViewFamily == ViewFamily.ThreeDimensional);
            if (vft == null) return null;

            var made = View3D.CreateIsometric(doc, vft.Id);
            if (made == null) return null;

            // Remember it BEFORE naming: if the rename throws, the view still exists and
            // must be reused rather than abandoned and re-created on the next click.
            _focusViews[docKey] = made.Id;
            try { made.Name = mine; }
            catch (Exception ex)
            {
                Log.Info("Focus view kept Revit's default name ('" + SafeName(made)
                         + "') — the name was rejected, but the view is tracked and reused. " + ex.Message);
            }
            return made;
        }

        /// <summary>Characters Revit refuses in a view name.</summary>
        private static readonly char[] NotInViewNames = @"\:{}[]|;<>?`~".ToCharArray();

        /// <summary>
        /// The focus view's name. In a workshared model every user shares the model's views, and
        /// one user's focus view stays borrowed by them until they relinquish it, so each user
        /// gets their own: "LiveClash — Clash Focus (name)". Otherwise the plain name.
        /// </summary>
        private static string NameFor(Document doc)
        {
            string user = null;
            try { if (doc.IsWorkshared) user = doc.Application.Username; } catch { }
            if (string.IsNullOrWhiteSpace(user)) return ViewName;

            var clean = new StringBuilder();
            foreach (var ch in user.Trim()) clean.Append(Array.IndexOf(NotInViewNames, ch) >= 0 ? '_' : ch);
            return ViewName + " (" + clean + ")";
        }

        /// <summary>Borrowed by another user, so this one cannot edit it. False when unknown.</summary>
        private static bool OwnedByOther(Document doc, Element e)
        {
            try
            {
                return doc.IsWorkshared
                    && WorksharingUtils.GetCheckoutStatus(doc, e.Id) == CheckoutStatus.OwnedByOtherUser;
            }
            catch { return false; }
        }

        private static string SafeName(View3D v)
        {
            try { return v.Name; } catch { return "?"; }
        }

        /// <summary>
        /// Select both sides so highlight and native Selection Box also cover the clash.
        /// Host elements select directly; a linked element selects through a link
        /// reference (which is what makes it pickable from the host at all). Falls back to
        /// element-id selection of the host side if references cannot be built.
        ///
        /// <paramref name="selected"/> is true only when every side of every clash was
        /// selected, so the panel never says "both sides are selected" about an element
        /// that is gone or sits in a link that is not loaded.
        /// </summary>
        private static bool SelectTargets(UIDocument uidoc, Document doc, List<ClashResult> targets, out bool selected)
        {
            var refs = new List<Reference>();
            var hostIds = new List<ElementId>();
            var seen = new HashSet<string>();
            bool anyLink = false;
            bool everySide = true;

            foreach (var r in targets)
            {
                if (r == null) continue;
                if (!AddHostRef(doc, r.HostElementId, refs, hostIds, seen)) everySide = false;

                if (r.LinkInstanceId != null)
                {
                    if (AddLinkRef(doc, r.LinkInstanceId, r.OtherElementId, refs, seen)) anyLink = true;
                    else everySide = false;
                }
                else
                {
                    if (!AddHostRef(doc, r.OtherElementId, refs, hostIds, seen)) everySide = false;
                }
            }

            selected = false;
            try
            {
                if (refs.Count > 0)
                {
                    uidoc.Selection.SetReferences(refs);
                    selected = everySide;
                    return anyLink;
                }
            }
            catch { }
            try
            {
                if (hostIds.Count > 0)
                {
                    uidoc.Selection.SetElementIds(hostIds);
                    selected = everySide && !anyLink;   // a linked side has no element id here
                }
            }
            catch { }
            return anyLink;
        }

        /// <returns>True when the element exists in this model (added now, or already).</returns>
        private static bool AddHostRef(Document doc, ElementId id, List<Reference> refs,
                                       List<ElementId> ids, HashSet<string> seen)
        {
            try
            {
                if (id == null || id == ElementId.InvalidElementId) return false;
                var el = doc.GetElement(id);
                if (el == null || !el.IsValidObject) return false;
                if (!seen.Add("h" + IdUtil.Value(id))) return true;
                ids.Add(id);
                var rf = new Reference(el);
                if (rf != null) refs.Add(rf);
                return true;
            }
            catch { return false; }
        }

        private static bool AddLinkRef(Document doc, ElementId linkInstanceId, ElementId linkedId,
                                       List<Reference> refs, HashSet<string> seen)
        {
            try
            {
                var link = doc.GetElement(linkInstanceId) as RevitLinkInstance;
                if (link == null) return false;
                var linkDoc = link.GetLinkDocument();
                if (linkDoc == null || linkedId == null || linkedId == ElementId.InvalidElementId) return false;
                var el = linkDoc.GetElement(linkedId);
                if (el == null || !el.IsValidObject) return false;
                if (!seen.Add("l" + IdUtil.Value(linkInstanceId) + ":" + IdUtil.Value(linkedId))) return true;

                var inLink = new Reference(el);
                if (inLink == null) return false;
                var inHost = inLink.CreateLinkReference(link);
                if (inHost != null) { refs.Add(inHost); return true; }
            }
            catch { }
            return false;
        }

        /// <summary>Frame the box in the newly activated view.</summary>
        private static void ZoomToBox(UIDocument uidoc, View3D view, BoundingBoxXYZ box)
        {
            try
            {
                foreach (var uv in uidoc.GetOpenUIViews())
                    if (uv.ViewId == view.Id)
                    {
                        uv.ZoomAndCenterRectangle(box.Min, box.Max);
                        return;
                    }
            }
            catch { }
        }

        public string GetName() { return "LiveClash.Focus"; }
    }
}

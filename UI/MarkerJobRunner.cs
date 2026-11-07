using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using LiveClash.Core;

namespace LiveClash.UI
{
    /// <summary>
    /// Runs the clash schedule's Revit work on the Revit thread (spec 6.2, 6.7) for the panel's Task-returning actions
    /// (<see cref="IClashScheduleActions"/>): each job is queued, an ExternalEvent is raised, and the job runs in API context behind the
    /// same guards as every other handler: <see cref="HandlerGuard.Refuses"/> (the session's document) and
    /// <see cref="HandlerGuard.SettingsReloaded"/>. Its Task is completed through a <see cref="TaskCompletionSource{T}"/> created with
    /// RunContinuationsAsynchronously, so a continuation never runs inside the ExternalEvent. A refusal or a failure is a RESULT, never an
    /// exception: the Task never faults. Never used from the updater or Idling (spec 6b.1); an event (a document change, a synchronize) may only RAISE
    /// the Reload job, which still runs here in Execute, in API context (delta review D-I3).
    /// </summary>
    public sealed class MarkerJobRunner : IExternalEventHandler
    {
        public ClashPanel Panel { get; set; }

        private readonly Queue<Action<UIApplication>> _jobs = new Queue<Action<UIApplication>>();
        private readonly object _gate = new object();
        private ExternalEvent _event;

        /// <summary>Makes the handler and its event. Call in API context (start-up), as the panel does for its other handlers.</summary>
        public static MarkerJobRunner Create(ClashPanel panel)
        {
            var handler = new MarkerJobRunner { Panel = panel };
            handler._event = ExternalEvent.Create(handler);
            return handler;
        }

        /// <summary>
        /// Queues <paramref name="work"/> to run on the document the handler acts on. <paramref name="action"/> completes "Open the
        /// project to ..." in the refusal text; <paramref name="refused"/> makes the result for a refusal or a failure from its text.
        /// </summary>
        public Task<T> Run<T>(string action, Func<Document, T> work, Func<string, T> refused)
        {
            var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _jobs.Enqueue(app => Complete(source, app, action, work, refused));
            var ev = _event;
            if (ev != null)
            {
                try { ev.Raise(); }
                catch (Exception ex) { Log.Warn("Clash schedule event could not be raised", ex); }
            }
            return source.Task;
        }

        /// <summary>Like <see cref="Run{T}"/>, for the actions that change the active view: the work gets the active <see cref="UIDocument"/> (null when it is not the document the job acts on).</summary>
        public Task<T> RunUi<T>(string action, Func<UIDocument, T> work, Func<string, T> refused)
        {
            var source = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
                _jobs.Enqueue(app => Complete(source, app, action,
                    doc => work(app.ActiveUIDocument != null && ReferenceEquals(app.ActiveUIDocument.Document, doc) ? app.ActiveUIDocument : null), refused));
            var ev = _event;
            if (ev != null)
            {
                try { ev.Raise(); }
                catch (Exception ex) { Log.Warn("Clash schedule event could not be raised", ex); }
            }
            return source.Task;
        }

        private void Complete<T>(TaskCompletionSource<T> source, UIApplication app, string action, Func<Document, T> work, Func<string, T> refused)
        {
            T answer;
            try
            {
                var doc = HandlerGuard.Pick(app == null ? null : app.ActiveUIDocument);
                var off = MarkerRefusals.Check(new MarkerEnv { SwitchedOff = !MarkerSwitch.IsOn });
                if (off != null) answer = refused(off);
                else if (doc == null || !doc.IsValidObject) answer = refused("Open a project first. Nothing was changed.");
                else if (HandlerGuard.Refuses(doc, Panel, action)) answer = refused(SessionGuard.Refusal(action));
                else if (HandlerGuard.SettingsReloaded()) answer = refused("The settings were read again. Press the button again. Nothing was changed.");
                else answer = work(doc);
            }
            catch (Exception ex)
            {
                Log.Warn("Clash schedule job failed", ex);
                answer = refused(ex.Message + " Nothing was changed.");
            }
            source.TrySetResult(answer);
        }

        public void Execute(UIApplication app)
        {
            while (true)
            {
                Action<UIApplication> job;
                lock (_gate)
                {
                    if (_jobs.Count == 0) return;
                    job = _jobs.Dequeue();
                }
                try { job(app); }
                catch (Exception ex) { Log.Warn("Clash schedule job threw", ex); }
            }
        }

        public string GetName()
        {
            return "LiveClash clash schedule";
        }
    }
}

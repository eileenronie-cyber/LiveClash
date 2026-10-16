using System;
using System.Diagnostics;
using System.Threading;
using System.Windows.Interop;
using System.Windows.Threading;
using LiveClash.Core;

namespace LiveClash.UI
{
    /// <summary>
    /// Shows <see cref="FullCheckProgressWindow"/> while a full check runs - on a thread of its own.
    ///
    /// WHY A THREAD. A full check runs on Revit's UI thread and blocks it for the whole sweep; the class comment
    /// on FullCheckHandler records that SetStatus marshals through the dispatcher on that very thread, so
    /// nothing posted to it can repaint until the sweep is over. A window on its own thread with its own
    /// message loop keeps painting while Revit's thread is busy - the way a splash screen does.
    ///
    /// WHY NOT PUMP MESSAGES FROM INSIDE THE SWEEP. Running a nested message loop on Revit's thread would
    /// let a click in Revit's own UI run in the MIDDLE of a sweep. Nothing on Revit's thread is touched here.
    ///
    /// NEVER A RISK TO THE SWEEP. Starting it never throws (a failure gives an inert object whose methods do
    /// nothing), an exception on the window's thread is logged and swallowed instead of taking Revit down with
    /// it, the thread is a background one, and Close always ends it.
    /// </summary>
    internal sealed class FullCheckProgress
    {
        private Thread _thread;
        private Dispatcher _dispatcher;
        private FullCheckProgressWindow _window;
        private volatile bool _dead;
        // The caller has given up on this window (Close was called). The window's thread checks it before it shows
        // anything, so a window that was slow to start cannot appear after the check it belonged to is over.
        private volatile bool _abandoned;
        private int _closeSent;

        // A clock that cannot wrap. This was Environment.TickCount, a 32-bit int: negative for the second half of
        // every 49.7-day uptime, where `now - last < 120` was true for every report and the window sat at 0%.
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private const long NeverPosted = long.MinValue;
        private long _lastPostMs = NeverPosted;
        internal const long MinPostIntervalMs = 120;

        /// <summary>
        /// PURE. Should a progress report be posted to the window? Always the first, and always the last (100%);
        /// otherwise only when at least <see cref="MinPostIntervalMs"/> has passed since the previous one.
        /// </summary>
        internal static bool ShouldPost(long nowMs, long lastPostMs, bool isLast)
        {
            if (isLast || lastPostMs == NeverPosted) return true;
            return nowMs - lastPostMs >= MinPostIntervalMs;
        }

        private long NowMs()
        {
            return _clock.ElapsedMilliseconds;
        }

        private const int ReadyTimeoutMs = 5000;

        private FullCheckProgress() { }

        /// <summary>A sweep expected to take less than this is not worth a window that would only flash.</summary>
        internal const double WorthShowingSeconds = 2.0;

        /// <summary>Is a sweep expected to take <paramref name="estimatedSeconds"/> worth a window?</summary>
        internal static bool ShouldShow(double estimatedSeconds)
        {
            return estimatedSeconds >= WorthShowingSeconds;
        }

        /// <summary>Start the window. Never throws; on any failure the result simply does nothing.</summary>
        internal static FullCheckProgress Start(IntPtr owner)
        {
            var p = new FullCheckProgress();
            try
            {
                var ready = new ManualResetEventSlim(false);
                p._thread = new Thread(() =>
                {
                    try
                    {
                        if (p._abandoned) return;          // Close was called before this thread began
                        p._dispatcher = Dispatcher.CurrentDispatcher;
                        // An exception raised on THIS thread must not be able to take Revit's process down.
                        p._dispatcher.UnhandledException += (s, e) =>
                        {
                            Log.Warn("Progress window: an error on its own thread was swallowed", e.Exception);
                            e.Handled = true;
                        };
                        p._window = new FullCheckProgressWindow();
                        if (owner != IntPtr.Zero) new WindowInteropHelper(p._window).Owner = owner;
                        if (p._abandoned) return;          // Close was called while the window was being built: never show it
                        p._window.Show();
                        ready.Set();
                        Dispatcher.Run();
                    }
                    catch (Exception ex)
                    {
                        p._dead = true;
                        Log.Warn("Progress window could not be shown", ex);
                        ready.Set();
                    }
                });
                p._thread.SetApartmentState(ApartmentState.STA);
                p._thread.IsBackground = true;
                p._thread.Name = "LiveClash progress window";
                p._thread.Start();

                if (!ready.Wait(ReadyTimeoutMs) || p._dead || p._window == null) p._dead = true;
            }
            catch (Exception ex)
            {
                p._dead = true;
                Log.Warn("Progress window could not be started", ex);
            }
            return p;
        }

        /// <summary>The current owner window of the process, as the other dialogs use.</summary>
        internal static IntPtr RevitWindow()
        {
            try { return Process.GetCurrentProcess().MainWindowHandle; } catch { return IntPtr.Zero; }
        }

        /// <summary>
        /// Post a progress report. Never blocks: it queues the update on the window's own thread. Updates
        /// arriving faster than the eye can follow are dropped, except the last.
        /// </summary>
        internal void Report(int done, int total, long elapsedMs, long remainingMs)
        {
            if (_dead || _dispatcher == null) return;
            long now = NowMs();
            bool last = total > 0 && done >= total;
            if (!ShouldPost(now, _lastPostMs, last)) return;
            _lastPostMs = now;
            try
            {
                var w = _window;
                _dispatcher.BeginInvoke(new Action(() => w.Update(done, total, elapsedMs, remainingMs)));
            }
            catch (Exception ex) { Log.Warn("Progress window: could not post an update", ex); }
        }

        /// <summary>Change the line under the title (a phase that has no percentage).</summary>
        internal void SetStage(string text)
        {
            if (_dead || _dispatcher == null) return;
            try
            {
                var w = _window;
                _dispatcher.BeginInvoke(new Action(() => w.SetStage(text)));
            }
            catch (Exception ex) { Log.Warn("Progress window: could not post a stage", ex); }
        }

        /// <summary>
        /// Close the window and end its thread. Safe to call more than once, or on an inert instance.
        ///
        /// It ends the thread EVEN WHEN <see cref="Start"/> gave up waiting for it (the window took more than five
        /// seconds to appear). Before, "gave up" meant "dead", and a dead object returned here without closing anything -
        /// so the window that was still being built appeared later and stayed on screen, with its message loop running,
        /// until Revit exited.
        /// </summary>
        internal void Close()
        {
            bool wasLive = !_dead;
            _dead = true;
            _abandoned = true;                                 // a thread that has not shown its window yet will not
            if (System.Threading.Interlocked.Exchange(ref _closeSent, 1) == 1) return;
            var d = _dispatcher;
            var t = _thread;
            if (d == null) return;                             // the thread never got as far as a dispatcher; it will see _abandoned
            try
            {
                // _window is read when this RUNS, not now: on a slow start it is assigned after Close was called.
                d.BeginInvoke(new Action(() =>
                {
                    try { var w = _window; if (w != null) w.Close(); } catch { }
                    d.InvokeShutdown();
                }));
                if (t != null && wasLive) t.Join(3000);
            }
            catch (Exception ex) { Log.Warn("Progress window: could not close cleanly", ex); }
        }
    }
}

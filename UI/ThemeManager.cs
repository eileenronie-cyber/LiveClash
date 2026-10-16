using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Autodesk.Revit.UI;
using Autodesk.Revit.UI.Events;

namespace LiveClash.UI
{
    /// <summary>
    /// Light or dark, following Revit.
    ///
    /// Revit 2024 and later have a dark UI (Options > User Interface > Colour theme). An add-in window that stays
    /// white in the middle of a dark Revit is the most visible way to look like a foreign object - and the panel
    /// is docked right beside Revit's own dark panels.
    ///
    /// HOW. Every brush in the windows is looked up dynamically, and the dark palette (ThemeDark.xaml) has the same
    /// keys as the light one (the top of Theme.xaml). A window that should be dark has ThemeDark.xaml merged
    /// AFTER Theme.xaml, so it wins; one that should be light has it taken out. <see cref="Attach"/> does that
    /// when the window is created and again whenever Revit's theme changes, and lets go when it closes.
    ///
    /// WHAT REVIT SAYS. <c>UIThemeManager.CurrentTheme</c> (Autodesk.Revit.UI, present from Revit 2024 - the
    /// earliest version this add-in supports) and the theme-changed event, hooked once at start-up by
    /// <see cref="HookRevit"/>. Both are read defensively: if either is missing or throws, the answer is Light.
    /// </summary>
    internal static class ThemeManager
    {
        private static readonly Uri DarkUri = new Uri("/LiveClash;component/UI/ThemeDark.xaml", UriKind.Relative);

        private static readonly List<WeakReference<FrameworkElement>> Attached = new List<WeakReference<FrameworkElement>>();
        private static ThemeHook _revitHook;

        // What Revit said last, kept here. IsDark is read for every status cell of every row on every refresh, and
        // by windows built on threads of their own; a native Revit call each time is needless (the value only
        // changes when Revit says so - at start-up and on its ThemeChanged event).
        private static volatile bool _revitDark;
        private static volatile bool _revitDarkKnown;

        /// <summary>How many windows are currently following theme changes.</summary>
        internal static int Following
        {
            get
            {
                lock (Attached)
                {
                    Prune();
                    return Attached.Count;
                }
            }
        }

        /// <summary>Is the UI dark right now?</summary>
        internal static bool IsDark
        {
            get
            {
                return _revitDarkKnown ? _revitDark : RevitIsDark();
            }
        }

        /// <summary>What Revit itself says, read from the API now - never the override, never the cache. False when it cannot be read.</summary>
        internal static bool RevitIsDark()
        {
            try { return UIThemeManager.CurrentTheme == UITheme.Dark; }
            catch { return false; }
        }

        /// <summary>Read Revit's theme and remember it. Called at start-up and whenever Revit says the theme changed.</summary>
        private static void RememberRevitTheme()
        {
            _revitDark = RevitIsDark();
            _revitDarkKnown = true;
        }

        /// <summary>Choose between a light-theme and a dark-theme value (for the few colours code, not XAML, decides).</summary>
        internal static T Pick<T>(T light, T dark) { return IsDark ? dark : light; }

        // ---------------------------------------------------------------- following the theme

        /// <summary>
        /// Make <paramref name="root"/> match the theme now and keep it matching. Safe to call twice.
        /// For a Window the title bar follows too (Windows draws that, not WPF).
        /// </summary>
        internal static void Attach(FrameworkElement root)
        {
            if (root == null) return;
            Apply(root, IsDark);
            lock (Attached)
            {
                Prune();
                foreach (var w in Attached)
                {
                    FrameworkElement e;
                    if (w.TryGetTarget(out e) && ReferenceEquals(e, root)) return;
                }
                Attached.Add(new WeakReference<FrameworkElement>(root));
            }
            var win = root as Window;
            if (win != null)
            {
                win.SourceInitialized += delegate { TitleBar(win, IsDark); };
                win.Closed += delegate { Detach(win); };
            }
            // A docked panel (not a Window) is never closed, only hidden and shown; it stays attached.
        }

        /// <summary>The dark palette on its own, for a test to compare with the light one.</summary>
        internal static ResourceDictionary DarkDictionary() { return new ResourceDictionary { Source = DarkUri }; }

        internal static void Detach(FrameworkElement root)
        {
            lock (Attached)
            {
                Attached.RemoveAll(w =>
                {
                    FrameworkElement e;
                    return !w.TryGetTarget(out e) || ReferenceEquals(e, root);
                });
            }
        }

        private static void Prune()
        {
            Attached.RemoveAll(w => { FrameworkElement e; return !w.TryGetTarget(out e); });
        }

        /// <summary>The theme changed (Revit said so, or a test did): bring every attached window in line.</summary>
        internal static void Refresh()
        {
            RememberRevitTheme();
            List<FrameworkElement> live = new List<FrameworkElement>();
            lock (Attached)
            {
                Prune();
                foreach (var w in Attached)
                {
                    FrameworkElement e;
                    if (w.TryGetTarget(out e)) live.Add(e);
                }
            }
            bool dark = IsDark;
            foreach (var e in live)
            {
                try
                {
                    var d = e.Dispatcher;
                    if (d.CheckAccess()) Apply(e, dark);
                    else d.BeginInvoke(new Action(() => Apply(e, dark)));
                }
                catch (Exception ex) { Core.Log.Warn("Could not switch a window's theme", ex); }
            }
            var handler = Changed;
            if (handler != null) { try { handler(); } catch (Exception ex) { Core.Log.Warn("A theme-changed handler threw", ex); } }
        }

        /// <summary>Raised after every attached window has been switched. The panel uses it to rebuild rows that carry code-chosen colours.</summary>
        internal static event Action Changed;

        /// <summary>
        /// Put the dark dictionary in or take it out. The dictionary is added AFTER what is already merged, so it
        /// overrides Theme.xaml; removing it restores the light palette.
        /// </summary>
        internal static void Apply(FrameworkElement root, bool dark)
        {
            var merged = root.Resources.MergedDictionaries;
            ResourceDictionary existing = null;
            foreach (var d in merged)
                if (d.Source != null && d.Source.OriginalString.EndsWith("ThemeDark.xaml", StringComparison.OrdinalIgnoreCase)) existing = d;

            if (dark && existing == null) merged.Add(new ResourceDictionary { Source = DarkUri });
            else if (!dark && existing != null) merged.Remove(existing);

            var win = root as Window;
            if (win != null) TitleBar(win, dark);
        }

        /// <summary>Is the dark palette merged into this element?</summary>
        internal static bool IsDarkApplied(FrameworkElement root)
        {
            foreach (var d in root.Resources.MergedDictionaries)
                if (d.Source != null && d.Source.OriginalString.EndsWith("ThemeDark.xaml", StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        // ---------------------------------------------------------------- Revit

        /// <summary>
        /// Called once from the add-in's start-up (in API context, so the theme can be read then). Revit raises its
        /// theme-changed event on its own UI thread. Undone by <see cref="UnhookRevit"/> at shutdown.
        /// </summary>
        internal static void HookRevit(UIControlledApplication app)
        {
            if (_revitHook != null || app == null) return;
            try
            {
                RememberRevitTheme();
                var hook = new ThemeHook(h => app.ThemeChanged += h, h => app.ThemeChanged -= h);
                hook.Attach();
                _revitHook = hook;
            }
            catch (Exception ex) { Core.Log.Warn("Could not follow Revit's theme changes", ex); }
        }

        /// <summary>The counterpart of <see cref="HookRevit"/>: called from the add-in's shutdown, like every other event it subscribes.</summary>
        internal static void UnhookRevit()
        {
            var hook = _revitHook;
            _revitHook = null;
            if (hook != null) hook.Detach();
        }

        internal static bool Hooked { get { var h = _revitHook; return h != null && h.IsAttached; } }

        // ---------------------------------------------------------------- title bar

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);

        /// <summary>Does Windows currently draw this window's title bar dark? Null when it cannot say (an older Windows, or no handle yet).</summary>
        internal static bool? TitleBarIsDark(Window w)
        {
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero) return null;
                int v;
                if (DwmGetWindowAttribute(h, 20, out v, sizeof(int)) != 0 && DwmGetWindowAttribute(h, 19, out v, sizeof(int)) != 0) return null;
                return v != 0;
            }
            catch { return null; }
        }

        /// <summary>
        /// The title bar is drawn by Windows, so the window's own colours do not reach it. Asks for the dark one
        /// (Windows 10 1809 and later; the attribute number was 19 before build 18985 and is 20 after; older
        /// Windows ignores both).
        /// </summary>
        private static void TitleBar(Window w, bool dark)
        {
            try
            {
                var h = new WindowInteropHelper(w).Handle;
                if (h == IntPtr.Zero) return;
                int on = dark ? 1 : 0;
                if (DwmSetWindowAttribute(h, 20, ref on, sizeof(int)) != 0)
                    _ = DwmSetWindowAttribute(h, 19, ref on, sizeof(int));   // older Windows 10 id; cosmetic if it fails too
            }
            catch { }
        }
    }

    /// <summary>
    /// One subscription to Revit's theme-changed event that can be taken back. The handler is kept (an anonymous
    /// lambda handed straight to `+=` cannot be removed, and the add-in removes every event it adds at shutdown),
    /// and the event itself is passed in as a pair of actions so a test can drive it with an event of its own.
    /// </summary>
    internal sealed class ThemeHook
    {
        private readonly Action<EventHandler<ThemeChangedEventArgs>> _add;
        private readonly Action<EventHandler<ThemeChangedEventArgs>> _remove;
        private EventHandler<ThemeChangedEventArgs> _handler;

        internal ThemeHook(Action<EventHandler<ThemeChangedEventArgs>> add, Action<EventHandler<ThemeChangedEventArgs>> remove)
        {
            _add = add;
            _remove = remove;
        }

        internal bool IsAttached { get { return _handler != null; } }

        /// <summary>Subscribe. Twice is the same as once.</summary>
        internal void Attach()
        {
            if (_handler != null) return;
            var h = new EventHandler<ThemeChangedEventArgs>((s, e) => ThemeManager.Refresh());
            _add(h);
            _handler = h;
        }

        /// <summary>Unsubscribe. Safe when not attached.</summary>
        internal void Detach()
        {
            var h = _handler;
            if (h == null) return;
            _handler = null;
            try { _remove(h); }
            catch (Exception ex) { Core.Log.Warn("Could not stop following Revit's theme changes", ex); }
        }
    }}

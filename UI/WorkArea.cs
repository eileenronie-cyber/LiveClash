using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace LiveClash.UI
{
    /// <summary>
    /// Keeps a window on the screen it opens on.
    ///
    /// WHY. The bigger windows are sized for a desktop monitor (the filter is 960 x 880, the record 1140 x 680).
    /// On a laptop, or a 1080p screen scaled to 150%, the work area is about 1280 x 690 device-independent
    /// pixels, and a window taller than that pushes its OK / Close row off the bottom of the screen with no way
    /// to reach it. Record clamped itself to the PRIMARY monitor's work area - which is wrong the moment Revit
    /// is on a second monitor of a different size, and the other windows did not clamp at all.
    ///
    /// HOW. Once the window has a handle (so Windows can say which monitor it is on) its size is cut down to
    /// that monitor's work area and it is slid back inside if any of it is outside. The arithmetic is
    /// <see cref="Fit"/>, which is pure; the Windows calls only supply the rectangle.
    /// </summary>
    internal static class WorkArea
    {
        /// <summary>Room left round a window that had to be shrunk.</summary>
        internal const double Margin = 24;

        private static Rect? WorkAreaOf(IntPtr hwnd)
        {
            return MonitorWorkArea(hwnd);
        }

        /// <summary>
        /// PURE. <paramref name="window"/> made to fit inside <paramref name="work"/>: shrunk so that at least
        /// <paramref name="margin"/> is left over, then slid inside if any of it sticks out.
        /// </summary>
        internal static Rect Fit(Rect window, Rect work, double margin = Margin)
        {
            double w = Math.Min(window.Width, Math.Max(work.Width - margin, 1));
            double h = Math.Min(window.Height, Math.Max(work.Height - margin, 1));
            double left = Math.Max(work.Left, Math.Min(window.Left, work.Right - w));
            double top = Math.Max(work.Top, Math.Min(window.Top, work.Bottom - h));
            return new Rect(left, top, w, h);
        }

        /// <summary>Fit <paramref name="w"/> to its monitor as soon as it has one. Windows that size to their content are left alone.</summary>
        internal static void Keep(Window w)
        {
            w.SourceInitialized += delegate { Apply(w); };
        }

        internal static void Apply(Window w)
        {
            try
            {
                var src = PresentationSource.FromVisual(w) as HwndSource;
                if (src == null) return;
                var work = WorkAreaOf(src.Handle);
                if (!work.HasValue || work.Value.Width < 100 || work.Value.Height < 100) return;

                double width = w.Width, height = w.Height;
                if (double.IsNaN(width) || double.IsNaN(height)) return;   // sizes to its content: nothing to cut
                double left = double.IsNaN(w.Left) ? work.Value.Left : w.Left;
                double top = double.IsNaN(w.Top) ? work.Value.Top : w.Top;

                var fit = Fit(new Rect(left, top, width, height), work.Value);
                if (fit.Width != width) w.Width = fit.Width;
                if (fit.Height != height) w.Height = fit.Height;
                if (fit.Left != left) w.Left = fit.Left;
                if (fit.Top != top) w.Top = fit.Top;
            }
            catch (Exception ex)
            {
                // A window that cannot be fitted is still a window; never let this stop it opening.
                Core.Log.Warn("Could not fit a window to its screen", ex);
            }
        }

        // ---------------------------------------------------------------- Windows

        private const uint MONITOR_DEFAULTTONEAREST = 2;

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

        [DllImport("user32.dll")]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        /// <summary>The work area (screen minus the taskbar) of the monitor nearest <paramref name="hwnd"/>, in WPF units.</summary>
        internal static Rect? MonitorWorkArea(IntPtr hwnd)
        {
            var mon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (mon == IntPtr.Zero) return null;
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
            if (!GetMonitorInfo(mon, ref mi)) return null;

            // device pixels -> WPF units, by the same transform WPF itself uses for this window
            var src = HwndSource.FromHwnd(hwnd);
            var toDip = src == null ? System.Windows.Media.Matrix.Identity : src.CompositionTarget.TransformFromDevice;
            var tl = toDip.Transform(new Point(mi.rcWork.Left, mi.rcWork.Top));
            var br = toDip.Transform(new Point(mi.rcWork.Right, mi.rcWork.Bottom));
            return new Rect(tl, br);
        }
    }
}

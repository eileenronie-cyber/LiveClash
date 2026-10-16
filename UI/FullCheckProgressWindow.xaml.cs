using System;
using System.Windows;

namespace LiveClash.UI
{
    /// <summary>
    /// What a full check shows while it runs: a percentage, how many elements, elapsed time and an estimate of
    /// what is left. It has no Cancel, on purpose - the sweep cannot be interrupted, and a button that does not
    /// work is worse than none - so it says plainly that there is nothing to click.
    ///
    /// It is a plain window. The thread it lives on is <see cref="FullCheckProgress"/>'s business.
    /// </summary>
    public partial class FullCheckProgressWindow : Window
    {
        public FullCheckProgressWindow()
        {
            InitializeComponent();
            ThemeManager.Attach(this);
        }

        /// <summary>The line under the title: what the check is doing right now.</summary>
        public void SetStage(string text)
        {
            StageText.Text = text ?? "";
        }

        /// <summary>
        /// One progress report. <paramref name="remainingMs"/> is the caller's estimate, negative when there is
        /// not one yet; the estimate is not shown until some real work has been done, because dividing a
        /// little work by a little time is how a 12-second job once announced 74 minutes.
        /// </summary>
        public void Update(int done, int total, long elapsedMs, long remainingMs)
        {
            if (total < 0) total = 0;
            if (done < 0) done = 0;
            if (done > total) done = total;

            int percent = total > 0 ? (int)(done * 100L / total) : 0;
            Bar.Value = percent;
            PercentText.Text = percent + "%";
            CountText.Text = done.ToString("N0") + " of " + total.ToString("N0") + " elements";
            StageText.Text = done >= total && total > 0 ? "Finishing up…" : "Checking your model against your filter…";

            string t = Clock(elapsedMs) + " elapsed";
            if (remainingMs >= 0 && done > 0 && done < total)
                t += "  ·  about " + Rough(remainingMs) + " left";
            TimeText.Text = t;
        }

        internal static string Clock(long ms)
        {
            if (ms < 0) ms = 0;
            var s = ms / 1000;
            return (s / 60) + ":" + (s % 60).ToString("00");
        }

        internal static string Rough(long ms)
        {
            long s = (ms + 500) / 1000;
            if (s < 5) return "a few seconds";
            if (s < 60) return s + " s";
            long m = (s + 30) / 60;
            return m + (m == 1 ? " minute" : " minutes");
        }
    }
}

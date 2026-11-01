using System;
using System.Collections.Generic;

namespace LiveClash.Core
{
    /// <summary>
    /// Sentences for the user that were raised while nobody was listening (delta review of round 8, L-4). The panel subscribes to
    /// <c>ClashSettings.Notice</c> only while it is loaded, so a Save As sentence ("your triage file could not be copied ...") or "read
    /// again; changes resume" raised while the pane is closed or behind a tab was lost: the silence the Save As sentences exist to end.
    /// A sentence raised with no listener is kept here (the newest <see cref="Capacity"/>, a repeat straight after itself once), and the
    /// panel takes what it missed when it next appears. Revit-free.
    /// </summary>
    public sealed class NoticeHolder
    {
        /// <summary>The most sentences kept; the oldest go first.</summary>
        public const int Capacity = 8;

        private readonly object _gate = new object();
        private readonly List<string> _kept = new List<string>();

        /// <summary>Deliver <paramref name="text"/> to <paramref name="handler"/>, or keep it when there is no handler. A listener that
        /// throws is ignored (a notice never breaks the caller); the sentence was delivered to it, so it is not kept. Nothing and the empty
        /// sentence are not kept.</summary>
        public void Raise(string text, Action<string> handler)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (handler != null)
            {
                try { handler(text); } catch { }
                return;
            }
            lock (_gate)
            {
                if (_kept.Count > 0 && string.Equals(_kept[_kept.Count - 1], text, StringComparison.Ordinal)) return;
                _kept.Add(text);
                while (_kept.Count > Capacity) _kept.RemoveAt(0);
            }
        }

        /// <summary>The sentences kept, oldest first (never null), and forget them.</summary>
        public IList<string> Take()
        {
            lock (_gate)
            {
                var taken = new List<string>(_kept);
                _kept.Clear();
                return taken;
            }
        }
    }
}

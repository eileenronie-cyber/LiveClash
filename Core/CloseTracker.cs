using System;
using System.Collections.Generic;

namespace LiveClash.Core
{
    /// <summary>
    /// The documents that are closing, until Revit says whether the close happened (round 8, P9-3). LiveClash used to do its cleanup at
    /// DocumentClosing, which is raised before another add-in can cancel the close, so a close that did not happen had already dropped the
    /// document's queued fixes. Now DocumentClosing only notes the document here, and the cleanup waits for DocumentClosed with the status
    /// Succeeded. Keyed by the document's DocumentId, NOT by its key: a file closed without saving and opened again is a different document
    /// with a new id, so the reopened one can neither be finalised by the old close nor inherit its note. Pure, so the rules are asserted headlessly.
    /// </summary>
    public sealed class CloseTracker
    {
        public const int DefaultCap = 256;

        /// <summary>The instance the app uses.</summary>
        internal static readonly CloseTracker Shared = new CloseTracker(DefaultCap);

        private readonly int _cap;
        private readonly object _gate = new object();
        private readonly List<KeyValuePair<int, string>> _notes = new List<KeyValuePair<int, string>>();

        public CloseTracker(int cap)
        {
            _cap = cap < 1 ? 1 : cap;
        }

        /// <summary>How many closing documents are noted.</summary>
        public int Count
        {
            get { lock (_gate) return _notes.Count; }
        }

        /// <summary>
        /// A document is closing: note its <paramref name="key"/> under <paramref name="documentId"/>. A second note for the same id replaces the
        /// first. The table is capped (the oldest note goes first), so a close that is cancelled and never reported cannot grow it.
        /// </summary>
        public void Closing(int documentId, string key)
        {
            if (key == null) return;
            lock (_gate)
            {
                for (int i = _notes.Count - 1; i >= 0; i--)
                    if (_notes[i].Key == documentId) _notes.RemoveAt(i);
                _notes.Add(new KeyValuePair<int, string>(documentId, key));
                while (_notes.Count > _cap) _notes.RemoveAt(0);
            }
        }

        /// <summary>
        /// Revit reported the close of <paramref name="documentId"/>. Returns the key to clean up when it SUCCEEDED, and null otherwise (cancelled or
        /// failed: nothing is dropped) or when the id was never noted. The note is removed whatever the status.
        /// </summary>
        public string Closed(int documentId, bool succeeded)
        {
            lock (_gate)
            {
                for (int i = 0; i < _notes.Count; i++)
                {
                    if (_notes[i].Key != documentId) continue;
                    string key = _notes[i].Value;
                    _notes.RemoveAt(i);
                    return succeeded ? key : null;
                }
                return null;
            }
        }
    }
}
using System;
using System.Collections.Generic;

namespace LiveClash.Core
{
    /// <summary>
    /// A resumable walk over a list of documents, the shape the category count has always had (CategoryCatalog.WarmStep) lifted out of
    /// the Revit types so it can be checked headless and mutation-tested.
    ///
    ///   * Every item of every document is visited once, in document order, however the time is sliced.
    ///   * <see cref="Step"/> keeps going until <paramref name="budgetMs"/> has been spent, checked every <c>checkEvery</c> items (a power
    ///     of two; the clock is not read per element), but always visits at least one item per step, so a tiny budget still makes progress.
    ///     It never gives up an item it has already fetched: that item is held and visited first on the next step.
    ///   * A document that will not open (the opener throws or returns null) is skipped, as the old iterator-open loop did.
    ///   * A walk that throws part-way is <see cref="Failed"/>: the step says done and nothing more is visited. Callers must not file a
    ///     half-built answer; they fall back to the one-shot path.
    ///   * The SIGNATURE is fixed at construction. It names the document set this pass is actually walking, so what it finds is filed
    ///     under that set (see <see cref="SignedCache{T}"/>) and a document set that changed since is a different key, never a hit.
    ///     That is the rule U1 needs for caching the key once per pass instead of once per slice.
    /// </summary>
    public sealed class SlicedPass<TDoc, TItem>
    {
        private readonly string _signature;
        private readonly IList<TDoc> _docs;
        private readonly Func<TDoc, IEnumerator<TItem>> _open;
        private readonly Action<int, TItem> _visit;
        private readonly Func<long> _now;
        private readonly int _mask;

        private int _docIx = -1;
        private IEnumerator<TItem> _it;
        private bool _hasCurrent;
        private bool _finished;
        private bool _failed;
        private int _seen;
        private int _skipped;

        public SlicedPass(string signature, IList<TDoc> docs, Func<TDoc, IEnumerator<TItem>> open, Action<int, TItem> visit, Func<long> nowMs, int checkEvery = 1024)
        {
            _signature = signature;
            _docs = docs ?? new List<TDoc>();
            _open = open;
            _visit = visit;
            _now = nowMs;
            _mask = checkEvery < 1 ? 0 : checkEvery - 1;
        }

        /// <summary>The document set this pass walks, as given at construction. Never changes.</summary>
        public string Signature { get { return _signature; } }

        /// <summary>Items visited so far.</summary>
        public int Seen { get { return _seen; } }

        /// <summary>Documents that would not open (the opener threw or returned nothing) and were skipped. A pass that skipped one is missing that document's items.</summary>
        public int Skipped { get { return _skipped; } }

        /// <summary>The pass walked every document it was given and did not fail: its answer is whole. Only an intact pass may be filed as an answer (U1 review H1).</summary>
        public bool Intact { get { return !_failed && _skipped == 0; } }

        /// <summary>The document being walked (0-based); the document count once the walk is over. Only ever goes up.</summary>
        public int DocIndex { get { return _docIx < 0 ? 0 : Math.Min(_docIx, _docs.Count); } }

        /// <summary>How many documents the pass was given.</summary>
        public int DocCount { get { return _docs.Count; } }

        /// <summary>Give the pass up: the walk is over and the iterator it holds is closed (a part-way pass that is dropped would otherwise leave one open).</summary>
        public void Abandon() { _finished = true; Close(); }

        /// <summary>The walk threw part-way. The pass is done and its result must not be used.</summary>
        public bool Failed { get { return _failed; } }

        /// <summary>Is this pass walking exactly the document set named by <paramref name="signature"/>? An exact (ordinal) match.</summary>
        public bool IsFor(string signature) { return signature != null && string.Equals(_signature, signature, StringComparison.Ordinal); }

        /// <summary>Advance by about <paramref name="budgetMs"/>. Returns true when the walk is over (finished or failed).</summary>
        public bool Step(int budgetMs)
        {
            if (_finished) return true;
            long start = _now();
            int visitedThisStep = 0;
            try
            {
                while (true)
                {
                    if (!_hasCurrent)
                    {
                        if (_it == null && !OpenNext()) { _finished = true; return true; }
                        if (!_it.MoveNext())
                        {
                            Close();
                            continue;
                        }
                        _hasCurrent = true;
                    }

                    // The budget is only looked at BEFORE an item is visited, and never before the first of a step.
                    if (visitedThisStep > 0 && (_seen & _mask) == 0 && _now() - start >= budgetMs) return false;

                    _visit(_docIx, _it.Current);
                    _hasCurrent = false;
                    _seen++;
                    visitedThisStep++;
                }
            }
            catch
            {
                _failed = true;
                _finished = true;
                Close();
                return true;
            }
        }

        private bool OpenNext()
        {
            while (++_docIx < _docs.Count)
            {
                try
                {
                    var it = _open(_docs[_docIx]);
                    if (it != null) { _it = it; return true; }
                    _skipped++;
                }
                catch { _skipped++; /* a document that will not open is skipped - and counted, so the pass is known not to be whole */ }
            }
            return false;
        }

        private void Close()
        {
            var it = _it;
            _it = null;
            _hasCurrent = false;
            if (it != null) { try { it.Dispose(); } catch { } }
        }
    }

    /// <summary>
    /// A cache with ONE ENTRY PER SIGNATURE, not one slot. A lookup under a document set other than the one an entry was filed under
    /// misses, which is how a link loaded or unloaded between slices can never be served counts for the wrong set.
    /// </summary>
    public sealed class SignedCache<T>
    {
        private readonly Dictionary<string, T> _items = new Dictionary<string, T>(StringComparer.Ordinal);
        private readonly object _gate = new object();

        public void Put(string signature, T value)
        {
            if (signature == null) return;
            lock (_gate) _items[signature] = value;
        }

        public bool TryGet(string signature, out T value)
        {
            value = default(T);
            if (signature == null) return false;
            lock (_gate) return _items.TryGetValue(signature, out value);
        }

        public void Clear()
        {
            lock (_gate) _items.Clear();
        }

        public int Count { get { lock (_gate) return _items.Count; } }
    }
}

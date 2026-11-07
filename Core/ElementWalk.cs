using System;
using System.Collections;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// A streamed walk over every non-type element of one document, as an <see cref="IEnumerator{T}"/> so a
    /// <see cref="SlicedPass{TDoc,TItem}"/> can resume it. One iterator over one document: never one collector per category, which is
    /// O(document) each (see CategoryCatalog.Counts).
    /// </summary>
    internal sealed class ElementWalk : IEnumerator<Element>
    {
        private readonly FilteredElementIterator _it;

        public ElementWalk(Document doc)
        {
            _it = new FilteredElementCollector(doc)
                .WhereElementIsNotElementType()
                .GetElementIterator();
            _it.Reset();
        }

        public Element Current { get { return _it.Current as Element; } }
        object IEnumerator.Current { get { return _it.Current; } }
        public bool MoveNext() { return _it.MoveNext(); }
        public void Reset() { _it.Reset(); }
        public void Dispose() { _it.Dispose(); }
    }
}

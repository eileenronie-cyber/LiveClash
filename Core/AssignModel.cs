using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace LiveClash.Core
{
    public enum AssignMode { None, Candidate, Other, Unassign }

    public sealed class AssignOption
    {
        public string Name { get; set; } = "";
        public string Reason { get; set; } = "";
    }

    public sealed class AssignResult
    {
        public string User { get; set; } = "";
        public bool Unassign { get; set; }
        /// <summary>A typed name that is not among the names seen in this model (Q4).</summary>
        public bool NotSeen { get; set; }
    }

    /// <summary>The Assign dialog: a list of names seen in the model, a search, a typed name and Unassign (spec 9, Q4). Pure.</summary>
    public sealed class AssignModel
    {
        private const int MaxName = 100;

        private readonly List<AssignOption> _all = new List<AssignOption>();
        private readonly int _rows;
        private string _search = "";
        private string _other = "";

        private static string T(string s) { return s == null ? "" : s.Trim(); }

        public AssignModel(IReadOnlyList<string> issueNos, IEnumerable<AssignCandidate> candidates, string me, IEnumerable<string> pendingLines)
        {
            var rows = issueNos ?? Array.Empty<string>();
            _rows = rows.Count;
            var first = rows.Select(T).FirstOrDefault(s => s.Length > 0);
            if (_rows == 0) Heading = "Assign";
            else if (first == null) Heading = "Assign " + _rows.ToString(CultureInfo.InvariantCulture) + (_rows == 1 ? " clash" : " clashes");
            else Heading = "Assign " + first + (_rows > 1 ? " (and " + (_rows - 1).ToString(CultureInfo.InvariantCulture) + " more)" : "");

            foreach (var c in candidates ?? Array.Empty<AssignCandidate>())
            {
                var name = c == null ? "" : T(c.Name);
                if (name.Length == 0) continue;
                var reason = T(c.Reason);
                var have = Find(name);
                if (have == null) _all.Add(new AssignOption { Name = name, Reason = reason });
                else if (reason.Length > 0) have.Reason = have.Reason.Length > 0 ? have.Reason + "; " + reason : reason;
            }

            var you = T(me);
            if (you.Length > 0)
            {
                var mine = Find(you);
                if (mine == null) _all.Add(new AssignOption { Name = you, Reason = "you" });
                else mine.Reason = mine.Reason.Length > 0 ? "you; " + mine.Reason : "you";
            }
            _all.Sort((a, b) => StringComparer.OrdinalIgnoreCase.Compare(a.Name, b.Name));

            foreach (var line in pendingLines ?? Array.Empty<string>())
            {
                var t = T(line);
                if (t.Length > 0) PendingLines.Add(t);
            }
            if (PendingLines.Count > 0) PendingHeading = "These will wait as pending, because another user holds them:";
        }

        private AssignOption Find(string name)
        {
            return _all.FirstOrDefault(o => string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
        }

        // Stryker disable once String : Heading is set by every constructor path
        public string Heading { get; private set; } = "";
        public string PendingHeading { get; private set; } = "";
        public List<string> PendingLines { get; private set; } = new List<string>();
        public AssignMode Mode { get; private set; }
        public string SelectedName { get; private set; } = "";

        /// <summary>The names whose text contains the search; a selection the search hides is dropped.</summary>
        public string Search
        {
            get { return _search; }
            set
            {
                _search = T(value);
                if (Mode == AssignMode.Candidate && !Visible.Any(o => o.Name == SelectedName)) { Mode = AssignMode.None; SelectedName = ""; }
            }
        }

        public IReadOnlyList<AssignOption> Visible
        {
            get
            {
                return _search.Length == 0 ? _all.ToList()
                    : _all.Where(o => CultureInfo.InvariantCulture.CompareInfo.IndexOf(o.Name, _search, CompareOptions.IgnoreCase) >= 0).ToList();
            }
        }

        /// <summary>Typing a name selects "other"; clearing the field leaves the choice as it was.</summary>
        public string OtherName
        {
            get { return _other; }
            set
            {
                _other = value ?? "";
                if (T(_other).Length > 0) { Mode = AssignMode.Other; SelectedName = ""; }
            }
        }

        public bool SelectCandidate(string name)
        {
            var n = T(name);
            var hit = Visible.FirstOrDefault(o => string.Equals(o.Name, n, StringComparison.OrdinalIgnoreCase));
            if (hit == null) return false;
            Mode = AssignMode.Candidate;
            SelectedName = hit.Name;
            return true;
        }

        public void SelectUnassign() { Mode = AssignMode.Unassign; SelectedName = ""; }

        public void SelectOther() { Mode = AssignMode.Other; SelectedName = ""; }

        /// <summary>Why the typed name cannot be used, or "" (only while "other" is selected and something is typed).</summary>
        public string Problem
        {
            get
            {
                if (Mode != AssignMode.Other) return "";
                var typed = T(_other);
                if (typed.Any(char.IsControl)) return "A name cannot hold control characters.";
                if (typed.Length > MaxName) return "A name can be at most 100 characters.";
                return "";
            }
        }

        /// <summary>"not seen in this model" for a usable typed name nobody in the list has (Q4).</summary>
        public string OtherHint
        {
            get
            {
                if (Mode != AssignMode.Other) return "";
                var typed = T(_other);
                return typed.Length > 0 && Problem.Length == 0 && Find(typed) == null ? "not seen in this model" : "";
            }
        }

        public bool CanAssign { get { return Result() != null; } }

        public AssignResult Result()
        {
            if (_rows == 0) return null;
            switch (Mode)
            {
                case AssignMode.Unassign:
                    return new AssignResult { User = "", Unassign = true };
                case AssignMode.Candidate:
                    return new AssignResult { User = SelectedName };
                case AssignMode.Other:
                    var typed = T(_other);
                    if (typed.Length == 0 || Problem.Length > 0) return null;
                    var seen = Find(typed);
                    return seen != null ? new AssignResult { User = seen.Name } : new AssignResult { User = typed, NotSeen = true };
                default:
                    return null;
            }
        }
    }
}

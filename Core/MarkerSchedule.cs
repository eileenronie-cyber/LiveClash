using System;
using System.Collections.Generic;
using System.Linq;
using Autodesk.Revit.DB;

namespace LiveClash.Core
{
    /// <summary>
    /// The one native schedule "LiveClash - Clashes" (spec 4): the marker parameters as columns in the spec's order, only markers
    /// (a filter on the Clash Id), grouped by clash set with a header and a count, sorted by group, status order and issue number. A colon
    /// is refused in view names, hence the dash. It is created once, found by the id kept in the settings record (never by name, so a
    /// user may rename it), made again when it is missing, and NEVER re-formatted afterwards: the user owns widths, sorting and extra filters.
    /// </summary>
    [RevitBound]
    public static class MarkerSchedule
    {
        public const string Name = "LiveClash - Clashes";

        /// <summary>Probe P9 decides the base filter. BeginsWith "LC-" is the spec's choice; HasValue is the fallback.</summary>
        internal const ScheduleFilterType BaseFilterKind = ScheduleFilterType.BeginsWith;
        internal const string BaseFilterText = "LC-";

        private static readonly Dictionary<string, string> Headings = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "LC Issue No", "Issue no." }, { "LC Clash Id", "Clash Id" }, { "LC Status", "Status" }, { "LC Severity", "Severity" },
            { "LC Set", "Clash set" }, { "LC Also Caught By", "Also caught by" }, { "LC Group", "Group" },
            { "LC A Id", "A id" }, { "LC A Name", "A name" }, { "LC A Category", "A category" },
            { "LC B Id", "B id" }, { "LC B Name", "B name" }, { "LC B Category", "B category" }, { "LC B Model", "B model" },
            { "LC Level", "Level" }, { "LC Assigned To", "Assigned to" }, { "LC Reported By", "Reported by" }, { "LC Reported", "Reported" },
            { "LC Changed By", "Changed by" }, { "LC Changed", "Changed" }, { "LC Note", "Note" }, { "LC Last Checked", "Last checked" }
        };

        /// <summary>The schedule the settings record points at, or null when there is none (or it was deleted).</summary>
        public static ViewSchedule Find(Document doc)
        {
            var settings = MarkerSettings.ReadSettings(doc);
            if (settings == null || string.IsNullOrEmpty(settings.ScheduleUid)) return null;
            return doc.GetElement(settings.ScheduleUid) as ViewSchedule;
        }

        private static string FreeName(Document doc)
        {
            var taken = new HashSet<string>(new FilteredElementCollector(doc).OfClass(typeof(View)).Cast<View>().Select(v => v.Name), StringComparer.Ordinal);
            if (!taken.Contains(Name)) return Name;
            for (int i = 2; ; i++)
            {
                var candidate = Name + " (" + i + ")";
                if (!taken.Contains(candidate)) return candidate;
            }
        }

        private static ScheduleField AddField(ScheduleDefinition def, Document doc, MarkerParameterMap.Def d)
        {
            var element = SharedParameterElement.Lookup(doc, d.ParameterGuid);
            if (element == null) throw new InvalidOperationException("The parameter " + d.Name + " is not in the model.");
            var schedulable = def.GetSchedulableFields().FirstOrDefault(sf => IdUtil.Value(sf.ParameterId) == IdUtil.Value(element.Id));
            if (schedulable == null) throw new InvalidOperationException("The parameter " + d.Name + " cannot be put in a schedule.");
            return def.AddField(schedulable);
        }

        /// <summary>The schedule, made when missing (and the settings record is the caller's to write). Needs an open transaction and bound parameters that are already committed.</summary>
        public static ViewSchedule Ensure(Document doc)
        {
            var found = Find(doc);
            if (found != null) return found;

            var schedule = ViewSchedule.CreateSchedule(doc, MarkerCategory.Id);
            schedule.Name = FreeName(doc);
            var def = schedule.Definition;
            var fields = new Dictionary<string, ScheduleField>(StringComparer.Ordinal);
            foreach (var d in MarkerParameterMap.Definitions)
            {
                var field = AddField(def, doc, d);
                string heading;
                if (Headings.TryGetValue(d.Name, out heading)) field.ColumnHeading = heading;
                fields[d.Name] = field;
            }
            fields[MarkerParameterMap.StatusOrderName].IsHidden = true;

            def.AddFilter(new ScheduleFilter(fields["LC Clash Id"].FieldId, BaseFilterKind, BaseFilterText));

            var bySet = new ScheduleSortGroupField(fields["LC Set"].FieldId, ScheduleSortOrder.Ascending)
            {
                ShowHeader = true, ShowFooter = true, ShowFooterCount = true, ShowFooterTitle = false, ShowBlankLine = false
            };
            def.AddSortGroupField(bySet);
            foreach (var name in new[] { "LC Group", MarkerParameterMap.StatusOrderName, "LC Issue No" })
                def.AddSortGroupField(new ScheduleSortGroupField(fields[name].FieldId, ScheduleSortOrder.Ascending));
            return schedule;
        }
    }
}

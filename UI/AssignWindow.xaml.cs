using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LiveClash.Core;

namespace LiveClash.UI
{
    /// <summary>
    /// The Assign dialog (spec 9, Q4): pick a name seen in the model, type one that has not been seen, or unassign.
    /// All its rules are <see cref="AssignModel"/>; this only shows it and passes the clicks on.
    /// </summary>
    public partial class AssignWindow : Window
    {
        private const string UnassignText = "Unassign";

        private readonly AssignModel _model;
        private bool _syncing;

        /// <summary>What the user chose, once Assign was pressed; null otherwise.</summary>
        public AssignResult Picked { get; private set; }

        public AssignWindow(AssignModel model)
        {
            InitializeComponent();
            ThemeManager.Attach(this);
            _model = model;

            Title = model.Heading;
            HeadingText.Text = model.Heading;
            PendingHeading.Text = model.PendingHeading;
            PendingList.ItemsSource = model.PendingLines;
            PendingPanel.Visibility = model.PendingLines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            Rebuild();
        }

        // The list: the names the search leaves, then Unassign (which no search hides).
        private void Rebuild()
        {
            _syncing = true;
            try
            {
                var items = new List<AssignOption>(_model.Visible);
                items.Add(new AssignOption { Name = UnassignText, Reason = "clear the assignee" });
                NameList.ItemsSource = items;
            }
            finally { _syncing = false; }
            Sync();
        }

        private void Sync()
        {
            _syncing = true;
            try
            {
                var items = NameList.ItemsSource as List<AssignOption>;
                AssignOption chosen = null;
                if (items != null)
                {
                    if (_model.Mode == AssignMode.Candidate) chosen = items.FirstOrDefault(o => o.Name == _model.SelectedName);
                    else if (_model.Mode == AssignMode.Unassign) chosen = items.LastOrDefault();
                }
                NameList.SelectedItem = chosen;

                SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
                OtherHint.Text = _model.OtherHint;
                ProblemText.Text = _model.Problem;
                AssignButton.IsEnabled = _model.CanAssign;
            }
            finally { _syncing = false; }
        }

        private void Search_Changed(object sender, TextChangedEventArgs e)
        {
            if (_model == null) return;
            _model.Search = SearchBox.Text;
            Rebuild();
        }

        private void Other_Changed(object sender, TextChangedEventArgs e)
        {
            if (_model == null) return;
            _model.OtherName = OtherBox.Text;
            Sync();
        }

        private void Name_Changed(object sender, SelectionChangedEventArgs e)
        {
            if (_syncing || _model == null) return;
            var option = NameList.SelectedItem as AssignOption;
            if (option == null) return;
            var items = NameList.ItemsSource as List<AssignOption>;
            if (items != null && ReferenceEquals(option, items.LastOrDefault())) _model.SelectUnassign();
            else _model.SelectCandidate(option.Name);
            Sync();
        }

        private void Name_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (_model.CanAssign) Ok_Click(sender, e);
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Picked = _model.Result();
            if (Picked == null) return;
            DialogResult = true;
            Close();
        }
    }
}

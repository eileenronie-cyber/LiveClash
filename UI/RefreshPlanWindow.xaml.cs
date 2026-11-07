using System.Windows;
using LiveClash.Core;

namespace LiveClash.UI
{
    /// <summary>
    /// The Refresh plan dialog (spec 9): what a Refresh would do, in counts, before it writes anything.
    /// All its text and logic is <see cref="RefreshPlanModel"/>; this only shows it.
    /// </summary>
    public partial class RefreshPlanWindow : Window
    {
        private readonly RefreshPlanModel _model;

        public RefreshPlanWindow(RefreshPlanModel model)
        {
            InitializeComponent();
            ThemeManager.Attach(this);
            _model = model;

            Title = model.Title;
            HeadingText.Text = model.Title;
            FooterText.Text = model.Footer;
            PrimaryButton.Content = model.PrimaryText;
            CancelButton.Content = model.CancelText;

            Fill(RefusalBox, RefusalText, model.Refusal);
            Fill(CapBox, CapText, model.CapMessage);
            Fill(WarnBox, WarnText, model.Warning);
            Fill(null, NothingText, model.NothingNote);

            AddsHeading.Text = model.AddsHeading;
            AddsList.ItemsSource = model.AddsLines;
            AddsPanel.Visibility = model.AddsLines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            ShowingHeading.Text = model.ShowingHeading;
            ShowingList.ItemsSource = model.ShowingLines;
            ShowingPanel.Visibility = model.ShowingLines.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

            LinesList.ItemsSource = model.Lines;

            if (model.ShowRetention)
            {
                RetentionCheck.Content = model.RetentionText;
                RetentionCheck.IsChecked = model.RemoveRetention;
                RetentionCheck.Visibility = Visibility.Visible;
            }
            Sync();
        }

        private static void Fill(FrameworkElement box, System.Windows.Controls.TextBlock text, string value)
        {
            bool any = !string.IsNullOrEmpty(value);
            text.Text = value ?? "";
            var target = box ?? text;
            target.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        }

        private void Sync()
        {
            PrimaryButton.IsEnabled = _model.CanApply;
        }

        private void Retention_Changed(object sender, RoutedEventArgs e)
        {
            if (_model == null) return;
            _model.RemoveRetention = RetentionCheck.IsChecked == true;
            Sync();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = true;
            Close();
        }
    }
}

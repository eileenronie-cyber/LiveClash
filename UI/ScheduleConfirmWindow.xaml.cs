using System.Windows;
using LiveClash.Core;

namespace LiveClash.UI
{
    /// <summary>
    /// A confirmation that starts unticked (E9): what will happen, a box to tick, and OK only once it is ticked.
    /// All its text and its rule is <see cref="ConfirmModel"/>; this only shows it.
    /// </summary>
    public partial class ScheduleConfirmWindow : Window
    {
        private readonly ConfirmModel _model;

        public ScheduleConfirmWindow(ConfirmModel model)
        {
            InitializeComponent();
            ThemeManager.Attach(this);
            _model = model;

            Title = model.Title;
            HeadingText.Text = model.Title;
            LinesList.ItemsSource = model.Lines;
            TickBox.Content = model.CheckText;
            TickBox.IsChecked = model.Ticked;
            OkButton.Content = model.OkText;
            CancelButton.Content = model.CancelText;
            Sync();
        }

        private void Sync()
        {
            OkButton.IsEnabled = _model.CanOk;
        }

        private void Tick_Changed(object sender, RoutedEventArgs e)
        {
            if (_model == null) return;
            _model.Ticked = TickBox.IsChecked == true;
            Sync();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = _model.CanOk;
            Close();
        }
    }
}

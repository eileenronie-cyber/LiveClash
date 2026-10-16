using System.Windows;

namespace LiveClash.UI
{
    /// <summary>
    /// One line saying why a decision was made. "Coordinated" on its own is a hidden
    /// row nobody can audit later; "agreed sleeve per RFI 42" is a record.
    ///
    /// Single line on purpose — this is a reason, not a discussion. WPF has no
    /// built-in input box, hence a window rather than a prompt call.
    /// </summary>
    public partial class NoteWindow : Window
    {
        public string Note { get; private set; }

        public NoteWindow(string prompt, string existing)
        {
            InitializeComponent();
            ThemeManager.Attach(this);
            PromptText.Text = prompt;
            NoteBox.Text = existing ?? "";
            NoteBox.SelectAll();
            NoteBox.Focus();
        }

        private void Ok_Click(object sender, RoutedEventArgs e)
        {
            Note = (NoteBox.Text ?? "").Trim();
            DialogResult = true;
            Close();
        }
    }
}

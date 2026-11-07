using System.Windows;
using LiveClash.Core;

namespace LiveClash.UI
{
    /// <summary>
    /// The windows and messages of the clash schedule flows, shown over the panel's window. Thin: the text and the
    /// rules are in the models; <see cref="ScheduleController"/> decides what to show and when.
    /// </summary>
    public sealed class ScheduleDialogs : IScheduleDialogs
    {
        private const string Caption = "Live Clash";

        private readonly ClashPanel _panel;

        public ScheduleDialogs(ClashPanel panel)
        {
            _panel = panel;
        }

        private Window Owner { get { return Window.GetWindow(_panel); } }

        public bool ShowPlan(RefreshPlanModel model)
        {
            return new RefreshPlanWindow(model) { Owner = Owner }.ShowDialog() == true;
        }

        public bool Confirm(ConfirmModel model)
        {
            return new ScheduleConfirmWindow(model) { Owner = Owner }.ShowDialog() == true;
        }

        public AssignResult ShowAssign(AssignModel model)
        {
            var window = new AssignWindow(model) { Owner = Owner };
            return window.ShowDialog() == true ? window.Picked : null;
        }

        public void Status(string text)
        {
            _panel.SetStatus(text);
        }

        public void Message(string text)
        {
            var owner = Owner;
            if (owner != null) MessageBox.Show(owner, text, Caption, MessageBoxButton.OK, MessageBoxImage.Information);
            else MessageBox.Show(text, Caption, MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}

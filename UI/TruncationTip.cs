using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace LiveClash.UI
{
    /// <summary>
    /// A hover tip that appears only when the text it belongs to has been cut short.
    ///
    /// WHY. A list cell that is narrower than its text shows the front of the name and hides the rest - and the
    /// rest is the part that tells one pipe from the next ("Pipes: Pipe Types - Chilled water 150 (412331)").
    /// A tip on every cell would pop up over text that is already fully visible, which is noise; so the tip is
    /// switched off unless the text really is wider than the room it has.
    ///
    /// USE. On a TextBlock in a cell template:
    /// <code>&lt;TextBlock Text="{Binding HostText}" TextTrimming="CharacterEllipsis"
    ///            ToolTip="{Binding HostText}" ui:TruncationTip.OnlyWhenCut="True"/&gt;</code>
    /// </summary>
    public static class TruncationTip
    {
        public static readonly DependencyProperty OnlyWhenCutProperty = DependencyProperty.RegisterAttached(
            "OnlyWhenCut", typeof(bool), typeof(TruncationTip), new PropertyMetadata(false, OnChanged));

        public static bool GetOnlyWhenCut(DependencyObject d) { return (bool)d.GetValue(OnlyWhenCutProperty); }
        public static void SetOnlyWhenCut(DependencyObject d, bool value) { d.SetValue(OnlyWhenCutProperty, value); }

        private static void OnChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var fe = d as FrameworkElement;
            if (fe == null) return;
            fe.ToolTipOpening -= Opening;
            if ((bool)e.NewValue) fe.ToolTipOpening += Opening;
        }

        private static void Opening(object sender, ToolTipEventArgs e)
        {
            var tb = sender as TextBlock;
            if (tb != null && !IsCut(tb)) e.Handled = true;   // handled = the tip is not shown
        }

        /// <summary>PURE. Is text that wants <paramref name="wanted"/> wide, given <paramref name="room"/>, cut short?</summary>
        internal static bool NeedsTip(double wanted, double room)
        {
            // Half a pixel of slack: text that fits exactly must not be reported as cut by rounding.
            return room > 0 && wanted > room + 0.5;
        }

        /// <summary>Is this block showing less than its text?</summary>
        internal static bool IsCut(TextBlock tb)
        {
            if (tb == null || string.IsNullOrEmpty(tb.Text)) return false;
            return NeedsTip(WidthOf(tb), tb.ActualWidth);
        }

        /// <summary>How wide the block's whole text would be on one line, in its own font.</summary>
        internal static double WidthOf(TextBlock tb)
        {
            var ft = new FormattedText(tb.Text, CultureInfo.CurrentUICulture, tb.FlowDirection,
                new Typeface(tb.FontFamily, tb.FontStyle, tb.FontWeight, tb.FontStretch),
                tb.FontSize, Brushes.Black, 1.0);
            return ft.WidthIncludingTrailingWhitespace;
        }
    }
}

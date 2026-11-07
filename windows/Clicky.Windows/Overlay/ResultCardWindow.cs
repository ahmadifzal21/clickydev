using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using Clicky.Windows.Companion;

namespace Clicky.Windows.Overlay;

/// <summary>
/// A card the user can keep (E11 T11.10/T11.25): a reply's <c>result("title", "body")</c> — a command, a draft, a summary.
/// Unlike the overlay's cards it takes clicks (Copy, Close), so it is its own small topmost window. It never takes focus from
/// the app the user is in, and it is left out of screenshots like the rest of Clicky. A new result replaces the old one.
/// </summary>
internal sealed class ResultCardWindow : Window
{
    private static ResultCardWindow? shownCard;

    private ResultCardWindow(string title, string body)
    {
        Title = "Clicky result";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        SizeToContent = SizeToContent.WidthAndHeight;

        Button copyButton = CardButton("Copy");
        copyButton.Click += (_, _) => { if (CompanionActions.Copy(body)) copyButton.Content = "Copied"; };
        Button closeButton = CardButton("Close");
        closeButton.Click += (_, _) => Close();
        StackPanel buttons = new() { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        buttons.Children.Add(copyButton);
        buttons.Children.Add(closeButton);

        StackPanel content = new();
        if (title.Length > 0)
            content.Children.Add(new TextBlock { Text = title, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = new SolidColorBrush(Color.FromRgb(0xEC, 0xEE, 0xED)), Margin = new Thickness(0, 0, 0, 6), TextWrapping = TextWrapping.Wrap });
        TextBlock bodyText = ScreenDrawingLayer.RichText(body, 13);
        content.Children.Add(new ScrollViewer { Content = bodyText, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        content.Children.Add(buttons);

        Content = new Border
        {
            Margin = new Thickness(16), // Room for the shadow.
            Background = new SolidColorBrush(Color.FromArgb(0xF5, 0x17, 0x19, 0x18)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x99, ScreenDrawingLayer.InkColor.R, ScreenDrawingLayer.InkColor.G, ScreenDrawingLayer.InkColor.B)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16, 12, 16, 12),
            Width = 380,
            Child = content,
            Effect = new DropShadowEffect { Color = Colors.Black, Opacity = 0.4, BlurRadius = 24, ShadowDepth = 6, Direction = 270 }
        };
    }

    private static Button CardButton(string text) => new()
    {
        Content = text,
        Margin = new Thickness(8, 0, 0, 0),
        Padding = new Thickness(12, 4, 12, 4),
        Foreground = Brushes.White,
        Background = new SolidColorBrush(ScreenDrawingLayer.InkColor),
        BorderThickness = new Thickness(0),
        Cursor = System.Windows.Input.Cursors.Hand
    };

    /// <summary>Shows the card in the bottom-right corner of the primary work area, replacing any card already shown.</summary>
    public static ResultCardWindow Show(string title, string body)
    {
        shownCard?.Close();
        ResultCardWindow card = new(title, body);
        card.Loaded += (_, _) =>
        {
            Rect workArea = SystemParameters.WorkArea;
            card.Left = workArea.Right - card.ActualWidth - 8;
            card.Top = workArea.Bottom - card.ActualHeight - 8;
        };
        card.Closed += (_, _) => { if (shownCard == card) shownCard = null; };
        shownCard = card;
        card.Show();
        return card;
    }

    protected override void OnSourceInitialized(EventArgs eventArgs)
    {
        base.OnSourceInitialized(eventArgs);
        IntPtr handle = new WindowInteropHelper(this).Handle;
        SetWindowLong(handle, GWL_EXSTYLE, GetWindowLong(handle, GWL_EXSTYLE) | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
        SetWindowDisplayAffinity(handle, WDA_EXCLUDEFROMCAPTURE); // Best effort: older Windows simply shows it in captures.
    }

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x00000011;

    [DllImport("user32.dll", SetLastError = true)] private static extern int GetWindowLong(IntPtr handle, int index);
    [DllImport("user32.dll", SetLastError = true)] private static extern int SetWindowLong(IntPtr handle, int index, int newLong);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowDisplayAffinity(IntPtr handle, uint affinity);
}

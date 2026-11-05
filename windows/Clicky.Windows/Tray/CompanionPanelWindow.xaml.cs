using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Clicky.Windows.Companion;
using Clicky.Windows.Configuration;
using Clicky.Windows.Display;

namespace Clicky.Windows.Tray;

/// <summary>
/// The floating companion panel shown next to the tray icon. Like the Mac's non-activating
/// NSPanel it never steals focus (WS_EX_NOACTIVATE), so the app the user was in stays active.
/// Because it's never active it can't rely on Deactivated or keyboard focus, so while visible it
/// watches for outside clicks with a low-level mouse hook and for Escape with a hotkey.
/// </summary>
public partial class CompanionPanelWindow : Window
{
    /// <summary>Visible gap between the card and the taskbar / work-area edges, in DIPs.</summary>
    private const double CardGapFromTaskbarDips = 8;

    /// <summary>
    /// A click on the tray icon while the panel is open first hides it via the outside-click
    /// hook, then arrives as a tray click. Ignoring tray clicks this soon after an outside-click
    /// hide makes the icon toggle the panel closed instead of reopening it.
    /// </summary>
    private static readonly TimeSpan TrayClickIgnoreWindowAfterOutsideClickHide = TimeSpan.FromMilliseconds(350);

    private const int EscapeHotKeyId = 0xC11C;

    private IntPtr panelWindowHandle;
    private HwndSource? panelHwndSource;
    private IntPtr mouseHookHandle;
    private LowLevelMouseProc? mouseHookCallback; // kept in a field so the GC can't collect it while hooked
    private DateTime lastOutsideClickHideTimeUtc = DateTime.MinValue;

    private readonly ClickyConfigurationStore clickyConfigurationStore;

    /// <summary>Raised when the user clicks Quit Clicky; the app owns shutdown.</summary>
    public event Action? QuitRequested;

    public CompanionPanelWindow(ClickyConfigurationStore clickyConfigurationStore)
    {
        this.clickyConfigurationStore = clickyConfigurationStore;
        InitializeComponent();
        SourceInitialized += HandleSourceInitialized;

        BuildBackendPickers();
        if (clickyConfigurationStore.ConfigurationLoadErrorMessage is { } configurationLoadErrorMessage)
        {
            ConfigurationErrorTextBlock.Text =
                $"clicky.config.json couldn't be read, so Clicky is using defaults and won't save changes until it's fixed.\n{configurationLoadErrorMessage}";
            ConfigurationErrorBanner.Visibility = Visibility.Visible;
        }

        clickyConfigurationStore.ConfigurationChanged += ApplyConfiguration;
        ApplyConfiguration(clickyConfigurationStore.CurrentConfiguration);
        SetCompanionStatus(clickyConfigurationStore.CurrentConfiguration.IsClickyCursorAlwaysVisible, CompanionVoiceState.Idle);
    }

    /// <summary>Updates the header status dot and text. Called by the app as companion state changes.</summary>
    public void SetCompanionStatus(bool isCursorOverlayVisible, CompanionVoiceState voiceState)
    {
        StatusTextBlock.Text = CompanionPanelText.GetStatusText(isCursorOverlayVisible, voiceState);

        string statusDotBrushKey = CompanionPanelText.GetStatusDotColor(isCursorOverlayVisible, voiceState) switch
        {
            CompanionStatusDotColor.Ready => "DS.Success",
            CompanionStatusDotColor.Busy => "DS.Blue400",
            _ => "DS.TextTertiary"
        };
        SolidColorBrush statusDotBrush = (SolidColorBrush)FindResource(statusDotBrushKey);
        StatusDot.Fill = statusDotBrush;
        StatusDotGlow.Color = statusDotBrush.Color;
    }

    private readonly List<(BackendRoutingPickerRow PickerRow, System.Windows.Controls.Primitives.ToggleButton SegmentButton, ChatBackendKind? Option)> backendSegmentButtons = [];

    /// <summary>
    /// One labeled segmented control per picker row. Each segment is a ToggleButton whose checked
    /// state is driven from the config (in ApplyConfiguration), never left to the button itself.
    /// </summary>
    private void BuildBackendPickers()
    {
        Style segmentButtonStyle = (Style)FindResource("DS.SegmentButton");

        foreach (BackendRoutingPickerRow pickerRow in BackendRoutingPickerRows.All)
        {
            System.Windows.Controls.Grid pickerRowGrid = new() { Margin = new Thickness(0, 0, 0, 6) };
            pickerRowGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(64) });
            pickerRowGrid.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            pickerRowGrid.Children.Add(new System.Windows.Controls.TextBlock
            {
                Text = pickerRow.Title,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)FindResource("DS.TextSecondary")
            });

            System.Windows.Controls.Primitives.UniformGrid segmentsGrid = new() { Rows = 1 };
            foreach (ChatBackendKind? option in pickerRow.Options)
            {
                System.Windows.Controls.Primitives.ToggleButton segmentButton = new()
                {
                    Content = BackendRoutingPickerRows.GetOptionDisplayName(option),
                    Style = segmentButtonStyle
                };
                segmentButton.Click += (_, _) => clickyConfigurationStore.UpdateAndSave(configuration => pickerRow.SetSelectedBackend(configuration, option));
                segmentsGrid.Children.Add(segmentButton);
                backendSegmentButtons.Add((pickerRow, segmentButton, option));
            }

            System.Windows.Controls.Border segmentsTrack = new()
            {
                Background = (Brush)FindResource("DS.Surface1"),
                CornerRadius = (CornerRadius)FindResource("DS.CornerRadius.Small"),
                Padding = new Thickness(2),
                Child = segmentsGrid
            };
            System.Windows.Controls.Grid.SetColumn(segmentsTrack, 1);
            pickerRowGrid.Children.Add(segmentsTrack);

            BackendPickersSection.Children.Add(pickerRowGrid);
        }
    }

    private void HandleOpenConfigurationFileButtonClick(object sender, RoutedEventArgs routedEventArgs)
    {
        HidePanel();
        BackendRoutingPickerRows.OpenConfigurationFileInEditor(clickyConfigurationStore.ConfigurationFilePath);
    }

    private void ApplyConfiguration(ClickyConfiguration clickyConfiguration)
    {
        foreach ((BackendRoutingPickerRow pickerRow, System.Windows.Controls.Primitives.ToggleButton segmentButton, ChatBackendKind? option) in backendSegmentButtons)
        {
            segmentButton.IsChecked = pickerRow.GetSelectedBackend(clickyConfiguration) == option;
        }

        PushToTalkShortcutRun.Text = CompanionPanelText.FormatEffectiveShortcutForDisplay(clickyConfiguration.PushToTalkShortcut);
        string? shortcutNote = CompanionPanelText.GetShortcutNote(clickyConfiguration.PushToTalkShortcut);
        ShortcutNoteTextBlock.Text = shortcutNote ?? "";
        ShortcutNoteTextBlock.Visibility = shortcutNote == null ? Visibility.Collapsed : Visibility.Visible;

        ShowClickyToggle.IsChecked = clickyConfiguration.IsClickyCursorAlwaysVisible;
        ShowClickyDescriptionTextBlock.Text = clickyConfiguration.IsClickyCursorAlwaysVisible
            ? "The blue cursor stays on screen."
            : "The cursor only appears while you talk to Clicky.";
    }

    private void HandleShowClickyToggleClick(object sender, RoutedEventArgs routedEventArgs)
    {
        bool isClickyCursorAlwaysVisible = ShowClickyToggle.IsChecked == true;
        clickyConfigurationStore.UpdateAndSave(configuration => configuration.IsClickyCursorAlwaysVisible = isClickyCursorAlwaysVisible);
    }

    private void HandleCloseButtonClick(object sender, RoutedEventArgs routedEventArgs) => HidePanel();

    private void HandleQuitButtonClick(object sender, RoutedEventArgs routedEventArgs)
    {
        HidePanel();
        QuitRequested?.Invoke();
    }

    /// <summary>Toggles the panel from a tray-icon click at <paramref name="trayClickPhysicalPoint"/>.</summary>
    public void TogglePanel(System.Drawing.Point trayClickPhysicalPoint)
    {
        if (IsVisible)
        {
            HidePanel();
            return;
        }

        if (DateTime.UtcNow - lastOutsideClickHideTimeUtc < TrayClickIgnoreWindowAfterOutsideClickHide)
        {
            return;
        }

        Show();
        UpdateLayout();
        PositionNextToTaskbar(trayClickPhysicalPoint);
        StartWatchingForDismissal();
    }

    public void HidePanel()
    {
        StopWatchingForDismissal();
        Hide();
    }

    private void HandleSourceInitialized(object? sender, EventArgs eventArgs)
    {
        panelWindowHandle = new WindowInteropHelper(this).Handle;

        // NOACTIVATE: clicks don't pull focus from the user's app. TOOLWINDOW: no Alt+Tab entry.
        int extendedWindowStyle = GetWindowLong(panelWindowHandle, GWL_EXSTYLE);
        SetWindowLong(panelWindowHandle, GWL_EXSTYLE, extendedWindowStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);

        panelHwndSource = HwndSource.FromHwnd(panelWindowHandle);
        panelHwndSource.AddHook(HandlePanelWindowMessage);
    }

    /// <summary>
    /// Places the window in physical pixels on the monitor that was clicked. The window is the
    /// card plus a transparent shadow margin, so the gap passed to the placement math is the
    /// visible gap minus that margin (it can be negative: the shadow may overlap the taskbar).
    /// </summary>
    private void PositionNextToTaskbar(System.Drawing.Point trayClickPhysicalPoint)
    {
        IntPtr monitorHandle = MonitorFromPoint(new NativePoint { X = trayClickPhysicalPoint.X, Y = trayClickPhysicalPoint.Y }, MONITOR_DEFAULTTONEAREST);
        MonitorInfo monitorInfo = new() { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitorHandle, ref monitorInfo))
        {
            return;
        }

        double monitorDpiScale = GetDpiForMonitor(monitorHandle, MDT_EFFECTIVE_DPI, out uint monitorDpiX, out _) == 0
            ? monitorDpiX / 96.0
            : 1.0;

        PixelRect monitorBounds = monitorInfo.MonitorRect.ToPixelRect();
        PixelRect workArea = monitorInfo.WorkRect.ToPixelRect();
        TaskbarEdge taskbarEdge = CompanionPanelPlacement.DetectTaskbarEdge(monitorBounds, workArea, trayClickPhysicalPoint.X, trayClickPhysicalPoint.Y);

        int panelWidthPixels = (int)Math.Ceiling(ActualWidth * monitorDpiScale);
        int panelHeightPixels = (int)Math.Ceiling(ActualHeight * monitorDpiScale);
        int gapPixels = (int)Math.Round((CardGapFromTaskbarDips - PanelCard.Margin.Left) * monitorDpiScale);

        (int panelLeft, int panelTop) = CompanionPanelPlacement.CalculatePanelTopLeft(
            workArea, taskbarEdge, trayClickPhysicalPoint.X, trayClickPhysicalPoint.Y, panelWidthPixels, panelHeightPixels, gapPixels);

        SetWindowPos(panelWindowHandle, HWND_TOPMOST, panelLeft, panelTop, 0, 0, SWP_NOSIZE | SWP_NOACTIVATE | SWP_SHOWWINDOW);
    }

    private void StartWatchingForDismissal()
    {
        if (mouseHookHandle == IntPtr.Zero)
        {
            mouseHookCallback = HandleLowLevelMouseEvent;
            mouseHookHandle = SetWindowsHookEx(WH_MOUSE_LL, mouseHookCallback, GetModuleHandle(null), 0);
        }

        // Registered only while the panel is open, so Escape behaves normally the rest of the time.
        RegisterHotKey(panelWindowHandle, EscapeHotKeyId, MOD_NOREPEAT, VK_ESCAPE);
    }

    private void StopWatchingForDismissal()
    {
        if (mouseHookHandle != IntPtr.Zero)
        {
            UnhookWindowsHookEx(mouseHookHandle);
            mouseHookHandle = IntPtr.Zero;
            mouseHookCallback = null;
        }

        UnregisterHotKey(panelWindowHandle, EscapeHotKeyId);
    }

    private IntPtr HandleLowLevelMouseEvent(int hookCode, IntPtr mouseMessage, IntPtr mouseHookStructPointer)
    {
        int message = mouseMessage.ToInt32();
        bool isButtonDown = message is WM_LBUTTONDOWN or WM_RBUTTONDOWN or WM_MBUTTONDOWN;
        if (hookCode >= 0 && isButtonDown)
        {
            NativePoint clickPoint = Marshal.PtrToStructure<LowLevelMouseHookStruct>(mouseHookStructPointer).Point;
            if (GetWindowRect(panelWindowHandle, out NativeRect panelRect) && !panelRect.Contains(clickPoint))
            {
                // Never do real work inside a low-level hook (Windows drops slow hooks); hide on the dispatcher.
                Dispatcher.BeginInvoke(() =>
                {
                    lastOutsideClickHideTimeUtc = DateTime.UtcNow;
                    HidePanel();
                });
            }
        }

        return CallNextHookEx(IntPtr.Zero, hookCode, mouseMessage, mouseHookStructPointer);
    }

    private IntPtr HandlePanelWindowMessage(IntPtr windowHandle, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == WM_HOTKEY && wParam.ToInt32() == EscapeHotKeyId)
        {
            HidePanel();
            handled = true;
        }

        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs eventArgs)
    {
        StopWatchingForDismissal();
        panelHwndSource?.RemoveHook(HandlePanelWindowMessage);
        clickyConfigurationStore.ConfigurationChanged -= ApplyConfiguration;
        base.OnClosed(eventArgs);
    }

    // ---- Win32 interop ----

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WH_MOUSE_LL = 14;
    private const int WM_LBUTTONDOWN = 0x0201;
    private const int WM_RBUTTONDOWN = 0x0204;
    private const int WM_MBUTTONDOWN = 0x0207;
    private const int WM_HOTKEY = 0x0312;
    private const uint MOD_NOREPEAT = 0x4000;
    private const uint VK_ESCAPE = 0x1B;
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const int MDT_EFFECTIVE_DPI = 0;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private static readonly IntPtr HWND_TOPMOST = new(-1);

    private delegate IntPtr LowLevelMouseProc(int hookCode, IntPtr mouseMessage, IntPtr mouseHookStructPointer);

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly bool Contains(NativePoint point) => point.X >= Left && point.X < Right && point.Y >= Top && point.Y < Bottom;

        public readonly PixelRect ToPixelRect() => new(Left, Top, Right, Bottom);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public int Size;
        public NativeRect MonitorRect;
        public NativeRect WorkRect;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct LowLevelMouseHookStruct
    {
        public NativePoint Point;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public IntPtr ExtraInfo;
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
    private static extern int GetWindowLong(IntPtr windowHandle, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
    private static extern int SetWindowLong(IntPtr windowHandle, int index, int newValue);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr windowHandle, IntPtr insertAfter, int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr windowHandle, out NativeRect windowRect);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(NativePoint point, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfo(IntPtr monitorHandle, ref MonitorInfo monitorInfo);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr monitorHandle, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int hookId, LowLevelMouseProc callback, IntPtr moduleHandle, uint threadId);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hookHandle);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hookHandle, int hookCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RegisterHotKey(IntPtr windowHandle, int hotKeyId, uint modifiers, uint virtualKey);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnregisterHotKey(IntPtr windowHandle, int hotKeyId);
}

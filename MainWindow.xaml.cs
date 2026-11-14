// Copyright (c) 2026 Cloudflare, Inc.
// Licensed under the Apache 2.0 license found in the LICENSE file or at:
// https://opensource.org/licenses/Apache-2.0

using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Kitesurf.Windows.Core;
using Kitesurf.Windows.Interop;
using Kitesurf.Windows.ViewModels;
using Microsoft.Win32;

namespace Kitesurf.Windows;

public partial class MainWindow : Window
{
    private readonly AppSettings _settings;
    private readonly MainViewModel _viewModel;
    private bool _addressEditing;
    private bool _shutdownStarted;

    public MainWindow()
    {
        InitializeComponent();

        _settings = SettingsStore.Load();
        _viewModel = new MainViewModel(Dispatcher, _settings);
        DataContext = _viewModel;

        _viewModel.PropertyChanged += ViewModel_PropertyChanged;
        _viewModel.ToastRequested += ShowToast;
        _viewModel.SurfaceSizeProvider = CurrentViewport;

        Surface.SetRenderScale(RenderScale);

        Loaded += MainWindow_Loaded;
        Closing += MainWindow_Closing;
        StateChanged += (_, _) => UpdateMaximizeGlyph();
        PreviewKeyDown += MainWindow_PreviewKeyDown;
        SourceInitialized += (_, _) => WindowEffects.Apply(this);
    }

    private double RenderScale
    {
        get
        {
            var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
            return _settings.RenderScale > 0 ? _settings.RenderScale : Math.Clamp(dpi, 1, 2);
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        if (!double.IsNaN(_settings.WindowWidth) && _settings.WindowWidth > 700)
        {
            Width = _settings.WindowWidth;
            Height = _settings.WindowHeight;
            if (!double.IsNaN(_settings.WindowLeft))
            {
                Left = _settings.WindowLeft;
                Top = _settings.WindowTop;
            }
        }

        if (_settings.WindowMaximized) WindowState = WindowState.Maximized;

        ClampToWorkArea();
        Surface.SetRenderScale(RenderScale);

        if (!string.IsNullOrWhiteSpace(_settings.LastEngineAddress))
        {
            var label = Uri.TryCreate(_settings.LastEngineAddress, UriKind.Absolute, out var uri) ? uri.Authority : "Engine";
            _ = _viewModel.ConnectAsync(new EngineProfile(label, _settings.LastEngineAddress));
        }

        await Task.Yield();
        AddressBox.Focus();

        // --demo starts the built-in synthetic engine and opens it: a smoke test without Cloudflare.
        if (Environment.GetCommandLineArgs().Any(argument => argument.Equals("--demo", StringComparison.OrdinalIgnoreCase)))
            await _viewModel.StartDemoEngineAsync();
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(MainViewModel.SelectedTab)) return;

        Surface.Tab = _viewModel.SelectedTab;
        if (_viewModel.SelectedTab is not null) Surface.Focus();
    }

    private void ClampToWorkArea()
    {
        var work = WindowEffects.GetWorkArea(this);
        System.IO.File.AppendAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kitesurf-startup.log"),
            $"dpi={VisualTreeHelper.GetDpi(this).DpiScaleX} work={work} before={Width}x{Height} state={WindowState}{Environment.NewLine}");
        if (work.Width < 200 || work.Height < 200) return;

        Width = Math.Clamp(Width, MinWidth, Math.Max(MinWidth, work.Width - 24));
        Height = Math.Clamp(Height, MinHeight, Math.Max(MinHeight, work.Height - 24));

        var left = double.IsNaN(Left) ? work.Left + (work.Width - Width) / 2 : Left;
        var top = double.IsNaN(Top) ? work.Top + (work.Height - Height) / 2 : Top;
        Left = Math.Clamp(left, work.Left, Math.Max(work.Left, work.Right - Width));
        Top = Math.Clamp(top, work.Top, Math.Max(work.Top, work.Bottom - Height));
        System.IO.File.AppendAllText(
            System.IO.Path.Combine(System.IO.Path.GetTempPath(), "kitesurf-startup.log"),
            $"after={Width}x{Height} at {Left},{Top}{Environment.NewLine}");
    }

    private (int Width, int Height) CurrentViewport()
    {
        var scale = RenderScale;
        var surfaceWidth = Surface.ActualWidth;
        var surfaceHeight = Surface.ActualHeight;

        if (surfaceWidth < 64 || surfaceHeight < 64)
        {
            var consoleWidth = _viewModel.ConsoleOpen ? _viewModel.ConsoleWidth + 6 : 0;
            surfaceWidth = Math.Max(320, ActualWidth - 2 - consoleWidth);
            surfaceHeight = Math.Max(240, ActualHeight - 46 - 52 - 28 - 2);
        }

        return ((int)Math.Round(surfaceWidth * scale), (int)Math.Round(surfaceHeight * scale));
    }

    private void MainWindow_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

        switch (e.Key)
        {
            case Key.T when ctrl:
                _viewModel.NewTabCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.W when ctrl:
                if (_viewModel.SelectedTab is { } tab) _ = _viewModel.CloseTabAsync(tab);
                e.Handled = true;
                break;

            case Key.L when ctrl:
                AddressBox.Focus();
                AddressBox.SelectAll();
                e.Handled = true;
                break;

            case Key.R when ctrl:
            case Key.F5:
                _viewModel.ReloadCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.S when ctrl && shift:
                _viewModel.CopyFrameCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.OemTilde when ctrl:
                _viewModel.ToggleConsoleCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Left when alt:
                _viewModel.BackCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.Right when alt:
                _viewModel.ForwardCommand.Execute(null);
                e.Handled = true;
                break;

            case Key.F12:
                OpenDevTools();
                e.Handled = true;
                break;

            case Key.I when ctrl && shift:
                OpenDevTools();
                e.Handled = true;
                break;

            case Key.Tab when ctrl:
                CycleTab(shift ? -1 : 1);
                e.Handled = true;
                break;

            case Key.Escape when _addressEditing:
                AddressBox.Text = _viewModel.SelectedTab?.Url ?? string.Empty;
                _viewModel.Address = AddressBox.Text;
                Surface.Focus();
                e.Handled = true;
                break;
        }
    }

    private void CycleTab(int delta)
    {
        if (_viewModel.Tabs.Count < 2) return;
        var index = _viewModel.SelectedTab is null ? 0 : _viewModel.Tabs.IndexOf(_viewModel.SelectedTab);
        var next = (index + delta + _viewModel.Tabs.Count) % _viewModel.Tabs.Count;
        _viewModel.SelectedTab = _viewModel.Tabs[next];
    }

    private void UpdateMaximizeGlyph()
    {
        if (MaximizeGlyph is null) return;
        MaximizeGlyph.Text = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Maximize_Click(object sender, RoutedEventArgs e) =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void TabChip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: TabViewModel tab }) _viewModel.SelectedTab = tab;
    }

    private void TabChip_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        // Selection already happened on press; nothing else to do on release.
    }

    private void TabChip_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (sender is FrameworkElement { DataContext: TabViewModel tab }) _ = _viewModel.CloseTabAsync(tab);
        e.Handled = true;
    }

    private void TabScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        TabScroll.ScrollToHorizontalOffset(TabScroll.HorizontalOffset - e.Delta);
        e.Handled = true;
    }

    private void ConsoleSplitter_DragDelta(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        _viewModel.ConsoleWidth -= e.HorizontalChange;
    }

    private void AddressBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _addressEditing = true;
        Dispatcher.BeginInvoke(new Action(() => AddressBox.SelectAll()), DispatcherPriority.Input);
    }

    private void AddressBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
    {
        _addressEditing = false;
        if (_viewModel.SelectedTab?.Url is { Length: > 0 } url) _viewModel.Address = url;
    }

    private void AddressBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (AddressBox.IsKeyboardFocusWithin) return;
        AddressBox.Focus();
        e.Handled = true;
    }

    private void AddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        var target = AddressBox.Text;
        e.Handled = true;
        Surface.Focus();
        _ = _viewModel.NavigateAsync(target);
    }

    private async void Screenshot_Click(object sender, RoutedEventArgs e)
    {
        var tab = _viewModel.SelectedTab;
        if (tab is null)
        {
            ShowToast("Open a page first.");
            return;
        }

        var bytes = await tab.CaptureScreenshotAsync();
        if (bytes is null or { Length: 0 })
        {
            ShowToast("The engine returned no image.");
            return;
        }

        var host = Uri.TryCreate(tab.Url, UriKind.Absolute, out var uri) ? uri.Host : "page";
        var dialog = new SaveFileDialog
        {
            Title = "Save page screenshot",
            Filter = "PNG image (*.png)|*.png",
            FileName = $"kitesurf-{host}-{DateTime.Now:yyyyMMdd-HHmmss}.png",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        };

        if (dialog.ShowDialog(this) != true) return;

        try
        {
            await File.WriteAllBytesAsync(dialog.FileName, bytes);
            ShowToast("Screenshot saved to " + dialog.FileName);
        }
        catch (Exception error)
        {
            ShowToast("Could not save the screenshot: " + error.Message);
        }
    }

    private void DevTools_Click(object sender, RoutedEventArgs e) => OpenDevTools();

    private void OpenDevTools()
    {
        var endpoint = _viewModel.Endpoint;
        if (endpoint is null)
        {
            ShowToast("Connect an engine before opening DevTools.");
            return;
        }

        var url = endpoint.InspectorUrl(_viewModel.SelectedTab?.Url);
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            ShowToast("DevTools opened in your system browser.");
        }
        catch (Exception error)
        {
            ShowToast("Could not open DevTools: " + error.Message);
        }
    }

    private void ShowToast(string message)
    {
        ToastText.Text = message;
        Toast.Visibility = Visibility.Visible;

        var timeline = new DoubleAnimationUsingKeyFrames { FillBehavior = FillBehavior.Stop };
        timeline.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        timeline.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(140))));
        timeline.KeyFrames.Add(new LinearDoubleKeyFrame(1, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2600))));
        timeline.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(2950))));
        timeline.Completed += (_, _) =>
        {
            Toast.Visibility = Visibility.Collapsed;
            Toast.Opacity = 0;
        };

        Toast.BeginAnimation(OpacityProperty, timeline);
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        SaveSettings();

        if (_shutdownStarted) return;

        e.Cancel = true;
        _shutdownStarted = true;
        _ = ShutdownAsync();
    }

    private async Task ShutdownAsync()
    {
        try
        {
            if (_viewModel.IsDemo) await Task.CompletedTask;
            await _viewModel.ShutdownAsync();
        }
        catch (Exception)
        {
            // Shutting down anyway.
        }

        await Dispatcher.InvokeAsync(Close);
    }

    private void SaveSettings()
    {
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, Width, Height)
            : RestoreBounds;

        if (!bounds.IsEmpty && bounds.Width > 200)
        {
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
        }

        _settings.ConsoleOpen = _viewModel.ConsoleOpen;
        _settings.ConsoleWidth = _viewModel.ConsoleWidth;
        _settings.Engines = _viewModel.SavedEngines.ToList();
        SettingsStore.Save(_settings);
    }
}

// Copyright (c) 2026 Cloudflare, Inc.
// Licensed under the Apache 2.0 license found in the LICENSE file or at:
// https://opensource.org/licenses/Apache-2.0

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kitesurf.Windows.Core;
using Kitesurf.Windows.ViewModels;

namespace Kitesurf.Windows.Controls;

/// <summary>
/// The page viewport: paints screencast frames and forwards Windows input into the engine's CDP
/// coordinates. The engine renders exactly the box requested here, so mapping stays exact.
/// </summary>
public sealed class PageSurface : Grid
{
    private static readonly Brush BackgroundBrush = new SolidColorBrush(Color.FromRgb(0x0A, 0x0D, 0x12));

    private readonly Image _image = new()
    {
        Stretch = Stretch.Fill,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
    };

    private readonly TextBlock _overlayText = new()
    {
        Foreground = new SolidColorBrush(Color.FromRgb(0x98, 0xA6, 0xBA)),
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI"),
        FontSize = 13,
        TextAlignment = TextAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        MaxWidth = 460,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly Border _overlay;

    private readonly DispatcherTimer _resizeTimer;
    private TabViewModel? _tab;
    private double _frameWidth = 1;
    private double _frameHeight = 1;
    private double _renderScale = 1;

    public PageSurface()
    {
        Background = BackgroundBrush;
        Focusable = true;
        ClipToBounds = true;
        KeyboardNavigation.SetIsTabStop(this, true);

        _overlay = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(0xCC, 0x0A, 0x0D, 0x12)),
            Child = _overlayText,
            Visibility = Visibility.Collapsed,
        };

        Children.Add(_image);
        Children.Add(_overlay);

        _resizeTimer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(160),
        };
        _resizeTimer.Tick += (_, _) => FlushResize();

        SizeChanged += (_, _) =>
        {
            _resizeTimer.Stop();
            _resizeTimer.Start();
        };

        Loaded += (_, _) => FlushResize();
    }

    public TabViewModel? Tab
    {
        get => _tab;
        set
        {
            if (ReferenceEquals(_tab, value)) return;

            if (_tab is not null)
            {
                _tab.FrameRendered -= OnFrameRendered;
                _tab.PropertyChanged -= OnTabPropertyChanged;
            }

            _tab = value;

            if (_tab is not null)
            {
                _tab.FrameRendered += OnFrameRendered;
                _tab.PropertyChanged += OnTabPropertyChanged;
                ShowFrame(_tab.Frame);
                UpdateOverlay();
                FlushResize();
            }
            else
            {
                _image.Source = null;
                _overlayText.Text = string.Empty;
                _overlay.Visibility = Visibility.Collapsed;
            }
        }
    }

    /// <summary>Called by the window when it knows the monitor DPI.</summary>
    public void SetRenderScale(double scale) => _renderScale = Math.Clamp(scale, 1, 2);

    private void OnTabPropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(TabViewModel.Overlay) or nameof(TabViewModel.HasOverlay)) UpdateOverlay();
    }

    private void UpdateOverlay()
    {
        if (_tab is null)
        {
            _overlayText.Text = string.Empty;
            _overlay.Visibility = Visibility.Collapsed;
            return;
        }

        if (_tab.HasOverlay)
        {
            _overlayText.Text = _tab.Overlay;
            _overlay.Visibility = Visibility.Visible;
            return;
        }

        if (_tab.Frame is null && !_tab.IsConnected)
        {
            _overlayText.Text = "Connecting to the engine…";
            _overlay.Visibility = Visibility.Visible;
            return;
        }

        _overlay.Visibility = Visibility.Collapsed;
    }

    private void OnFrameRendered(TabViewModel tab)
    {
        if (!ReferenceEquals(tab, _tab)) return;
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => OnFrameRendered(tab), DispatcherPriority.Render);
            return;
        }

        ShowFrame(tab.Frame);
        UpdateOverlay();
    }

    private void ShowFrame(BitmapSource? frame)
    {
        if (frame is null) return;
        _image.Source = frame;
        _frameWidth = Math.Max(1, frame.PixelWidth);
        _frameHeight = Math.Max(1, frame.PixelHeight);
    }

    private void FlushResize()
    {
        if (_tab is null) return;

        var scale = _renderScale > 0 ? _renderScale : VisualTreeHelper.GetDpi(this).DpiScaleX;
        var width = (int)Math.Round(Math.Max(320, ActualWidth) * scale);
        var height = (int)Math.Round(Math.Max(240, ActualHeight) * scale);
        _ = _tab.ResizeAsync(width, height);
    }

    private (double X, double Y) MapPoint(Point point) =>
        ViewportMapping.ToPage(point, ActualWidth, ActualHeight, (int)_frameWidth, (int)_frameHeight);

    private static string ButtonName(MouseButton button) => button switch
    {
        MouseButton.Left => "left",
        MouseButton.Right => "right",
        MouseButton.Middle => "middle",
        _ => "none",
    };

    private static int ButtonMask(MouseButton button) => button switch
    {
        MouseButton.Left => 1,
        MouseButton.Right => 2,
        MouseButton.Middle => 4,
        _ => 0,
    };

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (_tab is null) return;

        var (x, y) = MapPoint(e.GetPosition(this));
        var mask = e.LeftButton == MouseButtonState.Pressed ? 1 : 0;
        if (e.RightButton == MouseButtonState.Pressed) mask |= 2;
        if (e.MiddleButton == MouseButtonState.Pressed) mask |= 4;

        _ = _tab.SendMouseAsync("mouseMoved", x, y, "none", mask, 0, KeyboardMap.Modifiers(Keyboard.Modifiers));
    }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        base.OnMouseDown(e);
        if (_tab is null) return;

        Focus();
        Keyboard.Focus(this);

        var (x, y) = MapPoint(e.GetPosition(this));
        _ = _tab.SendMouseAsync("mousePressed", x, y, ButtonName(e.ChangedButton), ButtonMask(e.ChangedButton),
            e.ClickCount, KeyboardMap.Modifiers(Keyboard.Modifiers));
        e.Handled = true;
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        base.OnMouseUp(e);
        if (_tab is null) return;

        var (x, y) = MapPoint(e.GetPosition(this));
        _ = _tab.SendMouseAsync("mouseReleased", x, y, ButtonName(e.ChangedButton), 0, e.ClickCount,
            KeyboardMap.Modifiers(Keyboard.Modifiers));
        e.Handled = true;
    }

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        base.OnMouseWheel(e);
        if (_tab is null) return;

        var (x, y) = MapPoint(e.GetPosition(this));
        _ = _tab.SendMouseAsync("mouseWheel", x, y, "none", 0, 0, KeyboardMap.Modifiers(Keyboard.Modifiers),
            deltaX: 0, deltaY: -e.Delta);
        e.Handled = true;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        base.OnPreviewKeyDown(e);
        if (_tab is null) return;

        var modifiers = KeyboardMap.Modifiers(Keyboard.Modifiers);
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

        if (!KeyboardMap.TryGet(e.Key, out var descriptor)) return;

        // Text keys arrive through TextInput; everything else is a discrete key press.
        if (KeyboardMap.IsEditingKey(e.Key) || control || alt)
        {
            _ = _tab.SendKeyAsync("rawKeyDown", descriptor, modifiers, null, e.IsRepeat);
            e.Handled = true;
        }
    }

    protected override void OnPreviewKeyUp(KeyEventArgs e)
    {
        base.OnPreviewKeyUp(e);
        if (_tab is null) return;
        if (!KeyboardMap.TryGet(e.Key, out var descriptor)) return;

        var modifiers = KeyboardMap.Modifiers(Keyboard.Modifiers);
        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;

        if (KeyboardMap.IsEditingKey(e.Key) || control || alt)
        {
            _ = _tab.SendKeyAsync("keyUp", descriptor, modifiers, null);
            e.Handled = true;
        }
    }

    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        base.OnTextInput(e);
        if (_tab is null || string.IsNullOrEmpty(e.Text)) return;

        var control = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        var alt = (Keyboard.Modifiers & ModifierKeys.Alt) != 0;
        if (control || alt) return;

        var modifiers = KeyboardMap.Modifiers(Keyboard.Modifiers);

        foreach (var character in e.Text)
        {
            if (char.IsControl(character) && character is not '\r' and not '\t') continue;

            if (!KeyboardMap.TryGetTyped(character, out var descriptor))
                descriptor = new KeyDescriptor(character.ToString(), string.Empty, 0);

            _ = _tab.SendKeyAsync("keyDown", descriptor, modifiers, character.ToString());
            _ = _tab.SendKeyAsync("keyUp", descriptor, modifiers, null);
        }

        e.Handled = true;
    }
}

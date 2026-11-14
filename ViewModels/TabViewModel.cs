// Copyright (c) 2026 Cloudflare, Inc.
// Licensed under the Apache 2.0 license found in the LICENSE file or at:
// https://opensource.org/licenses/Apache-2.0

using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Kitesurf.Windows.Core;

namespace Kitesurf.Windows.ViewModels;

/// <summary>One browser tab: a page session plus everything the shell shows about it.</summary>
public sealed class TabViewModel : ObservableObject, IAsyncDisposable
{
    private const int ConsoleCapacity = 400;
    private const int MaxReconnectAttempts = 6;

    private readonly Dispatcher _dispatcher;
    private PageSession? _session;
    private string _title = "New tab";
    private string _url = string.Empty;
    private bool _isLoading;
    private bool _isActive;
    private bool _canGoBack;
    private bool _canGoForward;
    private bool _isConnected;
    private BitmapSource? _frame;
    private int _frameWidth;
    private int _frameHeight;
    private string? _overlay;
    private int _pendingWidth = 1280;
    private int _pendingHeight = 800;
    private int _lastAppliedWidth;
    private int _lastAppliedHeight;
    private CancellationTokenSource? _loadingTimeout;
    private bool _trafficEnabled;
    private bool _reconnecting;
    private int _reconnectAttempt;
    private bool _disposed;

    public TabViewModel(EngineEndpoint endpoint, string engineLabel, Dispatcher dispatcher, EngineProfile? profile = null)
    {
        Endpoint = endpoint;
        EngineLabel = engineLabel;
        _dispatcher = dispatcher;
        Profile = profile;
    }

    public EngineEndpoint Endpoint { get; }

    public EngineProfile? Profile { get; }

    public string EngineLabel { get; }

    public ObservableCollection<ConsoleLine> Console { get; } = new();

    public event Action<TabViewModel>? FrameRendered;

    public event Action<TabViewModel>? Changed;

    public string Title
    {
        get => _title;
        private set
        {
            if (Set(ref _title, value)) RaiseChanged();
        }
    }

    public string Url
    {
        get => _url;
        private set
        {
            if (Set(ref _url, value)) RaiseChanged();
        }
    }

    public bool IsLoading
    {
        get => _isLoading;
        private set
        {
            if (Set(ref _isLoading, value)) RaiseChanged();
        }
    }

    public bool IsConnected
    {
        get => _isConnected;
        private set
        {
            if (Set(ref _isConnected, value)) RaiseChanged();
        }
    }

    public bool IsActive
    {
        get => _isActive;
        set => Set(ref _isActive, value);
    }

    public bool CanGoBack
    {
        get => _canGoBack;
        private set => Set(ref _canGoBack, value);
    }

    public bool CanGoForward
    {
        get => _canGoForward;
        private set => Set(ref _canGoForward, value);
    }

    public BitmapSource? Frame
    {
        get => _frame;
        private set => Set(ref _frame, value);
    }

    public int FrameWidth => _frameWidth;

    public int FrameHeight => _frameHeight;

    /// <summary>Overlay text shown instead of the page (connecting, closed, error).</summary>
    public string? Overlay
    {
        get => _overlay;
        private set
        {
            if (Set(ref _overlay, value)) OnPropertyChanged(nameof(HasOverlay));
        }
    }

    public bool HasOverlay => !string.IsNullOrEmpty(_overlay);

    public double Fps { get; private set; }

    public double FrameIntervalMs { get; private set; }

    public bool TrafficEnabled
    {
        get => _trafficEnabled;
        set
        {
            if (!Set(ref _trafficEnabled, value)) return;
            if (_session is not null) _session.TraceTraffic = value;
        }
    }

    public async Task OpenAsync(int width, int height)
    {
        _pendingWidth = Math.Max(320, width);
        _pendingHeight = Math.Max(240, height);

        if (!await OpenSessionAsync())
        {
            Post(() => Title = "Connection failed");
        }
    }

    private async Task<bool> OpenSessionAsync()
    {
        var session = new PageSession(Endpoint, Profile);
        Attach(session);
        _session = session;

        try
        {
            await session.ConnectAsync();
            await session.ResizeAsync(_pendingWidth, _pendingHeight);
            _lastAppliedWidth = _pendingWidth;
            _lastAppliedHeight = _pendingHeight;
            session.TraceTraffic = TrafficEnabled;

            Post(() =>
            {
                IsConnected = true;
                Overlay = null;
                _reconnectAttempt = 0;
                RaiseChanged();
            });
            return true;
        }
        catch (Exception error)
        {
            Post(() =>
            {
                IsConnected = false;
                Overlay = error.Message;
                RaiseChanged();
            });

            await session.DisposeAsync();
            if (ReferenceEquals(_session, session)) _session = null;
            return false;
        }
    }

    private void Attach(PageSession session)
    {
        session.FrameArrived += OnFrame;
        session.ConsoleLineArrived += line => Post(() => AppendConsole(line));
        session.UrlChanged += url => Post(() =>
        {
            Url = url;
            RaiseChanged();
        });
        session.TitleChanged += title => Post(() => Title = title);
        session.LoadingChanged += loading => Post(() =>
        {
            IsLoading = loading;
            if (!loading)
            {
                StopLoadingTimeout();
                _ = RefreshHistoryAsync();
            }
        });
        session.SessionClosed += reason => Post(() => OnSessionClosed(reason));
        session.TrafficObserved += (direction, payload) =>
        {
            if (!TrafficEnabled) return;
            Post(() => AppendConsole(new ConsoleLine(DateTimeOffset.Now, direction, payload)));
        };
    }

    public async Task ResizeAsync(int width, int height)
    {
        _pendingWidth = Math.Max(320, width);
        _pendingHeight = Math.Max(240, height);

        if (_session is null || !_session.IsConnected) return;
        if (Math.Abs(_lastAppliedWidth - _pendingWidth) < 2 && Math.Abs(_lastAppliedHeight - _pendingHeight) < 2) return;

        _lastAppliedWidth = _pendingWidth;
        _lastAppliedHeight = _pendingHeight;

        try
        {
            await _session.ResizeAsync(_pendingWidth, _pendingHeight);
        }
        catch (Exception)
        {
            // Resizing during a navigation is harmless; the next frame carries the new size.
        }
    }

    public async Task NavigateAsync(string url)
    {
        if (_session is null) return;

        IsLoading = true;
        StartLoadingTimeout();
        try
        {
            await _session.NavigateAsync(url);
        }
        catch (Exception error)
        {
            IsLoading = false;
            StopLoadingTimeout();
            Overlay = error.Message;
        }
    }

    /// <summary>An engine that never reports load completion must not leave a spinner forever.</summary>
    private void StartLoadingTimeout()
    {
        StopLoadingTimeout();
        var cancellation = new CancellationTokenSource();
        _loadingTimeout = cancellation;

        _ = Task.Delay(TimeSpan.FromSeconds(20), cancellation.Token).ContinueWith(
            task =>
            {
                if (!task.IsCanceled) Post(() => IsLoading = false);
            },
            TaskScheduler.Default);
    }

    private void StopLoadingTimeout()
    {
        _loadingTimeout?.Cancel();
        _loadingTimeout?.Dispose();
        _loadingTimeout = null;
    }

    public async Task<bool> GoBackAsync()
    {
        if (_session is null) return false;
        try { return await _session.GoBackAsync(); }
        catch (Exception) { return false; }
    }

    public async Task<bool> GoForwardAsync()
    {
        if (_session is null) return false;
        try { return await _session.GoForwardAsync(); }
        catch (Exception) { return false; }
    }

    public async Task ReloadAsync()
    {
        if (_session is null || string.IsNullOrEmpty(Url)) return;
        await NavigateAsync(Url);
    }

    public async Task<byte[]?> CaptureScreenshotAsync()
    {
        if (_session is null) return null;
        try { return await _session.CaptureScreenshotAsync(); }
        catch (Exception error)
        {
            AppendConsole(new ConsoleLine(DateTimeOffset.Now, "error", "Screenshot failed: " + error.Message));
            return null;
        }
    }

    /// <summary>Puts the engine's current render on the Windows clipboard as a bitmap.</summary>
    public async Task<bool> CopyFrameToClipboardAsync()
    {
        var bytes = await CaptureScreenshotAsync();
        if (bytes is null || bytes.Length == 0) return false;

        try
        {
            using var stream = new MemoryStream(bytes);
            var image = new BitmapImage();
            image.BeginInit();
            image.CacheOption = BitmapCacheOption.OnLoad;
            image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
            image.StreamSource = stream;
            image.EndInit();
            image.Freeze();

            await _dispatcher.InvokeAsync(() => Clipboard.SetImage(image));
            return true;
        }
        catch (Exception error)
        {
            AppendConsole(new ConsoleLine(DateTimeOffset.Now, "error", "Clipboard copy failed: " + error.Message));
            return false;
        }
    }

    public async Task<JsonElement> EvaluateAsync(string expression)
    {
        if (_session is null) throw new InvalidOperationException("The tab is not connected.");
        return await _session.EvaluateAsync(expression);
    }

    public Task SendMouseAsync(string type, double x, double y, string button, int buttons, int clickCount, int modifiers,
        double deltaX = 0, double deltaY = 0) =>
        _session?.DispatchMouseAsync(type, x, y, button, buttons, clickCount, modifiers, deltaX, deltaY) ?? Task.CompletedTask;

    public Task SendKeyAsync(string type, KeyDescriptor key, int modifiers, string? text, bool autoRepeat = false) =>
        _session?.DispatchKeyAsync(type, key.Key, key.Code, key.VirtualKey, modifiers, text, autoRepeat, key.Location)
        ?? Task.CompletedTask;

    public Task InsertTextAsync(string text) => _session?.InsertTextAsync(text) ?? Task.CompletedTask;

    private void OnFrame(ScreencastFrame frame)
    {
        Post(() =>
        {
            Frame = frame.Image;
            _frameWidth = frame.PixelWidth;
            _frameHeight = frame.PixelHeight;
            Fps = _session?.Fps ?? 0;
            FrameIntervalMs = _session?.FrameIntervalMs ?? 0;
            FrameRendered?.Invoke(this);
        });
    }

    private void OnSessionClosed(string reason)
    {
        if (_disposed) return;

        IsConnected = false;
        IsLoading = false;
        Diagnostics.Log($"page: session closed — {reason}");

        var stale = _session;
        _session = null;
        if (stale is not null) _ = stale.DisposeAsync().AsTask();

        if (!_reconnecting) _ = ReconnectAsync(reason);
        RaiseChanged();
    }

    /// <summary>
    /// Workers can drop a long-lived WebSocket (idle timeout, CPU budget). The tab keeps its URL and
    /// walks 1s → 2s → 4s → 8s → 15s before giving up, so a blip does not cost the user the page.
    /// </summary>
    private async Task ReconnectAsync(string reason)
    {
        if (_disposed || _reconnecting) return;
        _reconnecting = true;
        var target = Url;

        try
        {
            for (var attempt = 1; attempt <= MaxReconnectAttempts && !_disposed; attempt++)
            {
                _reconnectAttempt = attempt;
                var delay = TimeSpan.FromMilliseconds(Math.Min(1000 * Math.Pow(2, attempt - 1), 15000));
                Post(() => Overlay = $"Connection lost — reconnecting in {delay.TotalSeconds:0}s (attempt {attempt}/{MaxReconnectAttempts})…");

                await Task.Delay(delay).ConfigureAwait(false);
                if (_disposed) return;

                Post(() => Overlay = $"Reconnecting (attempt {attempt}/{MaxReconnectAttempts})…");
                if (!await OpenSessionAsync().ConfigureAwait(false)) continue;

                if (!string.IsNullOrEmpty(target)) await NavigateAsync(target).ConfigureAwait(false);
                Post(() =>
                {
                    Overlay = null;
                    AppendConsole(new ConsoleLine(DateTimeOffset.Now, "log", $"Reconnected after {attempt} attempt(s)."));
                    RaiseChanged();
                });
                return;
            }

            Post(() => Overlay = $"Engine unreachable — {reason}. Press Reload to try again.");
        }
        finally
        {
            _reconnecting = false;
        }
    }

    /// <summary>Console output produced by the shell itself (input echoes, evaluation results).</summary>
    public void AddConsole(string level, string text) =>
        Post(() => AppendConsole(new ConsoleLine(DateTimeOffset.Now, level, text)));

    private async Task RefreshHistoryAsync()
    {
        if (_session is null) return;

        var (canGoBack, canGoForward) = await _session.GetHistoryStateAsync();
        Post(() =>
        {
            CanGoBack = canGoBack;
            CanGoForward = canGoForward;
            RaiseChanged();
        });
    }

    private void AppendConsole(ConsoleLine line)
    {
        Console.Add(line);
        while (Console.Count > ConsoleCapacity) Console.RemoveAt(0);
    }

    private void Post(Action action)
    {
        if (_disposed) return;
        if (_dispatcher.CheckAccess()) action();
        else _dispatcher.BeginInvoke(action, DispatcherPriority.Background);
    }

    private void RaiseChanged() => Changed?.Invoke(this);

    public void SetOverlay(string text) => Post(() => Overlay = text);

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        StopLoadingTimeout();

        if (_session is not null)
        {
            await _session.DisposeAsync();
            _session = null;
        }
    }
}

// Copyright (c) 2026 Cloudflare, Inc.
// Licensed under the Apache 2.0 license found in the LICENSE file or at:
// https://opensource.org/licenses/Apache-2.0

using System.IO;
using System.Text.Json;
using System.Windows.Media.Imaging;
using Kitesurf.Windows.Core.Cdp;

namespace Kitesurf.Windows.Core;

public sealed record ScreencastFrame(BitmapSource Image, int PixelWidth, int PixelHeight, double ScrollX, double ScrollY);

public sealed record ConsoleLine(DateTimeOffset Timestamp, string Level, string Text);

/// <summary>
/// One page of a Kitesurf engine, driven over its page-scoped CDP socket. A session owns the
/// screencast stream, so frame pacing and the render viewport both live here.
/// </summary>
public sealed class PageSession : IAsyncDisposable
{
    private const int MaxFrameDimension = 4096;

    private readonly EngineEndpoint _endpoint;
    private readonly EngineProfile? _profile;
    private readonly SemaphoreSlim _viewportLock = new(1, 1);
    private CdpClient? _client;
    private int _viewportWidth = 1280;
    private int _viewportHeight = 800;
    private bool _streaming;
    private int _disposed;
    private string? _currentUrl;
    private string? _currentTitle;
    private bool _loggedFirstFrame;

    public PageSession(EngineEndpoint endpoint, EngineProfile? profile = null)
    {
        _endpoint = endpoint;
        _profile = profile;
    }

    public string? CurrentUrl => _currentUrl;

    public string? CurrentTitle => _currentTitle;

    public bool IsConnected => _client?.IsOpen == true;

    public event Action<ScreencastFrame>? FrameArrived;

    public event Action<ConsoleLine>? ConsoleLineArrived;

    public event Action<string>? UrlChanged;

    public event Action<string>? TitleChanged;

    public event Action<bool>? LoadingChanged;

    public event Action<string>? SessionClosed;

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        var client = new CdpClient(_endpoint.PageSocketUrl, EngineEndpoint.AccessHeaders(_profile));
        client.EventReceived += OnEvent;
        client.Closed += OnClosed;

        await client.ConnectAsync(TimeSpan.FromSeconds(10), cancellationToken).ConfigureAwait(false);
        _client = client;

        Diagnostics.Log($"page: session open {_endpoint.PageSocketUrl} auth={(_profile?.HasAccessToken == true ? "cf-access" : "none")}");

        var ct = cancellationToken;
        await client.SendAsync("Page.enable", null, ct).ConfigureAwait(false);
        await client.SendAsync("Runtime.enable", null, ct).ConfigureAwait(false);
        await TrySendAsync("Page.setLifecycleEventsEnabled", new { enabled = true }, ct).ConfigureAwait(false);
        await TrySendAsync("Network.enable", null, ct).ConfigureAwait(false);

        // The screencast starts with the first real resize, so the engine never renders a default-sized frame.
    }

    /// <summary>Sets the render viewport. The engine renders exactly the screencast box it is given.</summary>
    public async Task ResizeAsync(int width, int height, CancellationToken cancellationToken = default)
    {
        var clampedWidth = Math.Clamp(width, 320, MaxFrameDimension);
        var clampedHeight = Math.Clamp(height, 240, MaxFrameDimension);

        await _viewportLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (clampedWidth == _viewportWidth && clampedHeight == _viewportHeight && _streaming) return;

            _viewportWidth = clampedWidth;
            _viewportHeight = clampedHeight;

            if (IsConnected) await StartScreencastLockedAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _viewportLock.Release();
        }
    }

    private async Task StartScreencastLockedAsync(CancellationToken cancellationToken)
    {
        if (_client is null) return;

        var parameters = new
        {
            format = "jpeg",
            quality = 80,
            maxWidth = _viewportWidth,
            maxHeight = _viewportHeight,
            everyNthFrame = 1,
            minFrameIntervalMs = 16,
            maxFramesInFlight = 2,
        };

        await _client.SendAsync("Page.startScreencast", parameters, cancellationToken).ConfigureAwait(false);
        _streaming = true;
        Diagnostics.Log($"page: startScreencast {_viewportWidth}x{_viewportHeight}");
    }

    public async Task NavigateAsync(string url, CancellationToken cancellationToken = default)
    {
        if (_client is null) throw new InvalidOperationException("The page session is not connected.");

        var result = await _client.SendAsync("Page.navigate", new { url }, cancellationToken).ConfigureAwait(false);
        if (result.ValueKind == JsonValueKind.Object
            && result.TryGetProperty("errorText", out var errorText)
            && errorText.GetString() is { Length: > 0 } message)
        {
            throw new InvalidOperationException(message);
        }

        SetUrl(url);
    }

    public async Task<bool> GoBackAsync(CancellationToken cancellationToken = default) =>
        await MoveHistoryAsync(-1, cancellationToken).ConfigureAwait(false);

    public async Task<bool> GoForwardAsync(CancellationToken cancellationToken = default) =>
        await MoveHistoryAsync(1, cancellationToken).ConfigureAwait(false);

    /// <summary>Whether the engine's session history has an entry on either side of the current one.</summary>
    public async Task<(bool CanGoBack, bool CanGoForward)> GetHistoryStateAsync(CancellationToken cancellationToken = default)
    {
        if (_client is null) return (false, false);

        try
        {
            var history = await _client.SendAsync("Page.getNavigationHistory", null, cancellationToken).ConfigureAwait(false);
            if (!history.TryGetProperty("entries", out var entries) || !history.TryGetProperty("currentIndex", out var index))
                return (false, false);

            var current = index.GetInt32();
            var count = entries.GetArrayLength();
            return (current > 0, current + 1 < count);
        }
        catch (Exception)
        {
            return (false, false);
        }
    }

    private async Task<bool> MoveHistoryAsync(int delta, CancellationToken cancellationToken)
    {
        if (_client is null) return false;

        var history = await _client.SendAsync("Page.getNavigationHistory", null, cancellationToken).ConfigureAwait(false);
        if (!history.TryGetProperty("entries", out var entries) || !history.TryGetProperty("currentIndex", out var index))
            return false;

        var target = index.GetInt32() + delta;
        if (target < 0 || target >= entries.GetArrayLength()) return false;

        var entryId = entries[target].GetProperty("id").GetInt32();
        await _client.SendAsync("Page.navigateToHistoryEntry", new { entryId }, cancellationToken).ConfigureAwait(false);
        return true;
    }

    public async Task<JsonElement> EvaluateAsync(string expression, CancellationToken cancellationToken = default)
    {
        if (_client is null) throw new InvalidOperationException("The page session is not connected.");

        var response = await _client.SendAsync(
            "Runtime.evaluate",
            new { expression, returnByValue = true, awaitPromise = true, userGesture = true },
            cancellationToken).ConfigureAwait(false);

        if (response.TryGetProperty("exceptionDetails", out var details))
        {
            var text = details.TryGetProperty("text", out var detailsText) ? detailsText.GetString() : null;
            var description = details.TryGetProperty("exception", out var exception)
                              && exception.TryGetProperty("description", out var value)
                ? value.GetString()
                : null;
            throw new InvalidOperationException(description ?? text ?? "Evaluation failed.");
        }

        return response.TryGetProperty("result", out var result) ? result : default;
    }

    public async Task<byte[]> CaptureScreenshotAsync(CancellationToken cancellationToken = default)
    {
        if (_client is null) throw new InvalidOperationException("The page session is not connected.");

        var response = await _client.SendAsync(
            "Page.captureScreenshot",
            new { format = "png" },
            cancellationToken).ConfigureAwait(false);

        var data = response.GetProperty("data").GetString() ?? string.Empty;
        return Convert.FromBase64String(data);
    }

    public Task DispatchMouseAsync(
        string type,
        double x,
        double y,
        string button = "none",
        int buttons = 0,
        int clickCount = 0,
        int modifiers = 0,
        double deltaX = 0,
        double deltaY = 0) =>
        SendInputAsync("Input.dispatchMouseEvent", new
        {
            type,
            x,
            y,
            button,
            buttons,
            clickCount,
            modifiers,
            deltaX,
            deltaY,
            pointerType = "mouse",
        });

    public Task DispatchKeyAsync(
        string type,
        string key,
        string code,
        int windowsVirtualKeyCode,
        int modifiers = 0,
        string? text = null,
        bool autoRepeat = false,
        int location = 0) =>
        SendInputAsync("Input.dispatchKeyEvent", new
        {
            type,
            key,
            code,
            windowsVirtualKeyCode,
            nativeVirtualKeyCode = windowsVirtualKeyCode,
            modifiers,
            text,
            unmodifiedText = text,
            autoRepeat,
            location,
        });

    public Task InsertTextAsync(string text) =>
        SendInputAsync("Input.insertText", new { text });

    private async Task SendInputAsync(string method, object parameters)
    {
        if (_client is null || !_client.IsOpen) return;
        try
        {
            await _client.SendAsync(method, parameters, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Input racing a navigation is normal; a dropped keystroke must not surface as a crash.
        }
    }

    private void OnEvent(CdpEvent raised)
    {
        switch (raised.Method)
        {
            case "Page.screencastFrame":
                _ = HandleFrameAsync(raised.Params);
                break;

            case "Page.frameNavigated":
                if (raised.Params.TryGetProperty("frame", out var frame)
                    && (!frame.TryGetProperty("parentId", out var parent) || parent.ValueKind == JsonValueKind.Null)
                    && frame.TryGetProperty("url", out var url))
                {
                    SetUrl(url.GetString() ?? "about:blank");
                }
                break;

            case "Page.frameStartedLoading":
                LoadingChanged?.Invoke(true);
                break;

            case "Page.frameStoppedLoading":
            case "Page.loadEventFired":
                LoadingChanged?.Invoke(false);
                _ = RefreshTitleAsync();
                break;

            case "Runtime.consoleAPICalled":
                ConsoleLineArrived?.Invoke(BuildConsoleLine(raised.Params));
                break;

            case "Runtime.exceptionThrown":
                ConsoleLineArrived?.Invoke(BuildExceptionLine(raised.Params));
                break;
        }
    }

    private async Task HandleFrameAsync(JsonElement parameters)
    {
        if (!parameters.TryGetProperty("data", out var dataProperty)) return;
        var base64 = dataProperty.GetString();
        if (string.IsNullOrEmpty(base64)) return;

        var sessionId = parameters.TryGetProperty("sessionId", out var idProperty) && idProperty.TryGetInt32(out var id) ? id : 0;

        double width = 0, height = 0, scrollX = 0, scrollY = 0;
        if (parameters.TryGetProperty("metadata", out var metadata))
        {
            if (metadata.TryGetProperty("deviceWidth", out var widthProperty)) width = widthProperty.GetDouble();
            if (metadata.TryGetProperty("deviceHeight", out var heightProperty)) height = heightProperty.GetDouble();
            if (metadata.TryGetProperty("scrollOffsetX", out var scrollXProperty)) scrollX = scrollXProperty.GetDouble();
            if (metadata.TryGetProperty("scrollOffsetY", out var scrollYProperty)) scrollY = scrollYProperty.GetDouble();
        }

        BitmapSource image;
        try
        {
            var bytes = Convert.FromBase64String(base64);
            image = await Task.Run(() => Decode(bytes)).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A truncated frame is not worth a dialog; the next one supersedes it.
            await AckFrameAsync(sessionId).ConfigureAwait(false);
            return;
        }

        FrameArrived?.Invoke(new ScreencastFrame(image, image.PixelWidth, image.PixelHeight, scrollX, scrollY));
        if (!_loggedFirstFrame)
        {
            _loggedFirstFrame = true;
            Diagnostics.Log($"page: first frame {image.PixelWidth}x{image.PixelHeight}");
        }

        await AckFrameAsync(sessionId).ConfigureAwait(false);
    }

    private static BitmapSource Decode(byte[] jpeg)
    {
        using var stream = new MemoryStream(jpeg);
        var image = new BitmapImage();
        image.BeginInit();
        image.CacheOption = BitmapCacheOption.OnLoad;
        image.CreateOptions = BitmapCreateOptions.IgnoreColorProfile;
        image.StreamSource = stream;
        image.EndInit();
        image.Freeze();
        return image;
    }

    private async Task AckFrameAsync(int sessionId)
    {
        if (_client is null || !_client.IsOpen || sessionId == 0) return;
        try
        {
            await _client.SendAsync("Page.screencastFrameAck", new { sessionId }, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // The session is going away.
        }
    }

    private async Task RefreshTitleAsync()
    {
        try
        {
            var result = await EvaluateAsync("document.title").ConfigureAwait(false);
            if (result.ValueKind == JsonValueKind.Object
                && result.TryGetProperty("value", out var value)
                && value.GetString() is { Length: > 0 } title
                && title != _currentTitle)
            {
                _currentTitle = title;
                TitleChanged?.Invoke(title);
            }
        }
        catch (Exception)
        {
            // Title is cosmetic.
        }
    }

    private static ConsoleLine BuildConsoleLine(JsonElement parameters)
    {
        var level = parameters.TryGetProperty("type", out var type) ? type.GetString() ?? "log" : "log";
        var text = string.Empty;

        if (parameters.TryGetProperty("args", out var args) && args.ValueKind == JsonValueKind.Array)
        {
            var parts = new List<string>(args.GetArrayLength());
            foreach (var argument in args.EnumerateArray())
            {
                parts.Add(argument.ValueKind switch
                {
                    JsonValueKind.Object when argument.TryGetProperty("value", out var value) => value.ToString(),
                    JsonValueKind.Object when argument.TryGetProperty("unserializableValue", out var raw) => raw.GetString() ?? string.Empty,
                    JsonValueKind.Object when argument.TryGetProperty("description", out var description) => description.GetString() ?? string.Empty,
                    _ => argument.ToString(),
                });
            }

            text = string.Join(" ", parts);
        }

        return new ConsoleLine(DateTimeOffset.Now, level, text);
    }

    private static ConsoleLine BuildExceptionLine(JsonElement parameters)
    {
        var text = "Uncaught exception";
        if (parameters.TryGetProperty("exceptionDetails", out var details))
        {
            if (details.TryGetProperty("exception", out var exception)
                && exception.TryGetProperty("description", out var description)
                && description.GetString() is { Length: > 0 } message)
            {
                text = message;
            }
            else if (details.TryGetProperty("text", out var detailsText) && detailsText.GetString() is { Length: > 0 } fallback)
            {
                text = fallback;
            }
        }

        return new ConsoleLine(DateTimeOffset.Now, "error", text);
    }

    private void SetUrl(string url)
    {
        if (url == _currentUrl) return;
        _currentUrl = url;
        UrlChanged?.Invoke(url);
    }

    private void OnClosed(string? reason)
    {
        if (Volatile.Read(ref _disposed) != 0) return;
        SessionClosed?.Invoke(reason ?? "The engine closed the page session.");
    }

    private async Task TrySendAsync(string method, object? parameters, CancellationToken cancellationToken)
    {
        if (_client is null) return;
        try
        {
            await _client.SendAsync(method, parameters, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Optional domains are absent on older engines.
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        if (_client is not null)
        {
            if (_streaming)
            {
                try { await _client.SendAsync("Page.stopScreencast", null, CancellationToken.None).ConfigureAwait(false); }
                catch { /* session already gone */ }
            }

            _client.EventReceived -= OnEvent;
            _client.Closed -= OnClosed;
            await _client.DisposeAsync().ConfigureAwait(false);
        }

        _viewportLock.Dispose();
    }
}

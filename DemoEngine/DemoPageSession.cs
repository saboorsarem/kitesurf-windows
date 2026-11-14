// Copyright (c) 2026 Cloudflare, Inc.
// Licensed under the Apache 2.0 license found in the LICENSE file or at:
// https://opensource.org/licenses/Apache-2.0

using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Kitesurf.Windows.DemoEngine;

/// <summary>One CDP socket of the demo engine, with the same frame ack pacing the real engine uses.</summary>
internal sealed class DemoPageSession
{
    private readonly WebSocket _socket;
    private readonly CancellationTokenSource _lifetime;
    private readonly DemoPage _page = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly object _stateGate = new();

    private int _viewportWidth = 1280;
    private int _viewportHeight = 800;
    private int _quality = 80;
    private int _maxFramesInFlight = 2;
    private int _framesInFlight;
    private int _frameSequence;
    private bool _streaming;
    private bool _dirty;
    private Task? _pump;

    /// <summary>Test hooks: emit one frame even when the page is idle, then drop the socket.</summary>
    public static bool ForceNextFrame;

    public static bool DropAfterNextFrame;
    private readonly List<HistoryEntry> _history = new();
    private int _historyIndex = -1;

    public DemoPageSession(WebSocket socket, CancellationToken requestAborted)
    {
        _socket = socket;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(requestAborted);
    }

    public async Task RunAsync()
    {
        _pump = Task.Run(() => PumpFramesAsync(_lifetime.Token));
        var buffer = new byte[16 * 1024];
        using var message = new MemoryStream();

        try
        {
            while (!_lifetime.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                message.SetLength(0);
                while (true)
                {
                    var result = await _socket.ReceiveAsync(new ArraySegment<byte>(buffer), _lifetime.Token).ConfigureAwait(false);
                    if (result.MessageType == WebSocketMessageType.Close) return;
                    message.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage) break;
                }

                await HandleMessageAsync(message.GetBuffer().AsMemory(0, (int)message.Length)).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Client went away.
        }
        catch (WebSocketException)
        {
            // Client went away.
        }
        finally
        {
            _lifetime.Cancel();
            if (_pump is not null)
            {
                try { await _pump.ConfigureAwait(false); } catch { /* pump cancelled */ }
            }
            _sendLock.Dispose();
        }
    }

    private async Task HandleMessageAsync(Memory<byte> utf8)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(utf8.Span);
        }
        catch (JsonException)
        {
            return;
        }

        if (node is not JsonObject request) return;
        if (!request.TryGetPropertyValue("id", out var idNode) || idNode is null) return;
        var id = idNode.GetValue<int>();
        var method = request["method"]?.GetValue<string>() ?? string.Empty;
        var parameters = request["params"] as JsonObject;

        try
        {
            var result = await DispatchAsync(method, parameters).ConfigureAwait(false);
            await ReplyAsync(id, result).ConfigureAwait(false);
        }
        catch (Exception error)
        {
            await ReplyErrorAsync(id, error.Message).ConfigureAwait(false);
        }
    }

    private async Task<JsonObject> DispatchAsync(string method, JsonObject? parameters)
    {
        switch (method)
        {
            case "Page.enable":
            case "Runtime.enable":
            case "Network.enable":
            case "Log.enable":
            case "Page.setLifecycleEventsEnabled":
            case "Page.stopScreencast":
                if (method == "Page.stopScreencast")
                {
                    lock (_stateGate) _streaming = false;
                }
                return new JsonObject();

            case "Page.startScreencast":
            {
                var width = parameters?["maxWidth"]?.GetValue<int>() ?? 1280;
                var height = parameters?["maxHeight"]?.GetValue<int>() ?? 800;
                lock (_stateGate)
                {
                    _viewportWidth = Math.Clamp(width, 320, 4096);
                    _viewportHeight = Math.Clamp(height, 240, 4096);
                    _quality = Math.Clamp(parameters?["quality"]?.GetValue<int>() ?? 80, 1, 100);
                    _maxFramesInFlight = Math.Clamp(
                        parameters?["maxFramesInFlight"]?.GetValue<int>()
                        ?? parameters?["maxInFlight"]?.GetValue<int>()
                        ?? 2,
                        1,
                        8);
                    _streaming = true;
                    _dirty = true;
                }

                _page.Configure(_viewportWidth, _viewportHeight);
                return new JsonObject();
            }

            case "Page.screencastFrameAck":
            {
                lock (_stateGate)
                {
                    if (_framesInFlight > 0) _framesInFlight--;
                }
                return new JsonObject();
            }

            case "Page.navigate":
            {
                var url = parameters?["url"]?.GetValue<string>() ?? "about:blank";
                if (!url.Contains("://", StringComparison.Ordinal) && !url.StartsWith("about:", StringComparison.Ordinal))
                    url = "https://" + url;

                _page.Navigate(url);
                RecordHistory(url);
                MarkDirty();

                await EmitNavigationAsync(url).ConfigureAwait(false);
                await EmitConsoleAsync("log", $"demo engine: rendered {url} at {_viewportWidth}×{_viewportHeight}").ConfigureAwait(false);

                return new JsonObject { ["frameId"] = "DEMO", ["loaderId"] = "L", ["isDownload"] = false };
            }

            case "Page.getNavigationHistory":
            {
                var entries = new JsonArray();
                foreach (var entry in _history)
                {
                    entries.Add(new JsonObject
                    {
                        ["id"] = entry.Id,
                        ["url"] = entry.Url,
                        ["userTypedURL"] = entry.Url,
                        ["title"] = entry.Title,
                        ["transitionType"] = "typed",
                    });
                }

                return new JsonObject { ["currentIndex"] = Math.Max(0, _historyIndex), ["entries"] = entries };
            }

            case "Page.navigateToHistoryEntry":
            {
                var entryId = parameters?["entryId"]?.GetValue<int>() ?? -1;
                var index = _history.FindIndex(entry => entry.Id == entryId);
                if (index < 0) throw new InvalidOperationException($"no history entry {entryId}");

                _historyIndex = index;
                _page.Navigate(_history[index].Url);
                MarkDirty();
                await EmitNavigationAsync(_history[index].Url).ConfigureAwait(false);
                return new JsonObject();
            }

            case "Page.stopLoading":
                return new JsonObject();

            case "Page.getLayoutMetrics":
                lock (_stateGate)
                {
                    return new JsonObject
                    {
                        ["cssLayoutViewport"] = new JsonObject
                        {
                            ["clientWidth"] = _viewportWidth,
                            ["clientHeight"] = _viewportHeight,
                            ["pageX"] = 0,
                            ["pageY"] = 0,
                        },
                        ["cssContentSize"] = new JsonObject
                        {
                            ["x"] = 0,
                            ["y"] = 0,
                            ["width"] = _viewportWidth,
                            ["height"] = 1560,
                        },
                    };
                }

            case "Page.captureScreenshot":
            {
                int width, height;
                lock (_stateGate)
                {
                    width = _viewportWidth;
                    height = _viewportHeight;
                }
                var png = _page.RenderPng(width, height);
                return new JsonObject { ["data"] = Convert.ToBase64String(png) };
            }

            case "Runtime.evaluate":
            {
                var expression = parameters?["expression"]?.GetValue<string>() ?? string.Empty;
                return new JsonObject { ["result"] = Evaluate(expression) };
            }

            case "Input.dispatchMouseEvent":
            {
                var type = parameters?["type"]?.GetValue<string>() ?? string.Empty;
                var x = parameters?["x"]?.GetValue<double>() ?? 0;
                var y = parameters?["y"]?.GetValue<double>() ?? 0;

                switch (type)
                {
                    case "mousePressed":
                        _page.ClickAt(x, y);
                        MarkDirty();
                        break;
                    case "mouseMoved":
                        _page.SetHover(x, y);
                        MarkDirty();
                        break;
                    case "mouseWheel":
                        var deltaY = parameters?["deltaY"]?.GetValue<double>() ?? 0;
                        var deltaX = parameters?["deltaX"]?.GetValue<double>() ?? 0;
                        _page.ScrollBy(deltaY + deltaX * 0.2);
                        MarkDirty();
                        break;
                }

                return new JsonObject();
            }

            case "Input.dispatchKeyEvent":
            {
                var type = parameters?["type"]?.GetValue<string>() ?? string.Empty;
                if (type is "keyDown" or "rawKeyDown")
                {
                    var text = parameters?["text"]?.GetValue<string>();
                    var key = parameters?["key"]?.GetValue<string>() ?? string.Empty;
                    if (!string.IsNullOrEmpty(text)) _page.Type(text);
                    else if (key == "Backspace") _page.Backspace();
                    else if (key == "Enter") _page.Type("\n");
                    MarkDirty();
                }
                return new JsonObject();
            }

            case "Input.insertText":
            {
                var text = parameters?["text"]?.GetValue<string>();
                if (!string.IsNullOrEmpty(text)) _page.Type(text);
                MarkDirty();
                return new JsonObject();
            }

            case "Target.getTargets":
                return new JsonObject
                {
                    ["targetInfos"] = new JsonArray
                    {
                        new JsonObject { ["targetId"] = "kitesurf", ["type"] = "page", ["title"] = _page.Title, ["url"] = _page.Url, ["attached"] = true },
                    },
                };

            default:
                throw new InvalidOperationException($"'{method}' was not found in the demo engine.");
        }
    }

    private void RecordHistory(string url)
    {
        if (_historyIndex >= 0 && _historyIndex < _history.Count - 1)
            _history.RemoveRange(_historyIndex + 1, _history.Count - _historyIndex - 1);

        _history.Add(new HistoryEntry(_history.Count + 1, url, _page.Title));
        _historyIndex = _history.Count - 1;
    }

    private async Task EmitNavigationAsync(string url)
    {
        await EmitAsync("Page.frameStartedLoading", new JsonObject { ["frameId"] = "DEMO" }).ConfigureAwait(false);
        await EmitAsync("Page.frameNavigated", new JsonObject
        {
            ["frame"] = new JsonObject
            {
                ["id"] = "DEMO",
                ["loaderId"] = "L" + Environment.TickCount64,
                ["url"] = url,
                ["securityOrigin"] = "https://demo.local",
                ["mimeType"] = "text/html",
            },
        }).ConfigureAwait(false);
        await EmitAsync("Page.lifecycleEvent", new JsonObject
        {
            ["frameId"] = "DEMO",
            ["loaderId"] = "L",
            ["name"] = "load",
            ["timestamp"] = Environment.TickCount64 / 1000.0,
        }).ConfigureAwait(false);
        await EmitAsync("Page.loadEventFired", new JsonObject { ["timestamp"] = Environment.TickCount64 / 1000.0 }).ConfigureAwait(false);
    }

    private sealed record HistoryEntry(int Id, string Url, string Title);

    private JsonObject Evaluate(string expression)
    {
        var trimmed = (expression ?? string.Empty).Trim();
        if (trimmed is "document.title") return new JsonObject { ["type"] = "string", ["value"] = _page.Title };
        if (trimmed is "location.href" or "document.URL") return new JsonObject { ["type"] = "string", ["value"] = _page.Url };
        if (trimmed is "demo.clicks") return new JsonObject { ["type"] = "number", ["value"] = _page.Clicks };
        if (trimmed is "demo.typed") return new JsonObject { ["type"] = "string", ["value"] = _page.Typed };
        return new JsonObject { ["type"] = "undefined" };
    }

    private void MarkDirty()
    {
        lock (_stateGate) _dirty = true;
    }

    private async Task PumpFramesAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(20));
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                int width, height, quality, frameId;
                lock (_stateGate)
                {
                    var forced = ForceNextFrame;
                    if (!_streaming || (!_dirty && !forced) || _framesInFlight >= _maxFramesInFlight) continue;
                    _dirty = false;
                    if (forced) ForceNextFrame = false;
                    _framesInFlight++;
                    _frameSequence++;
                    frameId = _frameSequence;
                    width = _viewportWidth;
                    height = _viewportHeight;
                    quality = _quality;
                }

                var jpeg = _page.RenderJpeg(width, height, quality);
                await EmitAsync("Page.screencastFrame", new JsonObject
                {
                    ["data"] = Convert.ToBase64String(jpeg),
                    ["sessionId"] = frameId,
                    ["metadata"] = new JsonObject
                    {
                        ["offsetTop"] = 0,
                        ["pageScaleFactor"] = 1,
                        ["deviceWidth"] = width,
                        ["deviceHeight"] = height,
                        ["scrollOffsetX"] = 0,
                        ["scrollOffsetY"] = 0,
                        ["timestamp"] = Environment.TickCount64 / 1000.0,
                    },
                }).ConfigureAwait(false);

                if (DropAfterNextFrame)
                {
                    DropAfterNextFrame = false;
                    try
                    {
                        await _socket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "demo: dropped mid-frame", CancellationToken.None).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Client already gone.
                    }

                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Session closed.
        }
    }

    private Task EmitConsoleAsync(string level, string text) =>
        EmitAsync("Runtime.consoleAPICalled", new JsonObject
        {
            ["type"] = level,
            ["args"] = new JsonArray { new JsonObject { ["type"] = "string", ["value"] = text } },
            ["executionContextId"] = 1,
            ["timestamp"] = Environment.TickCount64 / 1000.0,
        });

    private Task EmitAsync(string method, JsonObject parameters) =>
        SendAsync(new JsonObject { ["method"] = method, ["params"] = parameters });

    private Task ReplyAsync(int id, JsonObject result) =>
        SendAsync(new JsonObject { ["id"] = id, ["result"] = result });

    private Task ReplyErrorAsync(int id, string message) =>
        SendAsync(new JsonObject
        {
            ["id"] = id,
            ["error"] = new JsonObject { ["code"] = -32601, ["message"] = message },
        });

    private async Task SendAsync(JsonObject payload)
    {
        if (_socket.State != WebSocketState.Open) return;
        var bytes = Encoding.UTF8.GetBytes(payload.ToJsonString());

        await _sendLock.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, CancellationToken.None).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
            // The client closed; the read loop will notice.
        }
        finally
        {
            _sendLock.Release();
        }
    }
}

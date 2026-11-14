// Copyright (c) 2026 Cloudflare, Inc.
// Licensed under the Apache 2.0 license found in the LICENSE file or at:
// https://opensource.org/licenses/Apache-2.0

using System.Collections.Concurrent;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace Kitesurf.Windows.Core.Cdp;

public sealed class CdpException : Exception
{
    public CdpException(int errorCode, string message, string? errorData = null) : base(message)
    {
        ErrorCode = errorCode;
        ErrorData = errorData;
    }

    public int ErrorCode { get; }

    public string? ErrorData { get; }
}

public sealed record CdpEvent(string Method, JsonElement Params);

/// <summary>
/// One Chrome DevTools Protocol WebSocket session. Requests and responses are matched by id;
/// unhandled messages are raised as <see cref="EventReceived"/> on the receive loop.
/// </summary>
public sealed class CdpClient : IAsyncDisposable
{
    private const int MaxMessageBytes = 32 * 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ClientWebSocket _socket = new();
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IReadOnlyDictionary<string, string>? _headers;

    private Task? _receiveLoop;
    private int _nextId;
    private int _disposed;

    public CdpClient(string endpoint, IReadOnlyDictionary<string, string>? headers = null)
    {
        Endpoint = endpoint;
        _headers = headers;
    }

    public string Endpoint { get; }

    public bool IsOpen => _socket.State == WebSocketState.Open;

    /// <summary>When true, every frame sent and received is echoed on <see cref="TrafficObserved"/>.</summary>
    public bool TraceTraffic { get; set; }

    public event Action<string, string>? TrafficObserved;

    public event Action<CdpEvent>? EventReceived;

    /// <summary>Raised once when the session ends; the argument is the close reason, if any.</summary>
    public event Action<string?>? Closed;

    public async Task ConnectAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _lifetime.Token);
        attempt.CancelAfter(timeout);

        // Large frames dominate this socket; keep the payload budget well above one screen.
        _socket.Options.SetBuffer(64 * 1024, MaxMessageBytes);

        // Cloudflare Access authenticates the upgrade itself, not just the HTTP discovery call.
        if (_headers is not null)
        {
            foreach (var (name, value) in _headers)
            {
                try { _socket.Options.SetRequestHeader(name, value); }
                catch (ArgumentException) { /* a restricted header, the server will not need it */ }
            }
        }

        // A system proxy must never sit between the client and a loopback engine: it cannot reach it.
        if (new Uri(Endpoint).IsLoopback) _socket.Options.Proxy = null;

        Diagnostics.Log($"cdp: connecting {Endpoint} auth={(_headers is null ? "none" : "cf-access")}");

        try
        {
            await _socket.ConnectAsync(new Uri(Endpoint), attempt.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            Diagnostics.Log($"cdp: timed out connecting {Endpoint}");
            throw new TimeoutException($"Timed out connecting to {Endpoint}.");
        }
        catch (Exception error)
        {
            Diagnostics.Log($"cdp: connect failed {Endpoint}: {error.Message}");
            throw;
        }

        Diagnostics.Log("cdp: connected");

        _receiveLoop = Task.Run(() => ReceiveLoopAsync(_lifetime.Token), CancellationToken.None);
    }

    public async Task<JsonElement> SendAsync(string method, object? parameters = null, CancellationToken cancellationToken = default)
    {
        if (!IsOpen) throw new InvalidOperationException($"CDP session to {Endpoint} is not open.");

        var id = Interlocked.Increment(ref _nextId);
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = completion;

        var payload = BuildEnvelope(id, method, parameters);
        try
        {
            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await _socket.SendAsync(payload, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }

            if (TraceTraffic) RaiseTraffic("tx", payload);

            using var registration = cancellationToken.Register(() =>
            {
                if (_pending.TryRemove(id, out var pending)) pending.TrySetCanceled(cancellationToken);
            });

            return await completion.Task.ConfigureAwait(false);
        }
        catch
        {
            _pending.TryRemove(id, out _);
            throw;
        }
    }

    /// <summary>Resolves <see cref="SendAsync"/> errors that the caller treats as a soft miss.</summary>
    public static bool IsProtocolError(Exception error, out string message)
    {
        if (error is CdpException cdp)
        {
            message = cdp.Message;
            return true;
        }

        message = error.Message;
        return false;
    }

    private static byte[] BuildEnvelope(int id, string method, object? parameters)
    {
        var envelope = new CdpRequestEnvelope { Id = id, Method = method, Params = parameters };
        return JsonSerializer.SerializeToUtf8Bytes(envelope, JsonOptions);
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream(capacity: 256 * 1024);
        string? closeReason = null;

        try
        {
            while (!cancellationToken.IsCancellationRequested && _socket.State == WebSocketState.Open)
            {
                message.SetLength(0);

                while (true)
                {
                    var result = await _socket
                        .ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken)
                        .ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        closeReason = result.CloseStatusDescription ?? _socket.CloseStatus?.ToString();
                        return;
                    }

                    if (message.Length + result.Count > MaxMessageBytes)
                    {
                        closeReason = "Engine sent a message larger than the client budget.";
                        return;
                    }

                    message.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage) break;
                }

                Dispatch(message.GetBuffer().AsSpan(0, (int)message.Length));
            }
        }
        catch (OperationCanceledException)
        {
            // Shutdown path.
        }
        catch (WebSocketException error)
        {
            closeReason = error.Message;
        }
        catch (Exception error)
        {
            closeReason = error.Message;
        }
        finally
        {
            Diagnostics.Log($"cdp: closed ({closeReason ?? "normal"})");
            FailPending(closeReason ?? "The engine closed the session.");
            try { Closed?.Invoke(closeReason); } catch { /* listeners are UI code */ }
        }
    }

    private void Dispatch(ReadOnlySpan<byte> utf8)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(utf8.ToArray());
        }
        catch (JsonException)
        {
            return;
        }

        using (document)
        {
            var root = document.RootElement;

            if (TraceTraffic) RaiseTraffic("rx", utf8);
            if (root.TryGetProperty("id", out var idProperty) && idProperty.TryGetInt32(out var id))
            {
                if (!_pending.TryRemove(id, out var completion)) return;

                if (root.TryGetProperty("error", out var error))
                {
                    var errorCode = error.TryGetProperty("code", out var code) && code.TryGetInt32(out var parsed) ? parsed : -1;
                    var errorMessage = error.TryGetProperty("message", out var text) ? text.GetString() ?? "CDP error" : "CDP error";
                    var errorData = error.TryGetProperty("data", out var data) ? data.GetString() : null;
                    completion.TrySetException(new CdpException(errorCode, errorMessage, errorData));
                    return;
                }

                var result = root.TryGetProperty("result", out var payload) ? payload.Clone() : default;
                completion.TrySetResult(result);
                return;
            }

            if (root.TryGetProperty("method", out var methodProperty) && methodProperty.GetString() is { } method)
            {
                var parameters = root.TryGetProperty("params", out var payload) ? payload.Clone() : default;
                var raised = EventReceived;
                if (raised is null) return;
                try { raised(new CdpEvent(method, parameters)); } catch { /* a listener fault must not kill the socket */ }
            }
        }
    }

    private void RaiseTraffic(string direction, ReadOnlySpan<byte> utf8)
    {
        var hosted = TrafficObserved;
        if (hosted is null) return;

        var text = Encoding.UTF8.GetString(utf8);
        if (text.Length > 4000) text = text[..4000] + "…";

        try { hosted(direction, text); } catch { /* a listener fault must not kill the socket */ }
    }

    private void FailPending(string reason)
    {
        foreach (var key in _pending.Keys)
        {
            if (_pending.TryRemove(key, out var completion))
                completion.TrySetException(new InvalidOperationException(reason));
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _lifetime.Cancel();

        try
        {
            if (_socket.State == WebSocketState.Open)
            {
                using var closeTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(1));
                await _socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "client closing", closeTimeout.Token)
                    .ConfigureAwait(false);
            }
        }
        catch
        {
            // Closing a broken socket is best effort.
        }

        if (_receiveLoop is not null)
        {
            try { await _receiveLoop.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
            catch { /* loop already gone */ }
        }

        FailPending("The client closed the session.");
        _socket.Dispose();
        _sendLock.Dispose();
        _lifetime.Dispose();
    }

    private sealed class CdpRequestEnvelope
    {
        [System.Text.Json.Serialization.JsonPropertyName("id")]
        public int Id { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("method")]
        public string Method { get; set; } = string.Empty;

        [System.Text.Json.Serialization.JsonPropertyName("params")]
        public object? Params { get; set; }
    }

}

// Copyright (c) 2026 Cloudflare, Inc.
// Licensed under the Apache 2.0 license found in the LICENSE file or at:
// https://opensource.org/licenses/Apache-2.0

using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Kitesurf.Windows.Core;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Kitesurf.Windows.DemoEngine;

/// <summary>
/// A local, synthetic engine that speaks the slice of CDP this client uses. It exists so the shell
/// can be exercised on a machine without a Cloudflare Workers Paid plan; it is not a browser and
/// never leaves the loopback interface. <c>requireAccessToken</c> makes it demand the same
/// CF-Access headers a protected Worker does, so the client's token path can be tested.
/// </summary>
public sealed class DemoEngine : IAsyncDisposable
{
    public const string DemoAccessClientId = "demo-client-id";
    public const string DemoAccessSecret = "demo-client-secret";

    private readonly WebApplication _app;
    private readonly bool _requireAccessToken;
    private readonly AccessLog _log = new();
    private readonly List<WebSocket> _sockets = new();
    private EngineEndpoint _endpoint = null!;

    private DemoEngine(WebApplication app, bool requireAccessToken)
    {
        _app = app;
        _requireAccessToken = requireAccessToken;
    }

    public EngineEndpoint Endpoint => _endpoint;

    public string? LastProbeClientId => _log.ProbeClientId;

    public bool LastProbeHadSecret => _log.ProbeHadSecret;

    public string? LastSocketClientId => _log.SocketClientId;

    public bool LastSocketHadSecret => _log.SocketHadSecret;

    public static async Task<DemoEngine> StartAsync(bool requireAccessToken = false, CancellationToken cancellationToken = default)
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options => options.Listen(IPAddress.Loopback, 0));

        var app = builder.Build();
        app.UseWebSockets();

        var engine = new DemoEngine(app, requireAccessToken);

        app.MapGet("/", () => Results.Text("Kitesurf demo engine. Point the client at this origin.", "text/plain"));

        app.MapGet("/json/version", (HttpContext context) =>
        {
            engine._log.RecordProbe(context);
            if (!engine.IsAuthorized(context)) return Results.Text("Cloudflare Access denied", "text/html", Encoding.UTF8, 403);

            return Results.Json(new Dictionary<string, object>
            {
                ["Browser"] = "kitesurf-demo/1.0",
                ["Protocol-Version"] = "1.3",
                ["User-Agent"] = "kitesurf-demo",
                ["webSocketDebuggerUrl"] = string.Empty,
            });
        });

        app.MapGet("/json/list", (HttpContext context) => Results.Json(new object[]
        {
            new
            {
                id = "kitesurf",
                type = "page",
                title = "Kitesurf demo page",
                url = "about:blank",
                webSocketDebuggerUrl = $"ws://{context.Request.Host}/devtools/page/kitesurf",
            },
        }));

        // Test hook: what the engine received, so --selftest can prove both channels carried the token.
        app.MapGet("/demo/headers", () => Results.Json(new
        {
            probeClientId = engine.LastProbeClientId,
            probeHadSecret = engine.LastProbeHadSecret,
            socketClientId = engine.LastSocketClientId,
            socketHadSecret = engine.LastSocketHadSecret,
        }));

        // Test hook: drop every live page socket, the way a Worker idle timeout would.
        app.MapPost("/demo/drop", async () =>
        {
            WebSocket[] sockets;
            lock (engine._sockets) sockets = engine._sockets.ToArray();

            foreach (var socket in sockets)
            {
                try
                {
                    await socket.CloseAsync(WebSocketCloseStatus.EndpointUnavailable, "demo: dropped", CancellationToken.None);
                }
                catch
                {
                    // Already gone.
                }
            }

            return Results.Json(new { dropped = sockets.Length });
        });

        app.Map("/devtools/page/kitesurf", async context =>
        {
            engine._log.RecordSocket(context);
            if (!engine.IsAuthorized(context))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                await context.Response.WriteAsync("Cloudflare Access denied", context.RequestAborted);
                return;
            }

            if (!context.WebSockets.IsWebSocketRequest)
            {
                context.Response.StatusCode = StatusCodes.Status400BadRequest;
                return;
            }

            using var socket = await context.WebSockets.AcceptWebSocketAsync();
            lock (engine._sockets) engine._sockets.Add(socket);

            try
            {
                var session = new DemoPageSession(socket, context.RequestAborted);
                await session.RunAsync();
            }
            finally
            {
                lock (engine._sockets) engine._sockets.Remove(socket);
            }
        });

        await app.StartAsync(cancellationToken).ConfigureAwait(false);

        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()?.Addresses
                        ?? Array.Empty<string>();
        var origin = (addresses.FirstOrDefault() ?? "http://127.0.0.1:8787").Replace("[::]", "127.0.0.1");

        engine._endpoint = EngineEndpoint.Create(origin);
        return engine;
    }

    private bool IsAuthorized(HttpContext context)
    {
        if (!_requireAccessToken) return true;

        var clientId = context.Request.Headers["CF-Access-Client-Id"].ToString();
        var secret = context.Request.Headers["CF-Access-Client-Secret"].ToString();
        return clientId == DemoAccessClientId && secret == DemoAccessSecret;
    }

    public async ValueTask DisposeAsync()
    {
        try { await _app.StopAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false); }
        catch { /* shutting down */ }
        await _app.DisposeAsync().ConfigureAwait(false);
    }

    private sealed class AccessLog
    {
        private readonly object _gate = new();

        public string? ProbeClientId { get; private set; }

        public bool ProbeHadSecret { get; private set; }

        public string? SocketClientId { get; private set; }

        public bool SocketHadSecret { get; private set; }

        public void RecordProbe(HttpContext context)
        {
            lock (_gate)
            {
                ProbeClientId = context.Request.Headers["CF-Access-Client-Id"].ToString();
                ProbeHadSecret = context.Request.Headers["CF-Access-Client-Secret"].ToString().Length > 0;
            }
        }

        public void RecordSocket(HttpContext context)
        {
            lock (_gate)
            {
                SocketClientId = context.Request.Headers["CF-Access-Client-Id"].ToString();
                SocketHadSecret = context.Request.Headers["CF-Access-Client-Secret"].ToString().Length > 0;
            }
        }
    }
}

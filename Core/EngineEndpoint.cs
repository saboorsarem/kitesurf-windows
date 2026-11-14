// Copyright (c) 2026 Cloudflare, Inc.
// Licensed under the Apache 2.0 license found in the LICENSE file or at:
// https://opensource.org/licenses/Apache-2.0

using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;

namespace Kitesurf.Windows.Core;

/// <summary>
/// A reachable Kitesurf engine. Kitesurf exposes the Chrome DevTools Protocol, so the client only
/// needs the engine origin: page sessions live at <c>/devtools/page/kitesurf</c>.
/// </summary>
public sealed class EngineEndpoint
{
    private EngineEndpoint(string httpBase, string pageSocketUrl, string browserSocketUrl)
    {
        HttpBase = httpBase;
        PageSocketUrl = pageSocketUrl;
        BrowserSocketUrl = browserSocketUrl;
    }

    public string HttpBase { get; }

    public string PageSocketUrl { get; }

    public string BrowserSocketUrl { get; }

    public string Host => new Uri(HttpBase).Authority;

    public bool IsSecure => HttpBase.StartsWith("https://", StringComparison.OrdinalIgnoreCase);

    public static EngineEndpoint Create(string httpBase)
    {
        var baseUri = new Uri(httpBase.TrimEnd('/'));
        var socketScheme = baseUri.Scheme == Uri.UriSchemeHttps ? "wss" : "ws";
        var authority = baseUri.Authority;
        return new EngineEndpoint(
            $"{baseUri.Scheme}://{authority}",
            $"{socketScheme}://{authority}/devtools/page/kitesurf",
            $"{socketScheme}://{authority}/devtools/browser");
    }

    /// <summary>
    /// Accepts what a person would paste: a bare host, an engine origin, a DevTools WebSocket URL,
    /// or the inspector URL the engine prints on its landing page.
    /// </summary>
    public static bool TryParse(string? input, out EngineEndpoint? endpoint, out string error)
    {
        endpoint = null;
        error = string.Empty;

        var text = (input ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            error = "Enter an engine address, for example 127.0.0.1:8787.";
            return false;
        }

        if (text.StartsWith("demo:", StringComparison.OrdinalIgnoreCase))
        {
            error = "The demo engine is started from the welcome screen.";
            return false;
        }

        if (!text.Contains("://", StringComparison.Ordinal)) text = "http://" + text;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.Host))
        {
            error = "That is not a URL the client can reach.";
            return false;
        }

        if (uri.Scheme is not ("http" or "https" or "ws" or "wss"))
        {
            error = "Use an http(s) or ws(s) engine address.";
            return false;
        }

        var httpScheme = uri.Scheme is "wss" or "https" ? "https" : "http";
        var builder = new UriBuilder(uri) { Scheme = httpScheme, Path = "/", Query = string.Empty, Fragment = string.Empty };

        try
        {
            endpoint = Create(builder.Uri.ToString());
        }
        catch (UriFormatException)
        {
            error = "That engine address is not valid.";
            return false;
        }

        return true;
    }

    public string InspectorUrl(string? pageUrl)
    {
        if (string.IsNullOrWhiteSpace(pageUrl)) return $"{HttpBase}/?inspect=1";
        return $"{HttpBase}/?url={Uri.EscapeDataString(pageUrl)}";
    }

    /// <summary>Returns a copy of this endpoint whose page socket came from engine discovery.</summary>
    public EngineEndpoint WithPageSocket(string? pageSocketUrl) =>
        new(HttpBase, string.IsNullOrWhiteSpace(pageSocketUrl) ? PageSocketUrl : pageSocketUrl, BrowserSocketUrl);

    /// <summary>
    /// Engines report their socket URL as ws(s)://, http(s):// or a relative path, and a Worker behind
    /// TLS must be reached over wss even if it names ws. Normalise all three into a usable URL.
    /// </summary>
    public string NormalizeSocketUrl(string? reported, string fallback)
    {
        if (string.IsNullOrWhiteSpace(reported)) return fallback;

        var text = reported.Trim();
        if (!text.Contains("://", StringComparison.Ordinal))
            return text.StartsWith('/') ? HttpBase + text : $"{HttpBase}/{text}";

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri)) return fallback;

        var scheme = IsSecure ? "wss" : "ws";
        var builder = new UriBuilder(uri)
        {
            Scheme = scheme,
            Port = uri.IsDefaultPort ? -1 : uri.Port,
        };

        return builder.Uri.ToString();
    }

    public override string ToString() => HttpBase;

    /// <summary>The Cloudflare Access headers both the probe and the socket upgrade must carry.</summary>
    public static IReadOnlyDictionary<string, string>? AccessHeaders(EngineProfile? profile)
    {
        if (profile is null || !profile.HasAccessToken) return null;

        var secret = profile.AccessClientSecret;
        if (string.IsNullOrWhiteSpace(secret)) return null;

        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["CF-Access-Client-Id"] = profile.AccessClientId!.Trim(),
            ["CF-Access-Client-Secret"] = secret.Trim(),
        };
    }
}

public sealed record EngineInfo(
    bool Ok,
    string Browser,
    string ProtocolVersion,
    long LatencyMs,
    string? Error,
    string? PageSocketUrl = null,
    string? BrowserSocketUrl = null)
{
    public static EngineInfo Unreachable(string error) => new(false, string.Empty, string.Empty, 0, error);
}

public static class EngineProbe
{
    private static readonly HttpClient Http = CreateClient(useProxy: true);

    private static readonly HttpClient LoopbackHttp = CreateClient(useProxy: false);

    private static HttpClient CreateClient(bool useProxy) => new(new HttpClientHandler
    {
        UseProxy = useProxy,
        // Redirects are how Cloudflare Access answers an unauthenticated request; report that
        // instead of quietly following it to an HTML login page.
        AllowAutoRedirect = false,
    })
    {
        Timeout = TimeSpan.FromSeconds(6),
    };

    public static async Task<EngineInfo> ProbeAsync(
        EngineEndpoint endpoint,
        EngineProfile? profile = null,
        CancellationToken cancellationToken = default)
    {
        var started = Stopwatch.StartNew();
        var client = new Uri(endpoint.HttpBase).IsLoopback ? LoopbackHttp : Http;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.HttpBase}/json/version");
            AttachAccessToken(request, profile);

            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            switch (response.StatusCode)
            {
                case HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden:
                    return EngineInfo.Unreachable(
                        "Blocked by Cloudflare Access — add a valid CF-Access-Client-Id and CF-Access-Client-Secret below.");

                case var status when (int)status is >= 300 and < 400:
                    return EngineInfo.Unreachable(
                        "The engine redirected instead of answering, which is Cloudflare Access. Add a service token, or connect to the Worker URL without Access.");
            }

            if (!response.IsSuccessStatusCode)
                return EngineInfo.Unreachable($"/json/version returned {(int)response.StatusCode} {response.ReasonPhrase}.");

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = document.RootElement;

            var browser = root.TryGetProperty("Browser", out var browserProperty) ? browserProperty.GetString() ?? "Kitesurf" : "Kitesurf";
            var protocol = root.TryGetProperty("Protocol-Version", out var protocolProperty) ? protocolProperty.GetString() ?? "?" : "?";
            var browserSocket = root.TryGetProperty("webSocketDebuggerUrl", out var browserWs) ? browserWs.GetString() : null;

            // Prefer the routes the engine advertises; fall back to the documented path.
            var pageSocket = await DiscoverPageSocketAsync(client, endpoint, profile, cancellationToken).ConfigureAwait(false);

            return new EngineInfo(
                true,
                browser,
                protocol,
                started.ElapsedMilliseconds,
                null,
                endpoint.NormalizeSocketUrl(pageSocket, endpoint.PageSocketUrl),
                endpoint.NormalizeSocketUrl(browserSocket, endpoint.BrowserSocketUrl));
        }
        catch (JsonException)
        {
            return EngineInfo.Unreachable(
                "The endpoint answered, but not with the DevTools discovery document. A Cloudflare Access login page in front of the Worker looks like this.");
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            return EngineInfo.Unreachable(Describe(error, endpoint));
        }
    }

    private static void AttachAccessToken(HttpRequestMessage request, EngineProfile? profile)
    {
        foreach (var (name, value) in EngineEndpoint.AccessHeaders(profile) ?? new Dictionary<string, string>())
            request.Headers.TryAddWithoutValidation(name, value);
    }

    /// <summary>Reads /json/list and returns the first page target's socket URL, if the engine publishes one.</summary>
    private static async Task<string?> DiscoverPageSocketAsync(
        HttpClient client,
        EngineEndpoint endpoint,
        EngineProfile? profile,
        CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, $"{endpoint.HttpBase}/json/list");
            AttachAccessToken(request, profile);

            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) return null;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            if (document.RootElement.ValueKind != JsonValueKind.Array) return null;

            var fallback = (string?)null;
            foreach (var target in document.RootElement.EnumerateArray())
            {
                if (!target.TryGetProperty("webSocketDebuggerUrl", out var socketProperty)) continue;
                if (socketProperty.GetString() is not { Length: > 0 } socket) continue;

                var isPage = !target.TryGetProperty("type", out var typeProperty)
                             || string.Equals(typeProperty.GetString(), "page", StringComparison.OrdinalIgnoreCase);

                if (isPage) return socket;
                fallback ??= socket;
            }

            return fallback;
        }
        catch (Exception)
        {
            // Discovery is additive: a static route still works when /json/list is absent.
            return null;
        }
    }

    private static string Describe(Exception error, EngineEndpoint endpoint)
    {
        switch (error)
        {
            case TaskCanceledException:
                return $"{endpoint.HttpBase} did not answer in time.";
            case HttpRequestException { InnerException: SocketException socket }:
                return socket.SocketErrorCode switch
                {
                    SocketError.ConnectionRefused => $"Nothing is listening at {endpoint.HttpBase}.",
                    SocketError.HostNotFound or SocketError.NoData => $"Host {endpoint.Host} was not found.",
                    SocketError.TimedOut => $"{endpoint.HttpBase} did not answer in time.",
                    SocketError.NetworkUnreachable or SocketError.HostUnreachable => $"No route to {endpoint.HttpBase}.",
                    _ => $"Could not reach {endpoint.HttpBase}: {socket.Message}",
                };
            case HttpRequestException http:
                return $"Could not reach {endpoint.HttpBase}: {http.Message}";
            default:
                return $"{endpoint.HttpBase} is not speaking Kitesurf: {error.Message}";
        }
    }
}

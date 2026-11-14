// Copyright (c) 2026 Cloudflare, Inc.
// Licensed under the Apache 2.0 license found in the LICENSE file or at:
// https://opensource.org/licenses/Apache-2.0

using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using Kitesurf.Windows.Core;
using Kitesurf.Windows.DemoEngine;

namespace Kitesurf.Windows;

/// <summary>
/// Non-interactive end-to-end check of the client's protocol layer against the demo engine:
/// connect, screencast, navigate, input, evaluation and screenshot. Runs with <c>--selftest</c>.
/// </summary>
internal static class SelfTest
{
    private static readonly List<SelfTestCheck> Checks = new();

    public static async Task<int> RunAsync()
    {
        Diagnostics.Log("selftest: start");
        var started = Stopwatch.StartNew();

        try
        {
            await using var engine = await DemoEngine.DemoEngine.StartAsync();
            Diagnostics.Log($"selftest: demo engine {engine.Endpoint.HttpBase}");

            var probe = await EngineProbe.ProbeAsync(engine.Endpoint);
            Check("probe", probe.Ok, $"{probe.Browser} {probe.ProtocolVersion} in {probe.LatencyMs} ms");

            await using var session = new PageSession(engine.Endpoint);
            var firstFrame = new TaskCompletionSource<ScreencastFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            var loaded = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            FrameStats stats = default;

            session.FrameArrived += frame =>
            {
                stats.Count++;
                stats.Width = frame.PixelWidth;
                stats.Height = frame.PixelHeight;
                firstFrame.TrySetResult(frame);
            };

            session.LoadingChanged += isLoading =>
            {
                if (!isLoading) loaded.TrySetResult(true);
            };

            await session.ConnectAsync();
            Check("connect", session.IsConnected, engine.Endpoint.PageSocketUrl);

            await session.ResizeAsync(900, 600);
            var frame = await WithTimeout(firstFrame.Task, 5000);
            Check("screencast frame", frame is not null, $"{stats.Width}x{stats.Height}, {stats.Count} frame(s)");

            await session.NavigateAsync("https://example.com/selftest");
            var didLoad = await WithTimeout(loaded.Task, 5000) == true;
            Check("navigate + load event", didLoad && session.CurrentUrl?.Contains("selftest", StringComparison.Ordinal) == true,
                session.CurrentUrl ?? "(no url)");

            // The demo page draws its button at a fixed spot; a click there increments its counter.
            var clicksBefore = await ReadNumberAsync(session, "demo.clicks");
            await session.DispatchMouseAsync("mousePressed", 220, 900, "left", 1, 1);
            await session.DispatchMouseAsync("mouseReleased", 220, 900, "left", 0, 1);
            await Task.Delay(250);
            var clicksAfter = await ReadNumberAsync(session, "demo.clicks");
            Check("mouse input", clicksAfter == clicksBefore + 1, $"clicks {clicksBefore} -> {clicksAfter}");

            await session.InsertTextAsync("kitesurf");
            await Task.Delay(150);
            var typed = await ReadStringAsync(session, "demo.typed");
            Check("text input", typed.Contains("kitesurf", StringComparison.Ordinal), $"typed '{typed}'");

            var png = await session.CaptureScreenshotAsync();
            Check("screenshot", png.Length > 1000 && png[0] == 0x89 && png[1] == 0x50, $"{png.Length} bytes");

            var title = await ReadStringAsync(session, "document.title");
            Check("runtime evaluate", title.Contains("demo", StringComparison.OrdinalIgnoreCase), title);

            await session.NavigateAsync("https://example.com/second");
            await Task.Delay(200);
            var afterSecond = await session.GetHistoryStateAsync();
            Check("history state", afterSecond.CanGoBack && !afterSecond.CanGoForward,
                $"back={afterSecond.CanGoBack} forward={afterSecond.CanGoForward}");

            var wentBack = await session.GoBackAsync();
            await Task.Delay(250);
            var afterBack = await session.GetHistoryStateAsync();
            Check("history back", wentBack && session.CurrentUrl?.Contains("selftest", StringComparison.Ordinal) == true
                                 && !afterBack.CanGoBack && afterBack.CanGoForward, session.CurrentUrl ?? "(no url)");

            var center = ViewportMapping.ToPage(new Point(360, 240), 720, 480, 900, 600);
            Check("input mapping scales", Math.Abs(center.X - 450) < 0.01 && Math.Abs(center.Y - 300) < 0.01,
                $"360,240 in 720x480 -> {center.X:0},{center.Y:0} in 900x600");

            var clamped = ViewportMapping.ToPage(new Point(-40, 900), 720, 480, 900, 600);
            Check("input mapping clamps", clamped.X == 0 && Math.Abs(clamped.Y - 600) < 0.01, $"{clamped.X:0},{clamped.Y:0}");

            // A guarded engine demands the same CF-Access headers a protected Worker does.
            await using var guarded = await DemoEngine.DemoEngine.StartAsync(requireAccessToken: true);

            var anonymous = await EngineProbe.ProbeAsync(guarded.Endpoint);
            Check("access: probe blocked without token",
                !anonymous.Ok && anonymous.Error?.Contains("Cloudflare Access", StringComparison.Ordinal) == true,
                anonymous.Error ?? "(unexpected success)");

            var securedProfile = new EngineProfile(
                "Guarded", guarded.Endpoint.HttpBase, DemoEngine.DemoEngine.DemoAccessClientId, DemoEngine.DemoEngine.DemoAccessSecret);
            var authorized = await EngineProbe.ProbeAsync(guarded.Endpoint, securedProfile);
            Check("access: probe with token", authorized.Ok, authorized.Error ?? authorized.Browser);

            var tokenFrame = new TaskCompletionSource<ScreencastFrame>(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var secured = new PageSession(guarded.Endpoint, securedProfile);
            secured.FrameArrived += frame => tokenFrame.TrySetResult(frame);
            await secured.ConnectAsync();
            await secured.ResizeAsync(800, 500);
            var securedFrame = await WithTimeout(tokenFrame.Task, 5000);
            Check("access: websocket with token", securedFrame is not null && secured.IsConnected,
                securedFrame is null ? "no frame" : $"{securedFrame.PixelWidth}x{securedFrame.PixelHeight}");

            using var plainHttp = new HttpClient(new HttpClientHandler { UseProxy = false });
            using var seen = JsonDocument.Parse(await plainHttp.GetStringAsync($"{guarded.Endpoint.HttpBase}/demo/headers"));
            var headers = seen.RootElement;
            var probeId = headers.GetProperty("probeClientId").GetString();
            var socketId = headers.GetProperty("socketClientId").GetString();
            Check("access: headers on both channels",
                probeId == DemoEngine.DemoEngine.DemoAccessClientId
                && headers.GetProperty("probeHadSecret").GetBoolean()
                && socketId == DemoEngine.DemoEngine.DemoAccessClientId
                && headers.GetProperty("socketHadSecret").GetBoolean(),
                $"probe={probeId} socket={socketId}");

            var protectedBlob = SecretProtector.Protect("token-value");
            Check("secret: dpapi round-trip",
                protectedBlob is not null && protectedBlob != "token-value" && SecretProtector.Unprotect(protectedBlob) == "token-value",
                "blob at rest differs from plaintext");
        }
        catch (Exception error)
        {
            Check("completed without exception", false, error.GetType().Name + ": " + error.Message);
            Diagnostics.Log($"selftest: exception {error}");
        }

        var passed = Checks.Count(check => check.Passed);
        var report = new
        {
            Passed = passed == Checks.Count,
            Total = Checks.Count,
            Checks,
            ElapsedMs = started.ElapsedMilliseconds,
        };

        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        var path = Path.Combine(Path.GetTempPath(), "kitesurf-selftest.json");
        await File.WriteAllTextAsync(path, json);

        foreach (var check in Checks)
            Diagnostics.Log($"selftest: {(check.Passed ? "PASS" : "FAIL")} {check.Name} — {check.Detail}");

        Diagnostics.Log($"selftest: {passed}/{Checks.Count} passed, report {path}");
        return passed == Checks.Count ? 0 : 1;
    }

    private static void Check(string name, bool passed, string detail) => Checks.Add(new SelfTestCheck(name, passed, detail));

    private static async Task<int> ReadNumberAsync(PageSession session, string expression)
    {
        var result = await session.EvaluateAsync(expression);
        return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("value", out var value)
            ? (int)value.GetDouble()
            : 0;
    }

    private static async Task<string> ReadStringAsync(PageSession session, string expression)
    {
        var result = await session.EvaluateAsync(expression);
        return result.ValueKind == JsonValueKind.Object && result.TryGetProperty("value", out var value)
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    private static async Task<T?> WithTimeout<T>(Task<T> task, int milliseconds)
    {
        var completed = await Task.WhenAny(task, Task.Delay(milliseconds));
        return completed == task ? await task : default;
    }

    private sealed record SelfTestCheck(string Name, bool Passed, string Detail);

    private struct FrameStats
    {
        public int Count;
        public int Width;
        public int Height;
    }
}

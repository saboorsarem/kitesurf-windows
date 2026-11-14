// Copyright (c) 2026 Cloudflare, Inc.
// Licensed under the Apache 2.0 license found in the LICENSE file or at:
// https://opensource.org/licenses/Apache-2.0

using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Kitesurf.Windows.Core;

/// <summary>
/// A Kitesurf engine the client can connect to. Cloudflare Access deployments need a service token;
/// the secret is persisted through DPAPI and never written to settings.json in the clear.
/// </summary>
public sealed class EngineProfile
{
    public EngineProfile()
    {
    }

    public EngineProfile(string name, string address, bool isDemo = false)
    {
        Name = name;
        Address = address;
        IsDemo = isDemo;
    }

    public EngineProfile(string name, string address, string? accessClientId, string? accessClientSecret, bool isDemo = false)
        : this(name, address, isDemo)
    {
        AccessClientId = accessClientId;
        AccessClientSecret = accessClientSecret;
    }

    public string Name { get; set; } = "Kitesurf engine";

    public string Address { get; set; } = "http://127.0.0.1:8787";

    public bool IsDemo { get; set; }

    public string? AccessClientId { get; set; }

    /// <summary>DPAPI blob, base64. See <see cref="AccessClientSecret"/>.</summary>
    public string? AccessSecretProtected { get; set; }

    [JsonIgnore]
    public string? AccessClientSecret
    {
        get => SecretProtector.Unprotect(AccessSecretProtected);
        set => AccessSecretProtected = SecretProtector.Protect(value);
    }

    [JsonIgnore]
    public bool HasAccessToken =>
        !string.IsNullOrWhiteSpace(AccessClientId) && !string.IsNullOrWhiteSpace(AccessSecretProtected);

    public EngineProfile Clone() => new(Name, Address, IsDemo)
    {
        AccessClientId = AccessClientId,
        AccessSecretProtected = AccessSecretProtected,
    };

    public override string ToString() => Name;
}

public sealed class AppSettings
{
    public List<EngineProfile> Engines { get; set; } = new();

    public string? LastEngineAddress { get; set; }

    public string HomeUrl { get; set; } = "https://example.com/";

    public bool ConsoleOpen { get; set; }

    public double ConsoleWidth { get; set; } = 360;

    public double WindowWidth { get; set; } = 1440;

    public double WindowHeight { get; set; } = 920;

    public double WindowLeft { get; set; } = double.NaN;

    public double WindowTop { get; set; } = double.NaN;

    public bool WindowMaximized { get; set; }

    /// <summary>Frame scale relative to layout units; 0 lets the client follow the monitor DPI.</summary>
    public double RenderScale { get; set; }

    public List<string> ClosedTabs { get; set; } = new();
}

public static class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    public static string DirectoryPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "KitesurfBrowser");

    public static string FilePath { get; } = Path.Combine(DirectoryPath, "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (!File.Exists(FilePath)) return new AppSettings();
            var json = File.ReadAllText(FilePath);
            return JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
        }
        catch (Exception)
        {
            // A corrupt settings file must not stop the browser from opening.
            return new AppSettings();
        }
    }

    public static void Save(AppSettings settings)
    {
        try
        {
            Directory.CreateDirectory(DirectoryPath);
            var temp = FilePath + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(settings, Options));
            File.Move(temp, FilePath, overwrite: true);
        }
        catch (Exception)
        {
            // Settings are a convenience; a read-only profile directory is not fatal.
        }
    }
}

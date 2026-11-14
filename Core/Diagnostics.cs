// Copyright (c) 2026 Cloudflare, Inc.
// Licensed under the Apache 2.0 license found in the LICENSE file or at:
// https://opensource.org/licenses/Apache-2.0

using System.IO;

namespace Kitesurf.Windows.Core;

/// <summary>
/// Append-only client log under %AppData%. It exists so a user can attach one file when an engine
/// misbehaves; it never records page content.
/// </summary>
public static class Diagnostics
{
    private const long MaxBytes = 1_000_000;

    private static readonly object Gate = new();

    public static string LogPath { get; } = Path.Combine(SettingsStore.DirectoryPath, "client.log");

    public static void Log(string message)
    {
        try
        {
            lock (Gate)
            {
                Directory.CreateDirectory(SettingsStore.DirectoryPath);

                var file = new FileInfo(LogPath);
                if (file.Exists && file.Length > MaxBytes)
                    File.Move(LogPath, LogPath + ".1", overwrite: true);

                File.AppendAllText(LogPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
            }
        }
        catch (Exception)
        {
            // Logging must never break the client.
        }
    }
}

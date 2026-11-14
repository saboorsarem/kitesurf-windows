// Copyright (c) 2026 Cloudflare, Inc.
// Licensed under the Apache 2.0 license found in the LICENSE file or at:
// https://opensource.org/licenses/Apache-2.0

using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kitesurf.Windows.Core;
using Kitesurf.Windows.ViewModels;

namespace Kitesurf.Windows.Controls;

public partial class ConsolePane : UserControl
{
    public ConsolePane()
    {
        InitializeComponent();
    }

    private MainViewModel? ViewModel => DataContext as MainViewModel;

    private void Clear_Click(object sender, RoutedEventArgs e) => ViewModel?.SelectedTab?.Console.Clear();

    private void CommandBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = RunAsync();
    }

    private void Run_Click(object sender, RoutedEventArgs e) => _ = RunAsync();

    private async Task RunAsync()
    {
        var tab = ViewModel?.SelectedTab;
        var expression = CommandBox.Text;

        if (tab is null || string.IsNullOrWhiteSpace(expression)) return;

        tab.AddConsole("input", expression);
        CommandBox.Clear();

        try
        {
            var result = await tab.EvaluateAsync(expression);
            tab.AddConsole("result", FormatResult(result));
        }
        catch (Exception error)
        {
            tab.AddConsole("error", error.Message);
        }
    }

    private static string FormatResult(JsonElement result)
    {
        if (result.ValueKind == JsonValueKind.Undefined) return "undefined";

        if (result.TryGetProperty("value", out var value))
        {
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
        }

        if (result.TryGetProperty("description", out var description)) return description.GetString() ?? "undefined";
        if (result.TryGetProperty("unserializableValue", out var raw)) return raw.GetString() ?? "undefined";
        return result.ToString();
    }
}

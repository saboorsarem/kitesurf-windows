// Copyright (c) 2026 Cloudflare, Inc.
// Licensed under the Apache 2.0 license found in the LICENSE file or at:
// https://opensource.org/licenses/Apache-2.0

using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Kitesurf.Windows.ViewModels;

namespace Kitesurf.Windows.Controls;

public partial class WelcomeView : UserControl
{
    private bool _syncingSecret;

    public WelcomeView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            SyncSecretFromViewModel();
            EngineAddressBox.Focus();
        };
        DataContextChanged += (_, _) =>
        {
            if (DataContext is MainViewModel viewModel)
                viewModel.PropertyChanged += ViewModel_PropertyChanged;
        };
    }

    private void ViewModel_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.AccessClientSecret)) SyncSecretFromViewModel();
    }

    /// <summary>PasswordBox has no bindable Password, so the secret moves through these two methods.</summary>
    private void AccessSecretBox_PasswordChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingSecret) return;
        if (DataContext is MainViewModel viewModel) viewModel.AccessClientSecret = AccessSecretBox.Password;
    }

    private void SyncSecretFromViewModel()
    {
        if (DataContext is not MainViewModel viewModel) return;

        var secret = viewModel.AccessClientSecret ?? string.Empty;
        if (AccessSecretBox.Password == secret) return;

        _syncingSecret = true;
        AccessSecretBox.Password = secret;
        _syncingSecret = false;
    }

    private void EngineAddressBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;

        if (DataContext is MainViewModel viewModel)
        {
            viewModel.ConnectCommand.Execute(null);
            e.Handled = true;
        }
    }
}

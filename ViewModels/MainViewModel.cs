// Copyright (c) 2026 Cloudflare, Inc.
// Licensed under the Apache 2.0 license found in the LICENSE file or at:
// https://opensource.org/licenses/Apache-2.0

using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Kitesurf.Windows.Core;
using Kitesurf.Windows.DemoEngine;

namespace Kitesurf.Windows.ViewModels;

/// <summary>Shell state: engine connection, tabs, and the commands the window binds to.</summary>
public sealed class MainViewModel : ObservableObject
{
    private readonly Dispatcher _dispatcher;
    private readonly AppSettings _settings;

    private TabViewModel? _selectedTab;
    private DemoEngine.DemoEngine? _demoEngine;
    private EngineEndpoint? _endpoint;
    private string _engineName = "No engine";
    private string _engineDetail = "not connected";
    private bool _isDemo;
    private bool _isConnecting;
    private bool _consoleOpen;
    private double _consoleWidth = 360;
    private string _address = string.Empty;
    private string _engineAddress = "127.0.0.1:8787";
    private string _accessClientId = string.Empty;
    private string _accessClientSecret = string.Empty;
    private EngineProfile? _activeProfile;
    private bool _isAccessProtected;
    private bool _accessExpanded;
    private string _statusHint = "Connect a Kitesurf engine to start browsing.";
    private string _viewportText = "—";
    private long _engineLatency;
    private bool _sessionRestored;

    public MainViewModel(Dispatcher dispatcher, AppSettings settings)
    {
        _dispatcher = dispatcher;
        _settings = settings;
        _consoleOpen = settings.ConsoleOpen;
        _consoleWidth = settings.ConsoleWidth <= 0 ? 360 : settings.ConsoleWidth;

        Tabs.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasTabs));
            OnPropertyChanged(nameof(IsWelcomeVisible));
        };

        NewTabCommand = new RelayCommand(() => _ = NewTabAsync(), () => _endpoint is not null);
        CloseTabCommand = new RelayCommand(parameter =>
        {
            if (parameter is TabViewModel tab) _ = CloseTabAsync(tab);
        });
        BackCommand = new RelayCommand(() => _ = GoBackAsync(), () => SelectedTab?.CanGoBack == true);
        ForwardCommand = new RelayCommand(() => _ = GoForwardAsync(), () => SelectedTab?.CanGoForward == true);
        ReloadCommand = new RelayCommand(() => _ = (SelectedTab?.ReloadAsync() ?? Task.CompletedTask), () => SelectedTab is not null);
        HomeCommand = new RelayCommand(() => _ = NavigateAsync(_settings.HomeUrl), () => SelectedTab is not null);
        NavigateCommand = new RelayCommand(() => _ = NavigateAsync(Address), () => _endpoint is not null);
        ConnectCommand = new RelayCommand(() => _ = ConnectFromFieldAsync(), () => !_isConnecting);
        ConnectSavedCommand = new RelayCommand(parameter =>
        {
            if (parameter is EngineProfile profile) _ = ConnectAsync(profile);
        }, _ => !_isConnecting);
        StartDemoCommand = new RelayCommand(() => _ = StartDemoEngineAsync(), () => !_isConnecting);
        ToggleConsoleCommand = new RelayCommand(() =>
        {
            ConsoleOpen = !ConsoleOpen;
            _settings.ConsoleOpen = ConsoleOpen;
        });
        CopyUrlCommand = new RelayCommand(() =>
        {
            if (!string.IsNullOrEmpty(SelectedTab?.Url)) Clipboard.SetText(SelectedTab.Url);
        });
        SelectTabCommand = new RelayCommand(parameter =>
        {
            if (parameter is TabViewModel tab) SelectedTab = tab;
        });

        if (settings.Engines.Count > 0) SavedEngines = new ObservableCollection<EngineProfile>(settings.Engines);
    }

    public ObservableCollection<TabViewModel> Tabs { get; } = new();

    public ObservableCollection<EngineProfile> SavedEngines { get; } = new();

    public event Action<string>? ToastRequested;

    public RelayCommand NewTabCommand { get; }

    public RelayCommand CloseTabCommand { get; }

    public RelayCommand BackCommand { get; }

    public RelayCommand ForwardCommand { get; }

    public RelayCommand ReloadCommand { get; }

    public RelayCommand HomeCommand { get; }

    public RelayCommand NavigateCommand { get; }

    public RelayCommand ConnectCommand { get; }

    public RelayCommand ConnectSavedCommand { get; }

    public RelayCommand StartDemoCommand { get; }

    public RelayCommand ToggleConsoleCommand { get; }

    public RelayCommand CopyUrlCommand { get; }

    public RelayCommand SelectTabCommand { get; }

    public AppSettings Settings => _settings;

    public EngineEndpoint? Endpoint => _endpoint;

    public bool HasTabs => Tabs.Count > 0;

    public bool IsWelcomeVisible => SelectedTab is null;

    public TabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (_selectedTab == value) return;

            if (_selectedTab is not null) _selectedTab.IsActive = false;
            _selectedTab = value;
            if (_selectedTab is not null) _selectedTab.IsActive = true;

            OnPropertyChanged();
            OnPropertyChanged(nameof(IsWelcomeVisible));
            OnPropertyChanged(nameof(CanEditAddress));
            Address = value?.Url ?? string.Empty;
            UpdateViewportText();
            CommandManager.InvalidateRequerySuggested();
        }
    }

    public bool CanEditAddress => _endpoint is not null;

    public string Address
    {
        get => _address;
        set => Set(ref _address, value);
    }

    public string EngineAddress
    {
        get => _engineAddress;
        set => Set(ref _engineAddress, value);
    }

    /// <summary>CF-Access-Client-Id for a Worker behind Cloudflare Access.</summary>
    public string AccessClientId
    {
        get => _accessClientId;
        set => Set(ref _accessClientId, value);
    }

    /// <summary>CF-Access-Client-Secret, held in memory only until the profile protects it.</summary>
    public string AccessClientSecret
    {
        get => _accessClientSecret;
        set => Set(ref _accessClientSecret, value);
    }

    public bool IsAccessProtected
    {
        get => _isAccessProtected;
        private set => Set(ref _isAccessProtected, value);
    }

    /// <summary>Keeps the service-token section open once a profile actually uses it.</summary>
    public bool AccessExpanded
    {
        get => _accessExpanded;
        set => Set(ref _accessExpanded, value);
    }

    public string EngineName
    {
        get => _engineName;
        private set => Set(ref _engineName, value);
    }

    public string EngineDetail
    {
        get => _engineDetail;
        private set => Set(ref _engineDetail, value);
    }

    public bool IsDemo
    {
        get => _isDemo;
        private set => Set(ref _isDemo, value);
    }

    public bool IsConnecting
    {
        get => _isConnecting;
        private set
        {
            if (Set(ref _isConnecting, value)) OnPropertyChanged(nameof(ConnectionLabel));
        }
    }

    public string ConnectionLabel => _isConnecting ? "Connecting…" : _engineName;

    public bool ConsoleOpen
    {
        get => _consoleOpen;
        set
        {
            if (Set(ref _consoleOpen, value))
            {
                _settings.ConsoleOpen = value;
                OnPropertyChanged(nameof(ConsoleGridWidth));
            }
        }
    }

    public double ConsoleWidth
    {
        get => _consoleWidth;
        set
        {
            if (Set(ref _consoleWidth, Math.Clamp(value, 280, 720)))
            {
                _settings.ConsoleWidth = _consoleWidth;
                OnPropertyChanged(nameof(ConsoleGridWidth));
            }
        }
    }

    public GridLength ConsoleGridWidth => ConsoleOpen ? new GridLength(ConsoleWidth) : new GridLength(0);

    public string StatusHint
    {
        get => _statusHint;
        private set => Set(ref _statusHint, value);
    }

    public string ViewportText
    {
        get => _viewportText;
        private set => Set(ref _viewportText, value);
    }

    public long EngineLatency
    {
        get => _engineLatency;
        private set
        {
            if (Set(ref _engineLatency, value)) OnPropertyChanged(nameof(LatencyText));
        }
    }

    public string LatencyText => _engineLatency <= 0 ? "—" : $"{_engineLatency} ms";

    public string SocketText => _endpoint is null ? "no engine" : $"CDP · {_endpoint.PageSocketUrl}";

    public async Task<bool> ConnectAsync(EngineProfile profile)
    {
        if (_isConnecting) return false;

        if (!EngineEndpoint.TryParse(profile.Address, out var endpoint, out var parseError) || endpoint is null)
        {
            StatusHint = parseError;
            ToastRequested?.Invoke(parseError);
            return false;
        }

        IsConnecting = true;
        StatusHint = $"Probing {endpoint.HttpBase} …";

        var probe = await EngineProbe.ProbeAsync(endpoint, profile);
        IsConnecting = false;

        if (!probe.Ok)
        {
            StatusHint = probe.Error ?? "Engine unreachable.";
            ToastRequested?.Invoke(probe.Error ?? "Engine unreachable.");
            return false;
        }

        _endpoint = endpoint;
        _activeProfile = profile.Clone();
        EngineName = profile.Name;
        EngineDetail = $"{probe.Browser} · protocol {probe.ProtocolVersion}" + (profile.HasAccessToken ? " · CF Access" : "");
        EngineLatency = probe.LatencyMs;
        IsDemo = profile.IsDemo;
        IsAccessProtected = profile.HasAccessToken;
        StatusHint = profile.HasAccessToken
            ? "Connected through Cloudflare Access. Type an address, or press Ctrl+T for a new tab."
            : "Connected. Type an address, or press Ctrl+T for a new tab.";

        AccessClientId = profile.AccessClientId ?? string.Empty;
        AccessClientSecret = profile.AccessClientSecret ?? string.Empty;
        if (profile.HasAccessToken) AccessExpanded = true;

        RememberEngine(profile);
        _settings.LastEngineAddress = profile.Address;

        OnPropertyChanged(nameof(CanEditAddress));
        OnPropertyChanged(nameof(SocketText));
        OnPropertyChanged(nameof(ConnectionLabel));
        OnPropertyChanged(nameof(IsAccessProtected));
        CommandManager.InvalidateRequerySuggested();

        if (!_sessionRestored)
        {
            _sessionRestored = true;
            var tab = await NewTabAsync();
            if (tab is not null) await NavigateAsync(_settings.HomeUrl);
        }

        return true;
    }

    private async Task ConnectFromFieldAsync()
    {
        var text = (EngineAddress ?? string.Empty).Trim();
        if (text.Length == 0)
        {
            StatusHint = "Enter the address of a Kitesurf engine.";
            return;
        }

        var label = Uri.TryCreate(text.Contains("://", StringComparison.Ordinal) ? text : "http://" + text, UriKind.Absolute, out var uri)
            ? uri.Authority
            : "Engine";

        var hasToken = !string.IsNullOrWhiteSpace(AccessClientId) && !string.IsNullOrWhiteSpace(AccessClientSecret);
        await ConnectAsync(hasToken
            ? new EngineProfile(label, text, AccessClientId, AccessClientSecret)
            : new EngineProfile(label, text));
    }

    public async Task StartDemoEngineAsync()
    {
        if (_demoEngine is not null)
        {
            await ConnectAsync(new EngineProfile("Demo engine", _demoEngine.Endpoint.HttpBase, true));
            return;
        }

        IsConnecting = true;
        StatusHint = "Starting the local demo engine…";

        try
        {
            _demoEngine = await DemoEngine.DemoEngine.StartAsync();
            IsConnecting = false;
            await ConnectAsync(new EngineProfile("Demo engine", _demoEngine.Endpoint.HttpBase, true));
        }
        catch (Exception error)
        {
            IsConnecting = false;
            StatusHint = "Demo engine failed: " + error.Message;
            ToastRequested?.Invoke(error.Message);
        }
    }

    public async Task<TabViewModel?> NewTabAsync()
    {
        if (_endpoint is null) return null;

        var tab = new TabViewModel(_endpoint, EngineName, _dispatcher, _activeProfile);
        tab.Changed += OnTabChanged;
        tab.FrameRendered += NotifyFrame;
        Tabs.Add(tab);
        SelectedTab = tab;

        var (width, height) = SurfaceSizeProvider?.Invoke() ?? (1280, 800);
        await tab.OpenAsync(width, height);

        OnPropertyChanged(nameof(HasTabs));
        OnPropertyChanged(nameof(IsWelcomeVisible));
        return tab;
    }

    /// <summary>Set by the window so a new tab starts at the page viewport size.</summary>
    public Func<(int Width, int Height)>? SurfaceSizeProvider { get; set; }

    public Func<(int Width, int Height)>? ViewportSizeReporter { get; set; }

    public async Task CloseTabAsync(TabViewModel tab)
    {
        var index = Tabs.IndexOf(tab);
        if (index < 0) return;

        if (!string.IsNullOrEmpty(tab.Url)) _settings.ClosedTabs.Insert(0, tab.Url);

        Tabs.Remove(tab);
        tab.Changed -= OnTabChanged;
        tab.FrameRendered -= NotifyFrame;
        await tab.DisposeAsync();

        if (Tabs.Count == 0)
        {
            SelectedTab = null;
        }
        else
        {
            SelectedTab = Tabs[Math.Min(index, Tabs.Count - 1)];
        }

        OnPropertyChanged(nameof(HasTabs));
        OnPropertyChanged(nameof(IsWelcomeVisible));
        CommandManager.InvalidateRequerySuggested();
    }

    public async Task NavigateAsync(string rawInput)
    {
        var tab = SelectedTab;
        if (tab is null || _endpoint is null) return;

        var url = NormalizeInput(rawInput);
        if (url is null) return;

        Address = url;
        StatusHint = "Loading " + url;
        await tab.NavigateAsync(url);
    }

    /// <summary>Turns omnibox text into a URL. Anything without a host becomes a search.</summary>
    public static string? NormalizeInput(string? rawInput)
    {
        var text = (rawInput ?? string.Empty).Trim();
        if (text.Length == 0) return null;

        if (text.StartsWith("about:", StringComparison.OrdinalIgnoreCase)) return text;
        if (Uri.TryCreate(text, UriKind.Absolute, out var absolute) && absolute.Scheme is "http" or "https") return absolute.ToString();

        if (!text.Contains(' ') && (text.Contains('.') || text.StartsWith("localhost", StringComparison.OrdinalIgnoreCase)))
            return "https://" + text.TrimStart('/');

        return "https://duckduckgo.com/?q=" + Uri.EscapeDataString(text);
    }

    private void OnTabChanged(TabViewModel tab)
    {
        if (ReferenceEquals(tab, SelectedTab))
        {
            if (!string.Equals(Address, tab.Url, StringComparison.Ordinal) && !string.IsNullOrEmpty(tab.Url))
                Address = tab.Url;

            if (!tab.IsLoading && StatusHint.StartsWith("Loading ", StringComparison.Ordinal))
                StatusHint = "Loaded " + tab.Url;

            UpdateViewportText();
        }

        CommandManager.InvalidateRequerySuggested();
    }

    private async Task GoBackAsync()
    {
        var tab = SelectedTab;
        if (tab is null) return;
        if (!await tab.GoBackAsync()) StatusHint = "No earlier entry in this session's history.";
    }

    private async Task GoForwardAsync()
    {
        var tab = SelectedTab;
        if (tab is null) return;
        if (!await tab.GoForwardAsync()) StatusHint = "No later entry in this session's history.";
    }

    private void UpdateViewportText()
    {
        var tab = SelectedTab;
        ViewportText = tab is null || tab.FrameWidth == 0
            ? "—"
            : $"{tab.FrameWidth} × {tab.FrameHeight}";
    }

    public void NotifyFrame(TabViewModel tab)
    {
        if (ReferenceEquals(tab, SelectedTab)) UpdateViewportText();
    }

    public async Task ShutdownAsync()
    {
        foreach (var tab in Tabs.ToArray()) await tab.DisposeAsync();
        Tabs.Clear();

        if (_demoEngine is not null)
        {
            await _demoEngine.DisposeAsync();
            _demoEngine = null;
        }
    }

    private void RememberEngine(EngineProfile profile)
    {
        var existing = SavedEngines.FirstOrDefault(saved =>
            string.Equals(saved.Address, profile.Address, StringComparison.OrdinalIgnoreCase));

        if (existing is null)
        {
            SavedEngines.Insert(0, profile);
        }
        else
        {
            // Same endpoint, possibly new credentials: keep the saved entry in step.
            existing.Name = profile.Name;
            existing.AccessClientId = profile.AccessClientId;
            existing.AccessSecretProtected = profile.AccessSecretProtected;
        }

        _settings.Engines = SavedEngines.ToList();
        while (SavedEngines.Count > 8) SavedEngines.RemoveAt(SavedEngines.Count - 1);
    }
}

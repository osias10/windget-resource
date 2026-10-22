using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using ResourceStatus.Core;

namespace ResourceStatus.App;

/// <summary>A manual update flow. The application owns the service and installer handoff.</summary>
public sealed class UpdateWindow : Window
{
    private readonly IUpdateService _service;
    private readonly string _downloadDirectory;
    private readonly AppController? _controller;
    private readonly CheckBox _prereleases = new() { Name = "UpdatePrereleaseOption", Content = "프리릴리즈 업데이트 포함" };
    private readonly TextBlock _latest = new() { Name = "AvailableUpdateVersion", Text = "아직 확인하지 않음" };
    private readonly TextBlock _status = new() { Name = "UpdateStatus", Text = "업데이트 확인을 누르면 새 버전을 확인합니다." };
    private readonly TextBlock _progressText = new() { Name = "UpdateProgressText" };
    private readonly ProgressBar _progress = new() { Name = "UpdateProgress", Minimum = 0, Maximum = 100 };
    private readonly StackPanel _progressPanel = new() { Visibility = Visibility.Collapsed };
    private readonly Button _check = new() { Name = "CheckForUpdatesButton", Content = "업데이트 확인" };
    private readonly Button _action = new() { Name = "InstallUpdateButton", Content = "다운로드", Visibility = Visibility.Collapsed };
    private readonly Button _cancel = new() { Name = "CancelUpdateButton", Content = "취소", Visibility = Visibility.Collapsed };
    private CancellationTokenSource? _operation;
    private ReleaseUpdate? _available;
    private string? _downloadedInstaller;
    private bool _closed, _installing;
    private bool _includePrereleases, _syncingChannel;
    private int _channelGeneration;

    public event Action<string>? InstallRequested;
    internal bool IsBusy => _operation is not null || _installing;
    internal string StatusText => _status.Text;
    internal string ActionText => (string)_action.Content;
    internal bool CanInstall => _downloadedInstaller is not null && !IsBusy;
    internal CheckBox PrereleaseOption => _prereleases;
    internal string AvailableVersionText => _latest.Text;
    internal bool CanDownload => _available is not null && !IsBusy;

    public UpdateWindow(IUpdateService service, string downloadDirectory, AppController? controller = null)
    {
        _service = service;
        _downloadDirectory = downloadDirectory;
        _controller = controller;
        _includePrereleases = controller?.Settings.IncludePrereleaseUpdates ?? false;
        Title = "Resource Status 업데이트";
        Width = 480;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Theme.Background;
        Foreground = Theme.Text;
        FontFamily = Theme.FontFamily;
        FontSize = 12;
        UseLayoutRounding = true;

        var root = new StackPanel { Margin = new Thickness(24) };
        root.Children.Add(new TextBlock { Text = "앱 업데이트", FontSize = 22, FontWeight = FontWeights.SemiBold });
        root.Children.Add(new TextBlock
        {
            Text = "GitHub 릴리즈에서 새 버전을 확인합니다.", Foreground = Theme.Muted,
            Margin = new Thickness(0, 7, 0, 16), TextWrapping = TextWrapping.Wrap
        });
        _prereleases.IsChecked = _includePrereleases;
        _prereleases.Checked += OnPrereleaseOptionChanged;
        _prereleases.Unchecked += OnPrereleaseOptionChanged;
        root.Children.Add(_prereleases);
        root.Children.Add(new TextBlock
        {
            Text = "기본은 정식 릴리즈만 확인합니다. 프리릴리즈를 포함하면 개발 중인 버전도 받을 수 있습니다.",
            Foreground = Theme.Muted, FontSize = 11, TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(21, 6, 0, 20)
        });
        var versions = new Grid { Margin = new Thickness(14) };
        versions.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(90) });
        versions.ColumnDefinitions.Add(new ColumnDefinition());
        versions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        versions.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        versions.Children.Add(new TextBlock { Text = "현재 버전", Foreground = Theme.Muted });
        var current = new TextBlock { Name = "CurrentUpdateVersion", Text = "v" + UpdateService.CurrentVersionText, TextWrapping = TextWrapping.Wrap };
        Grid.SetColumn(current, 1); versions.Children.Add(current);
        var availableLabel = new TextBlock { Text = "새 버전", Foreground = Theme.Muted, Margin = new Thickness(0, 10, 0, 0) };
        Grid.SetRow(availableLabel, 1); versions.Children.Add(availableLabel);
        _latest.Margin = new Thickness(0, 10, 0, 0);
        _latest.TextWrapping = TextWrapping.Wrap;
        Grid.SetRow(_latest, 1); Grid.SetColumn(_latest, 1); versions.Children.Add(_latest);
        root.Children.Add(new Border
        {
            Background = Theme.Surface, BorderBrush = Theme.Border, BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7), Child = versions
        });
        _status.Margin = new Thickness(0, 18, 0, 0);
        _status.TextWrapping = TextWrapping.Wrap;
        _status.LineHeight = 20;
        AutomationProperties.SetLiveSetting(_status, AutomationLiveSetting.Polite);
        root.Children.Add(_status);
        _progress.Foreground = Theme.Cpu;
        _progress.Background = Theme.Track;
        _progress.BorderBrush = Theme.Border;
        _progress.Height = 7;
        _progress.Margin = new Thickness(0, 14, 0, 7);
        _progressText.Foreground = Theme.Muted;
        _progressPanel.Children.Add(_progress);
        _progressPanel.Children.Add(_progressText);
        root.Children.Add(_progressPanel);
        root.Children.Add(new TextBlock
        {
            Text = "설치된 앱은 업데이트 후 다시 시작합니다. 포터블 버전은 설치 마법사가 열립니다. 저장한 설정은 유지됩니다.",
            Foreground = Theme.Muted, TextWrapping = TextWrapping.Wrap, FontSize = 11,
            Margin = new Thickness(0, 16, 0, 0)
        });
        var actions = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 22, 0, 0) };
        foreach (var button in new[] { _cancel, _check, _action })
        {
            button.Padding = new Thickness(12, 8, 12, 8);
            button.Margin = new Thickness(8, 0, 0, 0);
            actions.Children.Add(button);
        }
        _check.Click += async (_, _) => await CheckForUpdatesAsync();
        _action.Click += async (_, _) => await DownloadOrInstallAsync();
        _cancel.Click += (_, _) => CancelOperation();
        root.Children.Add(actions);
        Content = root;
        SourceInitialized += (_, _) => WindowPlatform.ApplyTheme(this, Theme.IsDark);
        Theme.Changed += OnThemeChanged;
        if (_controller is not null) _controller.SettingsChanged += OnSettingsChanged;
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) { Close(); args.Handled = true; } };
        Closed += (_, _) =>
        {
            _closed = true;
            _operation?.Cancel();
            Theme.Changed -= OnThemeChanged;
            if (_controller is not null) _controller.SettingsChanged -= OnSettingsChanged;
        };
    }

    public async Task CheckForUpdatesAsync()
    {
        if (_closed || IsBusy) return;
        var operation = _operation = new CancellationTokenSource();
        int channel = _channelGeneration;
        bool includePrereleases = _includePrereleases;
        _available = null;
        _downloadedInstaller = null;
        _latest.Text = "확인 중…";
        _progressPanel.Visibility = Visibility.Collapsed;
        SetStatus("새 버전을 확인하고 있습니다.");
        UpdateButtons();
        try
        {
            var update = await _service.CheckAsync(UpdateService.CurrentReleaseVersion, includePrereleases, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (_closed || channel != _channelGeneration) return;
            _available = update;
            _latest.Text = update is null ? "업데이트 없음" : $"v{update.VersionText} · {(update.IsPrerelease ? "프리릴리즈" : "정식 릴리즈")}";
            SetStatus(update is null ? "선택한 채널에 현재 버전보다 새로운 업데이트가 없습니다."
                : $"새 버전을 사용할 수 있습니다. 다운로드 크기: {update.InstallerSize / (1024d * 1024):F1} MB");
        }
        catch (OperationCanceledException)
        {
            if (!_closed && channel == _channelGeneration) { _latest.Text = "확인하지 못함"; SetStatus(operation.IsCancellationRequested ? "업데이트 확인을 취소했습니다." : "연결 시간이 초과되었습니다. 다시 확인해 주세요."); }
        }
        catch (Exception error)
        {
            if (!_closed && channel == _channelGeneration) { _latest.Text = "확인하지 못함"; SetStatus(ErrorMessage(error), true); }
        }
        finally
        {
            _operation = null;
            operation.Dispose();
            if (!_closed) UpdateButtons();
        }
    }

    internal async Task DownloadOrInstallAsync()
    {
        if (_closed || IsBusy || _available is null) return;
        int channel = _channelGeneration;
        if (_downloadedInstaller is { } installer)
        {
            var verification = _operation = new CancellationTokenSource();
            bool verified = false;
            _installing = true;
            UpdateButtons();
            SetStatus("설치 파일을 다시 확인하고 있습니다.");
            try
            {
                await _service.VerifyDownloadAsync(installer, verification.Token);
                verification.Token.ThrowIfCancellationRequested();
                if (_closed || channel != _channelGeneration) return;
                verified = true;
                SetStatus("설치 프로그램을 시작하고 있습니다.");
                if (InstallRequested is null) throw new InvalidOperationException("Installer handoff is unavailable.");
                InstallRequested.Invoke(installer);
            }
            catch (OperationCanceledException)
            {
                _installing = false;
                if (!_closed && channel == _channelGeneration) SetStatus("설치를 취소했습니다. 준비된 파일로 다시 시도할 수 있습니다.");
            }
            catch (Exception error)
            {
                _installing = false;
                if (channel == _channelGeneration)
                {
                    if (!verified) _downloadedInstaller = null;
                    ShowInstallError(verified ? "설치 프로그램을 시작하지 못했습니다. 다시 시도해 주세요." : ErrorMessage(error));
                }
            }
            finally
            {
                _operation = null;
                verification.Dispose();
                if (!_closed) UpdateButtons();
            }
            return;
        }

        var operation = _operation = new CancellationTokenSource();
        _progressPanel.Visibility = Visibility.Visible;
        _progress.Value = 0;
        _progressText.Text = "0%";
        SetStatus("업데이트 파일을 다운로드하고 있습니다.");
        UpdateButtons();
        var progress = new Progress<double>(value =>
        {
            if (_closed || channel != _channelGeneration || !ReferenceEquals(_operation, operation) || operation.IsCancellationRequested) return;
            _progress.Value = double.IsFinite(value) ? Math.Clamp(value, 0, 100) : 0;
            _progressText.Text = $"{_progress.Value:F0}%";
            if (_progress.Value >= 100) SetStatus("다운로드한 파일을 확인하고 있습니다.");
        });
        try
        {
            string downloadPath = await _service.DownloadAsync(_available, _downloadDirectory, progress, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (_closed || channel != _channelGeneration) return;
            _downloadedInstaller = downloadPath;
            _progress.Value = 100;
            _progressText.Text = "다운로드 및 파일 확인 완료";
            SetStatus("설치 준비가 완료되었습니다. 설치하고 다시 시작을 눌러 적용하세요.");
        }
        catch (OperationCanceledException)
        {
            if (!_closed && channel == _channelGeneration) { _progressPanel.Visibility = Visibility.Collapsed; SetStatus(operation.IsCancellationRequested ? "다운로드를 취소했습니다. 다시 다운로드할 수 있습니다." : "연결 시간이 초과되었습니다. 다시 다운로드해 주세요."); }
        }
        catch (Exception error)
        {
            if (!_closed && channel == _channelGeneration) { _progressPanel.Visibility = Visibility.Collapsed; SetStatus(ErrorMessage(error), true); }
        }
        finally
        {
            _operation = null;
            operation.Dispose();
            if (!_closed) UpdateButtons();
        }
    }

    internal void CancelOperation()
    {
        if (_operation is null) return;
        _operation.Cancel();
        _cancel.IsEnabled = false;
        SetStatus("취소하는 중입니다.");
    }

    public void ShowInstallError(string message)
    {
        if (_closed) return;
        _installing = false;
        SetStatus(message, true);
        UpdateButtons();
    }

    private void UpdateButtons()
    {
        _prereleases.IsEnabled = !IsBusy;
        _check.IsEnabled = !IsBusy;
        _action.IsEnabled = !IsBusy;
        _action.Visibility = _available is null ? Visibility.Collapsed : Visibility.Visible;
        _action.Content = _downloadedInstaller is null ? "다운로드" : "설치하고 다시 시작";
        _cancel.Visibility = _operation is null ? Visibility.Collapsed : Visibility.Visible;
        _cancel.IsEnabled = _operation is not null && !_operation.IsCancellationRequested;
    }

    private void OnPrereleaseOptionChanged(object sender, RoutedEventArgs e)
    {
        if (_syncingChannel) return;
        ChangeChannel(_prereleases.IsChecked == true);
        if (_controller is not null)
        {
            _controller.Settings.IncludePrereleaseUpdates = _includePrereleases;
            _controller.Save();
            if (_controller.SaveError is { } error) SetStatus(error, true);
        }
    }

    private void OnSettingsChanged(object? sender, EventArgs e)
    {
        if (_controller is not null && !_closed) ChangeChannel(_controller.Settings.IncludePrereleaseUpdates);
    }

    private void ChangeChannel(bool includePrereleases)
    {
        if (_includePrereleases == includePrereleases) return;
        _includePrereleases = includePrereleases;
        _channelGeneration++;
        _operation?.Cancel();
        _available = null;
        _downloadedInstaller = null;
        _installing = false;
        _syncingChannel = true;
        try { _prereleases.IsChecked = includePrereleases; }
        finally { _syncingChannel = false; }
        _latest.Text = "다시 확인 필요";
        _progressPanel.Visibility = Visibility.Collapsed;
        SetStatus("업데이트 채널을 변경했습니다. 업데이트 확인을 눌러 다시 확인하세요.");
        UpdateButtons();
    }

    private void SetStatus(string message, bool error = false)
    {
        _status.Text = message;
        _status.Foreground = error ? Theme.Warning : Theme.Text;
    }

    private static string ErrorMessage(Exception error) => error switch
    {
        ReleaseUpdateException => error.Message,
        HttpRequestException => "업데이트 서버에 연결하지 못했습니다. 인터넷 연결을 확인한 뒤 다시 시도해 주세요.",
        UnauthorizedAccessException => "업데이트 파일을 저장할 수 없습니다. 폴더 접근 권한을 확인해 주세요.",
        IOException => "업데이트 파일을 저장하지 못했습니다. 디스크 공간을 확인한 뒤 다시 시도해 주세요.",
        _ => "업데이트를 처리하지 못했습니다. 잠시 후 다시 시도해 주세요."
    };

    private void OnThemeChanged(object? sender, EventArgs e) => WindowPlatform.ApplyTheme(this, Theme.IsDark);
}

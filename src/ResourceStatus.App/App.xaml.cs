using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using ResourceStatus.Core;

namespace ResourceStatus.App;

public partial class App : Application
{
    private AppController? _controller;
    private MainWindow? _widget;
    private TrayService? _tray;
    private UpdateService? _updateService;
    private UpdateWindow? _updateWindow;
    private string _updatesDirectory = string.Empty;
    private Mutex? _mutex;
    private EventWaitHandle? _activate;
    private RegisteredWaitHandle? _activationWait;
    private bool _exiting, _started;
    private readonly Queue<long> _failures = new();
    private string _logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ResourceStatus", "error.log");

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? Arg(string name) { int index = Array.IndexOf(e.Args, name); return index >= 0 && index + 1 < e.Args.Length ? e.Args[index + 1] : null; }
        bool smoke = e.Args.Contains("--smoke-test"), demo = smoke || e.Args.Contains("--demo");
        string? settingsDir = Arg("--settings-dir");
        string dataDir = settingsDir ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), demo ? "ResourceStatus.Demo" : "ResourceStatus");
        _logPath = Path.Combine(dataDir, "error.log");
        DispatcherUnhandledException += (_, error) =>
        {
            WriteError(error.Exception);
            error.Handled = true;
            // A resident widget survives transient UI faults; exit only during startup or on a burst (3 within 60 s).
            long now = Environment.TickCount64;
            _failures.Enqueue(now);
            while (now - _failures.Peek() > 60_000) _failures.Dequeue();
            if (!smoke && _started && _failures.Count < 3) return;
            if (_exiting) return;
            if (!smoke) MessageBox.Show("오류가 발생했습니다. 앱을 종료합니다.\n" + error.Exception.Message, "Resource Status", MessageBoxButton.OK, MessageBoxImage.Error);
            ExitApp(1);
        };
        try
        {
            if (e.Args.Contains("--probe"))
            {
                var duration = int.TryParse(Arg("--seconds"), out var seconds) ? Math.Clamp(seconds, 3, 3600) : 15;
                var output = Arg("--output") ?? Path.Combine(dataDir, "probe.json");
                await Diagnostics.RunProbeAsync(duration, output);
                Shutdown();
                return;
            }
            if (!demo && settingsDir is null)
            {
                _mutex = new Mutex(true, @"Local\ResourceStatus.Widget", out bool first);
                if (!first)
                {
                    try { using var existing = EventWaitHandle.OpenExisting(@"Local\ResourceStatus.Activate"); existing.Set(); }
                    catch (WaitHandleCannotBeOpenedException) { }
                    Shutdown(); return;
                }
                _activate = new EventWaitHandle(false, EventResetMode.AutoReset, @"Local\ResourceStatus.Activate");
                _activationWait = ThreadPool.RegisterWaitForSingleObject(_activate, (_, _) => Dispatcher.BeginInvoke(new Action(() => _widget?.ShowWidget())), null, Timeout.Infinite, false);
            }
            _controller = new AppController(Path.Combine(dataDir, "settings.json"), demo);
            _updatesDirectory = Path.Combine(dataDir, "Updates");
            _controller.UpdateRequested += ShowUpdates;
            Theme.Apply(_controller.Settings.Theme);
            if (e.Args.Contains("--expanded")) _controller.Settings.WidgetMode = WidgetMode.Expanded;
            if (smoke || demo)
            {
                using var provider = new DemoMetricsProvider();
                for (int i = 60; i > 0; i--)
                    _controller.History.Add(provider.Read(null, null) with { Timestamp = DateTimeOffset.UtcNow.AddSeconds(-i) });
            }
            _widget = new MainWindow(_controller);
            MainWindow = _widget;
            _widget.ExitRequested += (_, _) => ExitApp();
            if (!smoke) _tray = new TrayService(_widget, _controller, () => ExitApp());
            _widget.Show();
            _controller.Start();
            _started = true;
            if (e.Args.Contains("--settings")) _widget.ShowSettings();
            if (smoke)
            {
                string output = Arg("--output") ?? Path.Combine(dataDir, "smoke.json");
                await UiSmoke.RunAsync(_controller, _widget, output);
                ExitApp();
            }
            else if (Arg("--screenshot") is { } imagePath)
            {
                await Task.Delay(2000);
                UiSmoke.SaveScreenshot(_widget.WidgetFrame, imagePath, 1);
                ExitApp();
            }
        }
        catch (Exception ex) { WriteError(ex); ExitApp(1); }
    }

    private void WriteError(Exception error)
    {
        try { Directory.CreateDirectory(Path.GetDirectoryName(_logPath)!); File.AppendAllText(_logPath, DateTimeOffset.Now + "\n" + error + "\n"); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async void ShowUpdates(object? sender, EventArgs e)
    {
        if (_exiting) return;
        if (_updateWindow is not null)
        {
            _updateWindow.Show();
            _updateWindow.Activate();
            return;
        }
        _updateService ??= new UpdateService();
        var window = new UpdateWindow(_updateService, _updatesDirectory);
        _updateWindow = window;
        window.Closed += (_, _) => { if (ReferenceEquals(_updateWindow, window)) _updateWindow = null; };
        window.InstallRequested += path =>
        {
            try
            {
                // The setup waits for this process to exit before checking the app
                // mutex or replacing files. A launch failure leaves the widget open.
                UpdateInstaller.Start(path, _updatesDirectory);
                ExitApp();
            }
            catch (Exception error) when (error is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                WriteError(error);
                window.ShowInstallError("설치 프로그램을 시작하지 못했습니다. 다시 시도해 주세요.");
            }
        };
        window.Show();
        window.Activate();
        await window.CheckForUpdatesAsync();
    }

    private void ExitApp(int code = 0)
    {
        if (Cleanup()) Shutdown(code);
    }

    // Logoff/shutdown bypasses ExitApp; run the same one-time cleanup (tray icon, placement save, mutex).
    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        base.OnSessionEnding(e);
        if (!e.Cancel) Cleanup();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        Cleanup();
        base.OnExit(e);
    }

    private bool Cleanup()
    {
        if (_exiting) return false;
        _exiting = true;
        if (_controller is not null) _controller.UpdateRequested -= ShowUpdates;
        _updateWindow?.Close();
        _updateService?.Dispose();
        _controller?.Dispose();
        _tray?.Dispose();
        _activationWait?.Unregister(null);
        _activate?.Dispose();
        // The widget may already be closed when WPF itself is shutting down.
        if (_widget is not null && PresentationSource.FromVisual(_widget) is not null) { _widget.AllowClose = true; _widget.Close(); }
        _mutex?.Dispose();
        return true;
    }
}

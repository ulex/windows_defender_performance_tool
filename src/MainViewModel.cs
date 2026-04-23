using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reactive;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;
using Newtonsoft.Json;
using ReactiveUI;
using ScottPlot;

namespace WindowsDefenderPerformanceTool;

public class MainViewModel : ReactiveObject, IDisposable
{
    private const int TopN = 10;

    // Central relay: both live and file listeners feed into this
    private readonly Subject<EventInfo> _eventsRelay = new();

    private readonly Plotter _plotter;
    private readonly EtwRecorder _recorder = new();
    private EtwListener? _liveListener;

    private IDisposable? _liveSubscription;
    private IDisposable? _liveRawSubscription;
    private IDisposable? _fileSubscription;
    private IDisposable? _fileRawSubscription;
    private IDisposable? _statsSubscription;
    private EtwListener? _fileListener;
    private readonly DispatcherTimer _cpuTimer;
    private TimeSpan? _cpuKernelBaseline;
    private TimeSpan? _cpuUserBaseline;

    // Stats (accessed only on main thread via ObserveOn)
    private readonly Dictionary<string, double> _processTotals = new();
    private readonly Dictionary<string, double> _fileTotals = new();
    private double _totalScannedMs;

    // Reactive properties
    private double _totalScannedSeconds;
    public double TotalScannedSeconds
    {
        get => _totalScannedSeconds;
        private set => this.RaiseAndSetIfChanged(ref _totalScannedSeconds, value);
    }

    private long _totalEventsProcessed;
    public long TotalEventsProcessed
    {
        get => _totalEventsProcessed;
        private set => this.RaiseAndSetIfChanged(ref _totalEventsProcessed, value);
    }

    private bool _cpuTimesAvailable;
    public bool CpuTimesAvailable
    {
        get => _cpuTimesAvailable;
        private set => this.RaiseAndSetIfChanged(ref _cpuTimesAvailable, value);
    }

    private string _kernelTimeText = "";
    public string KernelTimeText
    {
        get => _kernelTimeText;
        private set => this.RaiseAndSetIfChanged(ref _kernelTimeText, value);
    }

    private string _userTimeText = "";
    public string UserTimeText
    {
        get => _userTimeText;
        private set => this.RaiseAndSetIfChanged(ref _userTimeText, value);
    }

    private string _totalCpuTimeText = "";
    public string TotalCpuTimeText
    {
        get => _totalCpuTimeText;
        private set => this.RaiseAndSetIfChanged(ref _totalCpuTimeText, value);
    }

    private string _cpuStatusMessage = "";
    public string CpuStatusMessage
    {
        get => _cpuStatusMessage;
        private set => this.RaiseAndSetIfChanged(ref _cpuStatusMessage, value);
    }

    private string? _cpuStatusTooltip;
    public string? CpuStatusTooltip
    {
        get => _cpuStatusTooltip;
        private set => this.RaiseAndSetIfChanged(ref _cpuStatusTooltip, value);
    }

    private bool _isRecording;
    public bool IsRecording
    {
        get => _isRecording;
        private set => this.RaiseAndSetIfChanged(ref _isRecording, value);
    }

    private string _recordingName = "";
    public string RecordingName
    {
        get => _recordingName;
        set => this.RaiseAndSetIfChanged(ref _recordingName, value);
    }

    private string _snapshotName = "";
    public string SnapshotName
    {
        get => _snapshotName;
        private set => this.RaiseAndSetIfChanged(ref _snapshotName, value);
    }

    private string _windowTitle = "Windows Defender Performance Tool";
    public string WindowTitle
    {
        get => _windowTitle;
        private set => this.RaiseAndSetIfChanged(ref _windowTitle, value);
    }

    public bool IsRunningAsAdmin { get; } =
        new WindowsPrincipal(WindowsIdentity.GetCurrent()).IsInRole(WindowsBuiltInRole.Administrator);

    public bool IsNotRunningAsAdmin => !IsRunningAsAdmin;

    public ImageSource? UacShieldIcon { get; } = LoadUacShieldIcon();

    public ObservableCollection<ScanStat> TopProcesses { get; } = new ObservableCollection<ScanStat>();
    public ObservableCollection<ScanStat> TopFiles { get; } = new ObservableCollection<ScanStat>();

    // Exposes the ScottPlot control for the View
    public WpfPlot PlotControl => _plotter.WpfPlot;

    public ReactiveCommand<Unit, Unit> CopyHumanReadableCommand { get; }
    public ReactiveCommand<Unit, Unit> CopyJsonCommand { get; }
    public ReactiveCommand<Unit, Unit> ResetCommand { get; }
    public ReactiveCommand<Unit, Unit> StartRecordingCommand { get; }
    public ReactiveCommand<Unit, Unit> StopRecordingCommand { get; }
    public ReactiveCommand<Unit, Unit> OpenEtlFileCommand { get; }
    public ReactiveCommand<Unit, Unit> RestartAsAdminCommand { get; }

    public MainViewModel(bool startLiveMonitoring = true)
    {
        _plotter = new Plotter(_eventsRelay.AsObservable());

        // Stats subscription on main thread
        _statsSubscription = _eventsRelay
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(OnEvent);

        // Keep title in sync with snapshot name
        this.WhenAnyValue(x => x.SnapshotName)
            .Subscribe(name =>
            {
                WindowTitle = string.IsNullOrEmpty(name)
                    ? "Windows Defender Performance Tool"
                    : $"Windows Defender Performance Tool — {name}";
            });

        // Live monitoring requires admin
        if (startLiveMonitoring && IsRunningAsAdmin)
        {
            _liveListener = new EtwListener();
            _liveSubscription = _liveListener.Events.Subscribe(
                e => _eventsRelay.OnNext(e),
                _ => { });
            _liveRawSubscription = _liveListener.RawEventCount
                .Sample(TimeSpan.FromSeconds(1))
                .ObserveOn(RxApp.MainThreadScheduler)
                .Subscribe(count => TotalEventsProcessed = count);
            _liveListener.Start();
        }

        var canStart = this.WhenAnyValue(
            x => x.IsRecording,
            x => x.RecordingName,
            (rec, name) => !rec && !string.IsNullOrWhiteSpace(name));

        var canStop = this.WhenAnyValue(x => x.IsRecording);

        CopyHumanReadableCommand = ReactiveCommand.Create(CopyHumanReadable);
        CopyJsonCommand = ReactiveCommand.Create(CopyJson);
        ResetCommand = ReactiveCommand.Create(Reset);
        StartRecordingCommand = ReactiveCommand.Create(StartRecording, canStart);
        StopRecordingCommand = ReactiveCommand.Create(StopRecording, canStop);
        OpenEtlFileCommand = ReactiveCommand.Create(OpenEtlFile);
        RestartAsAdminCommand = ReactiveCommand.Create(RestartAsAdmin);

        _cpuTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _cpuTimer.Tick += (_, __) => PollCpuTimes();
        _cpuTimer.Start();
        PollCpuTimes();
    }

    private void OnEvent(EventInfo info)
    {
        _totalScannedMs += info.DurationMsec;
        TotalScannedSeconds = _totalScannedMs / 1000.0;

        if (!string.IsNullOrEmpty(info.Process))
        {
            _processTotals.TryGetValue(info.Process, out var existing);
            _processTotals[info.Process] = existing + info.DurationMsec;
        }

        if (!string.IsNullOrEmpty(info.FilePath))
        {
            _fileTotals.TryGetValue(info.FilePath, out var existing);
            _fileTotals[info.FilePath] = existing + info.DurationMsec;
        }

        RefreshTopLists();
    }

    private void PollCpuTimes()
    {
        switch (MsMpEngCpuInfo.Query())
        {
            case CpuTimesSuccess s:
                if (_cpuKernelBaseline == null)
                {
                    _cpuKernelBaseline = s.KernelTime;
                    _cpuUserBaseline = s.UserTime;
                }
                var kernel = s.KernelTime - _cpuKernelBaseline.Value;
                var user = s.UserTime - _cpuUserBaseline!.Value;
                KernelTimeText = MsMpEngCpuInfo.FormatTime(kernel);
                UserTimeText = MsMpEngCpuInfo.FormatTime(user);
                TotalCpuTimeText = MsMpEngCpuInfo.FormatTime(kernel + user);
                CpuStatusTooltip = null;
                CpuTimesAvailable = true;
                break;
            case CpuNotRunning:
                CpuStatusMessage = "Windows Defender is not running";
                CpuStatusTooltip = null;
                CpuTimesAvailable = false;
                break;
            case CpuError e:
                CpuStatusMessage = $"Unable to query CPU counters: {e.Message}";
                CpuStatusTooltip = e.Source.ToString();
                CpuTimesAvailable = false;
                break;
        }
    }

    private void RefreshTopLists()
    {
        SyncCollection(TopProcesses,
            _processTotals
                .OrderByDescending(kvp => kvp.Value)
                .Take(TopN)
                .Select(kvp => new ScanStat(kvp.Key, kvp.Value / 1000.0)));

        SyncCollection(TopFiles,
            _fileTotals
                .OrderByDescending(kvp => kvp.Value)
                .Take(TopN)
                .Select(kvp => new ScanStat(kvp.Key, kvp.Value / 1000.0)));
    }

    private static void SyncCollection(ObservableCollection<ScanStat> collection, IEnumerable<ScanStat> items)
    {
        collection.Clear();
        foreach (var item in items)
            collection.Add(item);
    }

    private void Reset()
    {
        _processTotals.Clear();
        _fileTotals.Clear();
        _totalScannedMs = 0;
        TotalScannedSeconds = 0;
        TotalEventsProcessed = 0;
        TopProcesses.Clear();
        TopFiles.Clear();
        _plotter.Reset();
        _cpuKernelBaseline = null;
        _cpuUserBaseline = null;
        PollCpuTimes();
    }

    private void CopyHumanReadable()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Windows Defender Scan Statistics");
        sb.AppendLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        sb.AppendLine($"Total Scanned Time: {TotalScannedSeconds:F2}s");
        sb.AppendLine();
        sb.AppendLine("Top Processes:");
        foreach (var stat in TopProcesses)
            sb.AppendLine($"  {stat.Name}: {stat.TotalSeconds:F2}s");
        sb.AppendLine();
        sb.AppendLine("Top Files:");
        foreach (var stat in TopFiles)
            sb.AppendLine($"  {stat.Name}: {stat.TotalSeconds:F2}s");
        Clipboard.SetText(sb.ToString());
    }

    private void CopyJson()
    {
        var payload = new
        {
            generatedAt = DateTime.Now,
            totalScannedSeconds = TotalScannedSeconds,
            topProcesses = TopProcesses.Select(s => new { name = s.Name, totalSeconds = s.TotalSeconds }),
            topFiles = TopFiles.Select(s => new { path = s.Name, totalSeconds = s.TotalSeconds })
        };
        Clipboard.SetText(JsonConvert.SerializeObject(payload, Formatting.Indented));
    }

    private void StartRecording()
    {
        var dir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Recordings");
        var filePath = Path.Combine(dir, RecordingName.Trim() + ".etl");
        _recorder.Start(filePath);
        IsRecording = true;
    }

    private void StopRecording()
    {
        _recorder.Stop();
        IsRecording = false;
    }

    private void OpenEtlFile()
    {
        var dialog = new OpenFileDialog
        {
            Filter = "ETL files (*.etl)|*.etl|All files (*.*)|*.*",
            Title = "Open ETL Recording"
        };
        if (dialog.ShowDialog() != true) return;

        LoadEtlFile(dialog.FileName);
    }

    public void LoadEtlFile(string filePath)
    {
        _fileSubscription?.Dispose();
        _fileRawSubscription?.Dispose();
        _fileListener?.Dispose();

        Reset();

        SnapshotName = Path.GetFileName(filePath);

        _fileListener = new EtwListener(filePath);
        _fileSubscription = _fileListener.Events.Subscribe(
            e => _eventsRelay.OnNext(e),
            _ => { });
        _fileRawSubscription = _fileListener.RawEventCount
            .Sample(TimeSpan.FromSeconds(1))
            .ObserveOn(RxApp.MainThreadScheduler)
            .Subscribe(count => TotalEventsProcessed = count);
        _fileListener.Start();
    }

    private static void RestartAsAdmin()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = Process.GetCurrentProcess().MainModule.FileName,
                UseShellExecute = true,
                Verb = "runas"
            });
            Application.Current.Shutdown();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // User cancelled UAC prompt — stay running
        }
    }

    // --- UAC shield icon via SHGetStockIconInfo ---

    private const uint SIID_SHIELD = 77;
    private const uint SHGSI_ICON = 0x100;
    private const uint SHGSI_SMALLICON = 0x1;

    [DllImport("shell32.dll")]
    private static extern int SHGetStockIconInfo(uint siid, uint uFlags, ref SHSTOCKICONINFO psii);

    [DllImport("user32.dll")]
    private static extern bool DestroyIcon(IntPtr hIcon);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHSTOCKICONINFO
    {
        public uint cbSize;
        public IntPtr hIcon;
        public int iSysIconIndex;
        public int iIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string szPath;
    }

    private static ImageSource? LoadUacShieldIcon()
    {
        var info = new SHSTOCKICONINFO { cbSize = (uint)Marshal.SizeOf<SHSTOCKICONINFO>() };
        if (SHGetStockIconInfo(SIID_SHIELD, SHGSI_ICON | SHGSI_SMALLICON, ref info) != 0)
            return null;
        try
        {
            return Imaging.CreateBitmapSourceFromHIcon(
                info.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
        }
        finally
        {
            DestroyIcon(info.hIcon);
        }
    }

    public void Dispose()
    {
        _cpuTimer.Stop();
        _statsSubscription?.Dispose();
        _liveSubscription?.Dispose();
        _liveRawSubscription?.Dispose();
        _fileSubscription?.Dispose();
        _fileRawSubscription?.Dispose();
        _liveListener?.Dispose();
        _fileListener?.Dispose();
        _recorder.Dispose();
        _plotter.Dispose();
        _eventsRelay.OnCompleted();
        _eventsRelay.Dispose();
    }
}

using System;
using System.IO;
using Microsoft.Diagnostics.Tracing.Session;

namespace WindowsDefenderPerformanceTool;

public sealed class EtwRecorder : IDisposable
{
    private const string SessionName = "WindowsDefenderPerformanceToolRecordingSession";

    private TraceEventSession? _session;

    public void Start(string filePath)
    {
        _session?.Dispose();
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        _session = new TraceEventSession(SessionName, filePath);
        _session.EnableProvider("Microsoft-Antimalware-Engine");
    }

    public void Stop()
    {
        _session?.Stop();
        _session?.Dispose();
        _session = null;
    }

    public void Dispose() => Stop();
}

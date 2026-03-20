using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reactive.Subjects;
using System.Threading;
using Microsoft.Diagnostics.Tracing;
using Microsoft.Diagnostics.Tracing.Session;
using static WindowsDefenderMonitoring.MicrosoftAntimalwareEngineEvents;

namespace WindowsDefenderMonitoring;

public sealed class EtwListener : IDisposable
{
    private const string SessionName = "WindowsDefenderMonitoringSession";

    private readonly Subject<EventInfo> _eventSubject = new();
    private readonly Subject<long> _rawEventCount = new();
    private readonly TraceEventSession? _session;
    private readonly ETWTraceEventSource _source;
    private readonly Thread _processingThread;
    private bool _disposed;
    private long _rawCount;

    private readonly ConcurrentDictionary<Guid, (DateTime Timestamp, string Process, string FilePath)> _pendingStarts = new();

    public IObservable<EventInfo> Events => _eventSubject;

    /// <summary>Emits the running total of raw ETW events received (all opcodes, not just matched pairs).</summary>
    public IObservable<long> RawEventCount => _rawEventCount;

    /// <summary>Real-time ETW session mode. Requires administrator privileges.</summary>
    public EtwListener()
    {
        _session = new TraceEventSession(SessionName);
        _session.EnableProvider("Microsoft-Antimalware-Engine", TraceEventLevel.Informational);
        _source = _session.Source;
        _processingThread = CreateProcessingThread(completesOnReturn: false);
    }

    /// <summary>ETL file replay mode. Processes all events from the file then completes.</summary>
    public EtwListener(string etlFilePath)
    {
        _source = new ETWTraceEventSource(etlFilePath);
        _processingThread = CreateProcessingThread(completesOnReturn: true);
    }

    /// <summary>Starts processing events. Subscribe to <see cref="Events"/> before calling this.</summary>
    public void Start() => _processingThread.Start();

    private Thread CreateProcessingThread(bool completesOnReturn)
    {
        _source.Dynamic.All += OnEvent;

        return new Thread(() =>
        {
            try
            {
                _source.Process();
            }
            catch (Exception)
            {
            }
            finally
            {
                if (completesOnReturn)
                    _eventSubject.OnCompleted();
            }
        })
        {
            IsBackground = true,
            Name = "ETW Processing Thread"
        };
    }

    private void OnEvent(TraceEvent data)
    {
        _rawEventCount.OnNext(Interlocked.Increment(ref _rawCount));

        switch ((MicrosoftAntimalwareEngineEvents)data.ID)
        {
            case StreamscanrequestStart:
                try
                {
                    var process = Path.GetFileName(data.PayloadByName("Process")?.ToString() ?? "Unknown");
                    var filePath = data.PayloadByName("Path")?.ToString() ?? "";
                    _pendingStarts[data.ActivityID] = (data.TimeStamp, process, filePath);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                }

                break;

            case StreamscanrequestStop:
                try
                {
                    if (!_pendingStarts.TryRemove(data.ActivityID, out var startInfo)) break;
                    var durationMsec = (data.TimeStamp - startInfo.Timestamp).TotalMilliseconds;
                    _eventSubject.OnNext(new EventInfo(durationMsec, startInfo.Process, data.TimeStamp,
                        startInfo.FilePath));
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                }

                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _source.Dynamic.All -= OnEvent;

        if (_session is not null)
        {
            _session.Stop();
            _session.Dispose();
        }
        else
        {
            _source.Dispose();
        }

        _eventSubject.OnCompleted();
        _eventSubject.Dispose();
        _rawEventCount.OnCompleted();
        _rawEventCount.Dispose();
    }
}

# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build & Run

```bash
# Build (debug)
dotnet build

# Publish (release)
dotnet publish -c Release
# Output: bin\Release\net48\publish\

# Run (requires administrator privileges — UAC prompt auto-triggered if not elevated)
./bin/Release/net48/publish/WindowsDefenderMonitoring.exe
```

There are no automated tests; verification is manual by running the app and observing ETW events during a Windows Defender scan.

## Architecture

A WPF application with three components wired together in `Program.cs`:

**`EtwListener.cs`** — subscribes to the `Microsoft-Antimalware-Engine` ETW provider on a background thread. Correlates Start/Stop `Streamscanrequest` events using ActivityID (Guid), calculates scan duration, and emits `EventInfo` records via a reactive `Subject<EventInfo>`.

**`Plotter.cs`** — subscribes to the `IObservable<EventInfo>` stream. Accumulates durations per-process per-second in a `ConcurrentDictionary`, then a `DispatcherTimer` fires every second to atomically swap buffers (via `Interlocked.Exchange`), shift a 16-element circular history buffer, and redraw a ScottPlot stacked bar chart showing seconds of scan activity over the last 16 seconds.

**`Program.cs`** — checks for admin elevation (auto-elevates via UAC if needed), creates the WPF `Application`, instantiates `EtwListener` and `Plotter`, and hosts a `WpfPlot` control in a 1000×600 window.

**`EventInfo.cs`** — immutable record: `DurationMsec`, `Process` (filename only), `Timestamp`.

### Key constraints
- Windows-only, x64, requires administrator privileges
- Target framework: `net48`; output type: `WinExe`; ScottPlot 4.1.x (not 5.x)
- Thread safety relies on `ConcurrentDictionary` and `Interlocked.Exchange` — no explicit locks
- Color palette caps consistent process coloring at 16 entries

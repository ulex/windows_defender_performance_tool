# Windows Defender Performance Tool

A .NET application that monitors Windows Defender ETW (Event Tracing for Windows) events and visualizes scan durations in real-time using a stacked bar chart.

## Features

- Listens to `Microsoft-Antimalware-Engine/StreamScanRequestTask/Stop` ETW events
- Displays scan durations per process in a stacked bar chart
- Shows 16 seconds of historical data, updating every second
- Auto-elevation to administrator privileges (required for ETW)

## Build & Publish

Build as a single-file executable:

```bash
dotnet publish -c Release
```

The output will be at `bin\Release\net48\publish\WindowsDefenderPerformanceTool.exe`

## Installation

Copy the published `WindowsDefenderPerformanceTool.exe` to a folder in your PATH, or create an alias:

```powershell
# Add to PowerShell profile for easy access
Set-Alias wdperf "C:\path\to\WindowsDefenderPerformanceTool.exe"
```

## Usage

Run the executable:

```bash
WindowsDefenderPerformanceTool.exe
```

Or if you set up the alias:

```bash
wdmon
```

**Note:** Administrator privileges are required. The tool will automatically prompt for elevation if not running as administrator.

## Requirements

- Windows 10/11 (x64)
- Administrator privileges (for ETW access)

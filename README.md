# Windows Defender Monitoring Tool

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

The output will be at `bin\Release\net10.0-windows\win-x64\publish\WindowsDefenderMonitoring.exe`

## Installation

Copy the published `WindowsDefenderMonitoring.exe` to a folder in your PATH, or create an alias:

```powershell
# Add to PowerShell profile for easy access
Set-Alias wdmon "C:\path\to\WindowsDefenderMonitoring.exe"
```

## Usage

Run the executable:

```bash
WindowsDefenderMonitoring.exe
```

Or if you set up the alias:

```bash
wdmon
```

**Note:** Administrator privileges are required. The tool will automatically prompt for elevation if not running as administrator.

## Requirements

- Windows 10/11 (x64)
- Administrator privileges (for ETW access)

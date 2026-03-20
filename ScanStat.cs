using System;
using System.IO;

namespace WindowsDefenderMonitoring;

public record ScanStat(string Name, double TotalSeconds)
{
    /// <summary>Filename only — used for display. Falls back to Name when no directory separator is present.</summary>
    public string ShortName => string.IsNullOrEmpty(Name) ? "" : (Path.GetFileName(Name) is { Length: > 0 } f ? f : Name);
}

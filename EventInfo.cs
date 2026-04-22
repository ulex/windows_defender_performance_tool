using System;

namespace WindowsDefenderPerformanceTool;

public record EventInfo(double DurationMsec, string Process, DateTime Timestamp, string FilePath = "");

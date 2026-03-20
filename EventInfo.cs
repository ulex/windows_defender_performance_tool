using System;

namespace WindowsDefenderMonitoring;

public record EventInfo(double DurationMsec, string Process, DateTime Timestamp, string FilePath = "");

using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace WindowsDefenderPerformanceTool;

internal abstract record CpuQueryResult;
internal sealed record CpuTimesSuccess(TimeSpan KernelTime, TimeSpan UserTime) : CpuQueryResult;
internal sealed record CpuNotRunning() : CpuQueryResult;
internal sealed record CpuAccessDenied() : CpuQueryResult;
internal sealed record CpuError(string Message, Exception Source) : CpuQueryResult;

internal static class MsMpEngCpuInfo
{
    private const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    private struct FILETIME
    {
        public uint dwLowDateTime;
        public uint dwHighDateTime;

        public TimeSpan ToTimeSpan() =>
            TimeSpan.FromTicks(((long)dwHighDateTime << 32) | dwLowDateTime);
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(uint dwDesiredAccess, bool bInheritHandle, int dwProcessId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetProcessTimes(
        IntPtr hProcess,
        out FILETIME lpCreationTime,
        out FILETIME lpExitTime,
        out FILETIME lpKernelTime,
        out FILETIME lpUserTime);

    public static CpuQueryResult Query()
    {
        Process[] procs;
        try
        {
            procs = Process.GetProcessesByName("MsMpEng");
        }
        catch (Exception ex)
        {
            return new CpuError("process enumeration failed", ex);
        }

        if (procs.Length == 0)
            return new CpuNotRunning();

        int pid = procs[0].Id;
        foreach (var p in procs) p.Dispose();

        var handle = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == IntPtr.Zero)
        {
            int err = Marshal.GetLastWin32Error();
            return err == 5 // ERROR_ACCESS_DENIED
                ? new CpuAccessDenied()
                : new CpuError($"OpenProcess failed (error {err})", new Win32Exception(err));
        }

        try
        {
            if (!GetProcessTimes(handle, out _, out _, out var kernel, out var user))
            {
                int err = Marshal.GetLastWin32Error();
                return err == 5
                    ? new CpuAccessDenied()
                    : new CpuError($"GetProcessTimes failed (error {err})", new Win32Exception(err));
            }

            return new CpuTimesSuccess(kernel.ToTimeSpan(), user.ToTimeSpan());
        }
        finally
        {
            CloseHandle(handle);
        }
    }

    public static string FormatTime(TimeSpan t)
    {
        if (t.TotalSeconds < 60) return $"{t.TotalSeconds:F2}s";
        if (t.TotalMinutes < 60) return $"{(int)t.TotalMinutes}m {t.Seconds:D2}s";
        return $"{(int)t.TotalHours}h {t.Minutes:D2}m {t.Seconds:D2}s";
    }
}

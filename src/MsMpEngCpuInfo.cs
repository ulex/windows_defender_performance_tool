using System;
using System.Globalization;
using System.Runtime.InteropServices;

namespace WindowsDefenderPerformanceTool;

internal abstract record CpuQueryResult;
internal sealed record CpuTimesSuccess(TimeSpan KernelTime, TimeSpan UserTime) : CpuQueryResult;
internal sealed record CpuNotRunning() : CpuQueryResult;
internal sealed record CpuError(string Message, Exception Source) : CpuQueryResult;

// Queries MsMpEng.exe CPU times via NtQuerySystemInformation(SystemProcessInformation).
// This enumerates all processes at the kernel level and returns CPU times without
// requiring a handle to the target process.
internal static class MsMpEngCpuInfo
{
    // SYSTEM_INFORMATION_CLASS value 5: returns one SYSTEM_PROCESS_INFORMATION per process.
    // Struct field offsets below are for x64 Windows (which this app requires).
    private const int SystemProcessInformation = 5;

    // NTSTATUS codes
    private const int StatusSuccess = 0;
    private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);

    // SYSTEM_PROCESS_INFORMATION x64 field offsets (see ntexapi.h in Windows SDK / phnt):
    //   0  ULONG         NextEntryOffset
    //   8  ULONGLONG     WorkingSetPrivateSize
    //  32  LARGE_INTEGER CreateTime
    //  40  LARGE_INTEGER UserTime        ← 100-ns ticks
    //  48  LARGE_INTEGER KernelTime      ← 100-ns ticks
    //  56  USHORT        ImageName.Length (in bytes)
    //  58  USHORT        ImageName.MaximumLength
    //  60  [4-byte pad for 8-byte alignment]
    //  64  PWCH          ImageName.Buffer
    //  72  LONG          BasePriority
    //  76  [4-byte pad]
    //  80  HANDLE        UniqueProcessId
    private const int OffUserTime = 40;
    private const int OffKernelTime = 48;
    private const int OffImageNameLength = 56;
    private const int OffImageNameBuffer = 64;

    [DllImport("ntdll.dll")]
    private static extern int NtQuerySystemInformation(
        int SystemInformationClass,
        IntPtr SystemInformation,
        int SystemInformationLength,
        out int ReturnLength);

    public static CpuQueryResult Query()
    {
        int bufferSize = 256 * 1024;

        while (true)
        {
            IntPtr buffer = Marshal.AllocHGlobal(bufferSize);
            int status = NtQuerySystemInformation(
                SystemProcessInformation, buffer, bufferSize, out int returnedLength);

            if (status == StatusInfoLengthMismatch)
            {
                Marshal.FreeHGlobal(buffer);
                bufferSize = returnedLength + 65536;
                continue;
            }

            try
            {
                if (status != StatusSuccess)
                    return new CpuError(
                        $"NtQuerySystemInformation returned 0x{status:X8}",
                        new InvalidOperationException($"NTSTATUS 0x{status:X8}"));

                IntPtr entry = buffer;
                while (true)
                {
                    if (TryReadImageName(entry, out string? name) &&
                        name.Equals("MsMpEng.exe", StringComparison.OrdinalIgnoreCase))
                    {
                        long userTicks   = Marshal.ReadInt64(entry, OffUserTime);
                        long kernelTicks = Marshal.ReadInt64(entry, OffKernelTime);
                        return new CpuTimesSuccess(
                            TimeSpan.FromTicks(kernelTicks),
                            TimeSpan.FromTicks(userTicks));
                    }

                    uint next = (uint)Marshal.ReadInt32(entry, 0);
                    if (next == 0) break;
                    entry = IntPtr.Add(entry, (int)next);
                }

                return new CpuNotRunning();
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    private static bool TryReadImageName(IntPtr entry, out string name)
    {
        name = string.Empty;
        ushort byteLen = (ushort)Marshal.ReadInt16(entry, OffImageNameLength);
        IntPtr ptr = Marshal.ReadIntPtr(entry, OffImageNameBuffer);
        if (ptr == IntPtr.Zero || byteLen == 0) return false;
        name = Marshal.PtrToStringUni(ptr, byteLen / 2);
        return true;
    }

    public static string FormatTime(TimeSpan t)
    {
        if (t.TotalSeconds < 60)
            return t.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture) + "s";
        if (t.TotalMinutes < 60)
            return $"{(int)t.TotalMinutes}m {t.Seconds:D2}s";
        return $"{(int)t.TotalHours}h {t.Minutes:D2}m {t.Seconds:D2}s";
    }
}

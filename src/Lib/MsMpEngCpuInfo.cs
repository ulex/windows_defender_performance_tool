using System;
using System.Runtime.InteropServices;

namespace Lib;

public abstract record CpuQueryResult;
public sealed record CpuTimesSuccess(TimeSpan KernelTime, TimeSpan UserTime) : CpuQueryResult;
public sealed record CpuNotRunning() : CpuQueryResult;
public sealed record CpuError(string Message, Exception Source) : CpuQueryResult;

/// Queries MsMpEng.exe CPU times via NtQuerySystemInformation(SystemProcessInformation).
public static class MsMpEngCpuInfo
{
  // SYSTEM_INFORMATION_CLASS value 5: returns one SYSTEM_PROCESS_INFORMATION per process.
  // Struct layout below assumes x64 Windows (which this app requires).
  private const int SystemProcessInformation = 5;

  // NTSTATUS codes
  private const int StatusSuccess = 0;
  private const int StatusInfoLengthMismatch = unchecked((int)0xC0000004);


  [StructLayout(LayoutKind.Sequential)]
  public struct UNICODE_STRING
  {
    public ushort Length;
    public ushort MaximumLength;
    public IntPtr Buffer;
  }

  [StructLayout(LayoutKind.Sequential)]
  internal struct SYSTEM_PROCESS_INFORMATION
  {
    internal uint NextEntryOffset;
    internal uint NumberOfThreads;
    internal ulong WorkingSetPrivateSize;
    internal uint HardFaultCount;
    internal uint NumberOfThreadsHighWatermark;
    internal ulong CycleTime;
    internal long CreateTime;                   // 100-ns ticks since creation
    internal long UserTime;                     // 100-ns ticks in user mode
    internal long KernelTime;                   // 100-ns ticks in kernel mode
    internal MsMpEngCpuInfo.UNICODE_STRING ImageName;
    internal int BasePriority;                  // KPRIORITY
    internal IntPtr UniqueProcessId;
    internal IntPtr InheritedFromUniqueProcessId;
    internal uint HandleCount;
    internal uint SessionId;
    internal UIntPtr UniqueProcessKey;
    internal UIntPtr PeakVirtualSize;           // SIZE_T
    internal UIntPtr VirtualSize;               // SIZE_T
    internal uint PageFaultCount;
    internal UIntPtr PeakWorkingSetSize;        // SIZE_T
    internal UIntPtr WorkingSetSize;            // SIZE_T
    internal UIntPtr QuotaPeakPagedPoolUsage;   // SIZE_T
    internal UIntPtr QuotaPagedPoolUsage;       // SIZE_T
    internal UIntPtr QuotaPeakNonPagedPoolUsage; // SIZE_T
    internal UIntPtr QuotaNonPagedPoolUsage;    // SIZE_T
    internal UIntPtr PagefileUsage;             // SIZE_T
    internal UIntPtr PeakPagefileUsage;         // SIZE_T
    internal UIntPtr PrivatePageCount;          // SIZE_T
    internal long ReadOperationCount;
    internal long WriteOperationCount;
    internal long OtherOperationCount;
    internal long ReadTransferCount;
    internal long WriteTransferCount;
    internal long OtherTransferCount;
    // SYSTEM_THREAD_INFORMATION Threads[1] — variable-length tail, not mapped.
  }

  [DllImport("ntdll.dll")]
  private static extern int NtQuerySystemInformation(
        int SystemInformationClass,
        IntPtr SystemInformation,
        int SystemInformationLength,
        out int ReturnLength);

  public static unsafe CpuQueryResult Query()
  {
    int bufferSize = 512 * 1024;

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

        byte* entry = (byte*)buffer;
        while (true)
        {
          MsMpEngCpuInfo.SYSTEM_PROCESS_INFORMATION* info = (MsMpEngCpuInfo.SYSTEM_PROCESS_INFORMATION*)entry;
          if (TryReadImageName(info->ImageName, out string? name) &&
              name.Equals("MsMpEng.exe", StringComparison.OrdinalIgnoreCase))
          {
            return new CpuTimesSuccess(
                TimeSpan.FromTicks(info->KernelTime),
                TimeSpan.FromTicks(info->UserTime));
          }

          if (info->NextEntryOffset == 0) break;
          entry += info->NextEntryOffset;
        }

        return new CpuNotRunning();
      }
      finally
      {
        Marshal.FreeHGlobal(buffer);
      }
    }
  }

  private static bool TryReadImageName(UNICODE_STRING imageName, out string name)
  {
    name = string.Empty;
    if (imageName.Buffer == IntPtr.Zero || imageName.Length == 0) return false;
    name = Marshal.PtrToStringUni(imageName.Buffer, imageName.Length / 2);
    return true;
  }
}

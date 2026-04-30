using System;
using System.Globalization;

namespace Lib;

public static class Util
{
  public static string FormatTime(TimeSpan t)
  {
    if (t.TotalSeconds < 60)
      return t.TotalSeconds.ToString("F2", CultureInfo.InvariantCulture) + "s";
    if (t.TotalMinutes < 60)
      return $"{(int)t.TotalMinutes}m {t.Seconds:D2}s";
    return $"{(int)t.TotalHours}h {t.Minutes:D2}m {t.Seconds:D2}s";
  }
}
using System;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using WindowsDefenderPerformanceTool;

namespace WindowsDefenderCpuTime
{
    class Program
    {
        private static volatile bool _resetPending;
        private static volatile bool _exitPending;

        private const int HistorySize = 16;
        private const int ChartRows   = 6;

        // history[HistorySize-1] = most recent (right), history[0] = oldest (left)
        private static readonly double[] _history = new double[HistorySize];

        static void Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            EnableAnsi();
            Console.CursorVisible = false;
            Console.Clear();

            var keyThread = new Thread(ReadKeys) { IsBackground = true };
            keyThread.Start();

            TimeSpan? baseline = null;
            TimeSpan prevAccumulated = TimeSpan.Zero;

            while (!_exitPending)
            {
                if (_resetPending)
                {
                    _resetPending = false;
                    baseline = null;
                    prevAccumulated = TimeSpan.Zero;
                    Array.Clear(_history, 0, HistorySize);
                }

                string statusLine;
                var result = MsMpEngCpuInfo.Query();

                if (result is CpuTimesSuccess success)
                {
                    var current = success.KernelTime + success.UserTime;
                    if (baseline == null)
                    {
                        baseline = current;
                        prevAccumulated = TimeSpan.Zero;
                    }
                    var accumulated = current - baseline.Value;
                    var delta       = accumulated - prevAccumulated;
                    prevAccumulated = accumulated;

                    // Shift history left and append new sample
                    Array.Copy(_history, 1, _history, 0, HistorySize - 1);
                    _history[HistorySize - 1] = Math.Max(0, delta.TotalSeconds);

                    statusLine = $"Total: {MsMpEngCpuInfo.FormatTime(accumulated)}  |  +{delta.TotalSeconds:F2}s last second";
                }
                else if (result is CpuNotRunning)
                {
                    statusLine = "MsMpEng.exe is not running";
                }
                else if (result is CpuError error)
                {
                    statusLine = $"Error: {error.Message}";
                }
                else
                {
                    statusLine = string.Empty;
                }

                Render(statusLine);
                Thread.Sleep(1000);
            }

            Console.CursorVisible = true;
        }

        // ── rendering ────────────────────────────────────────────────────────────

        static void Render(string statusLine)
        {
            Console.SetCursorPosition(0, 0);
            int w = Math.Max(Console.WindowWidth - 1, 40);

            PrintLine("  Windows Defender CPU Time Monitor", w);
            PrintLine("  R = reset accumulated  |  Enter = exit", w);
            PrintLine("", w);
            PrintLine("  CPU time / second (last 16s)", w);
            PrintLine("", w);
            DrawChart(w);
            PrintLine("", w);
            PrintLine("  " + statusLine, w);
        }

        static void PrintLine(string text, int width)
        {
            if (text.Length < width)
                text += new string(' ', width - text.Length);
            Console.WriteLine(text);
        }

        static void DrawChart(int consoleWidth)
        {
            double max = 0;
            foreach (var v in _history) if (v > max) max = v;
            if (max < 0.01) max = 0.01;

            string topLabel = FormatYLabel(max);
            string midLabel = FormatYLabel(max / 2);
            int labelWidth  = Math.Max(Math.Max(topLabel.Length, midLabel.Length), 5);

            // Visual widths (ANSI escapes don't count toward terminal columns)
            int barAreaWidth  = HistorySize * 2;                       // 32 cols
            int rowVisualWidth = 2 + labelWidth + 2 + barAreaWidth;    // margin + label + " │" + bars
            string rowPad     = new string(' ', Math.Max(0, consoleWidth - rowVisualWidth));

            // ── chart rows, top → bottom ─────────────────────────────────────────
            for (int row = ChartRows - 1; row >= 0; row--)
            {
                var sb = new StringBuilder();
                sb.Append("  ");

                // Y-axis label: top row shows max, middle row shows max/2
                string label = row == ChartRows - 1       ? topLabel
                             : row == ChartRows / 2 - 1   ? midLabel
                             : string.Empty;
                sb.Append(label.PadLeft(labelWidth));
                sb.Append(row == ChartRows - 1 || row == ChartRows / 2 - 1 ? " ┤" : " │");

                // Bars — use Unicode lower-N/8-block chars for smooth sub-row precision
                double rowFrac   = 1.0 / ChartRows;
                double rowBottom = row * rowFrac;
                double rowTop    = (row + 1) * rowFrac;

                for (int i = 0; i < HistorySize; i++)
                {
                    double frac = _history[i] / max;

                    if (frac >= rowTop)
                    {
                        sb.Append("\x1b[32m██\x1b[0m");
                    }
                    else if (frac <= rowBottom)
                    {
                        sb.Append("  ");
                    }
                    else
                    {
                        // Bar top lands inside this row — pick the nearest eighth block
                        double fill   = (frac - rowBottom) / rowFrac;   // 0..1
                        int    eighth = Math.Max(1, Math.Min(7, (int)Math.Round(fill * 8)));
                        char   block  = (char)(0x2580 + eighth);        // ▁ ▂ ▃ ▄ ▅ ▆ ▇
                        sb.Append("\x1b[32m").Append(block).Append(block).Append("\x1b[0m");
                    }
                }

                sb.Append(rowPad);
                Console.WriteLine(sb.ToString());
            }

            // ── axis line ────────────────────────────────────────────────────────
            PrintLine("  " + new string(' ', labelWidth) + " └" + new string('─', barAreaWidth), consoleWidth);

            // ── x-axis labels ────────────────────────────────────────────────────
            const string xLeft  = "←15s";
            const string xRight = "now";
            int gap = barAreaWidth - xLeft.Length - xRight.Length;
            string xAxis = "  " + new string(' ', labelWidth + 2) + xLeft + new string(' ', Math.Max(1, gap)) + xRight;
            PrintLine(xAxis, consoleWidth);
        }

        static string FormatYLabel(double seconds)
        {
            if (seconds < 60)    return seconds.ToString("F2") + "s";
            if (seconds < 3600)  return $"{(int)(seconds / 60)}m{(int)(seconds % 60):D2}s";
            return $"{(int)(seconds / 3600)}h{(int)(seconds / 60 % 60):D2}m";
        }

        // ── input ─────────────────────────────────────────────────────────────────

        private static void ReadKeys()
        {
            while (!_exitPending)
            {
                if (Console.KeyAvailable)
                {
                    var key = Console.ReadKey(intercept: true);
                    if (key.Key == ConsoleKey.R)
                        _resetPending = true;
                    else if (key.Key == ConsoleKey.Enter)
                        _exitPending = true;
                }
                Thread.Sleep(50);
            }
        }

        // ── ANSI / kernel32 ───────────────────────────────────────────────────────

        [DllImport("kernel32.dll")] private static extern bool    GetConsoleMode(IntPtr h, out uint mode);
        [DllImport("kernel32.dll")] private static extern bool    SetConsoleMode(IntPtr h, uint mode);
        [DllImport("kernel32.dll")] private static extern IntPtr  GetStdHandle(int h);

        static void EnableAnsi()
        {
            var handle = GetStdHandle(-11); // STD_OUTPUT_HANDLE
            if (GetConsoleMode(handle, out uint mode))
                SetConsoleMode(handle, mode | 4); // ENABLE_VIRTUAL_TERMINAL_PROCESSING
        }
    }
}

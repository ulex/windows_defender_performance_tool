using System;
using System.Windows;

namespace WindowsDefenderPerformanceTool;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        var app = new Application();
        using var viewModel = new MainViewModel();
        var mainWindow = new MainWindow(viewModel);
        app.Run(mainWindow);
    }
}

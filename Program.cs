using System;
using System.Runtime.CompilerServices;
using System.Windows;

namespace WindowsDefenderMonitoring;

internal static class Program
{
    [STAThread]
    public static void Main()
    {
        // Must be first — unpacks embedded DLLs and registers AssemblyResolve
        // before any external assembly is loaded.
        SupportFiles.UnpackResourcesIfNeeded();

        // Separated into its own method so the JIT doesn't resolve types that
        // live in external assemblies (MainViewModel, MainWindow, etc.) while
        // compiling Main() — before AssemblyResolve is registered.
        RunApp();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunApp()
    {
        var app = new Application();

        using var viewModel = new MainViewModel();
        var mainWindow = new MainWindow(viewModel);

        app.Run(mainWindow);
    }
}

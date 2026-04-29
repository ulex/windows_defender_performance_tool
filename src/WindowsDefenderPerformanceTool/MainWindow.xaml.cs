using System;
using System.IO;
using System.Linq;
using System.Windows;
using Microsoft.Win32;
using ReactiveUI;

namespace WindowsDefenderPerformanceTool;

public partial class MainWindow : ReactiveWindow<MainViewModel>
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;

        DragOver += OnDragOver;
        Drop += OnDrop;
    }

    private static void OnDragOver(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            var files = (string[])e.Data.GetData(DataFormats.FileDrop);
            if (files != null && files.Any(f => f.EndsWith(".etl", StringComparison.OrdinalIgnoreCase)))
            {
                e.Effects = DragDropEffects.Copy;
                e.Handled = true;
                return;
            }
        }
        e.Effects = DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;

        var files = (string[])e.Data.GetData(DataFormats.FileDrop);
        if (files == null) return;

        var etlFiles = files.Where(f => f.EndsWith(".etl", StringComparison.OrdinalIgnoreCase)).ToArray();
        if (etlFiles.Length == 0) return;

        if (etlFiles.Length == 1)
        {
            // Single file — load in this window
            ViewModel!.LoadEtlFile(etlFiles[0]);
            return;
        }

        // Multiple files — ask what to do
        var result = MessageBox.Show(this,
            $"{etlFiles.Length} ETL files dropped.\n\n" +
            "Yes — Export summary CSV\n" +
            "No — Open each in a separate viewer window",
            "Multiple ETL Files",
            MessageBoxButton.YesNoCancel,
            MessageBoxImage.Question);

        switch (result)
        {
            case MessageBoxResult.Yes:
                await BatchExportCsvAsync(etlFiles);
                break;

            case MessageBoxResult.No:
                foreach (var file in etlFiles)
                {
                    var vm = new MainViewModel(startLiveMonitoring: false);
                    var window = new MainWindow(vm) { Owner = null, Topmost = false };
                    vm.LoadEtlFile(file);
                    window.Show();
                }
                break;
        }
    }

    private async System.Threading.Tasks.Task BatchExportCsvAsync(string[] etlFiles)
    {
        var dialog = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv|All files (*.*)|*.*",
            Title = "Save Batch Summary",
            FileName = "defender_scan_summary.csv"
        };
        if (dialog.ShowDialog(this) != true) return;

        IsEnabled = false;
        Title = "Exporting…";
        try
        {
            var progress = new Progress<(int completed, int total)>(p =>
            {
                Title = $"Exporting… ({p.completed}/{p.total})";
            });

            await EtlBatchExporter.ExportCsvAsync(etlFiles, dialog.FileName, progress);

            MessageBox.Show(this,
                $"Exported {etlFiles.Length} files to:\n{dialog.FileName}",
                "Export Complete",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Export failed:\n{ex.Message}",
                "Export Error",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
        finally
        {
            IsEnabled = true;
            Title = ViewModel!.WindowTitle;
        }
    }
}

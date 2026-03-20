using System;
using ReactiveUI;

namespace WindowsDefenderMonitoring;

public partial class MainWindow : ReactiveWindow<MainViewModel>
{
    public MainWindow(MainViewModel viewModel)
    {
        InitializeComponent();
        ViewModel = viewModel;
        DataContext = viewModel;
    }
}

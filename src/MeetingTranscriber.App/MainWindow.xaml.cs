using System.Windows;
using MeetingTranscriber.App.Services;
using MeetingTranscriber.App.ViewModels;

namespace MeetingTranscriber.App;

public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel;

    public MainWindow()
    {
        InitializeComponent();
        _viewModel = new MainViewModel(new PipelineClient());
        DataContext = _viewModel;
        Closed += (_, _) => _viewModel.Dispose();
    }
}

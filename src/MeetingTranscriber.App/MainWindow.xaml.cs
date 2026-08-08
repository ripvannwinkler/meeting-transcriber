using System.Windows;
using MeetingTranscriber.App.Services;
using MeetingTranscriber.App.ViewModels;

namespace MeetingTranscriber.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Phase 3/4 swap NoopPipeline for the real Python-backed pipeline.
        DataContext = new MainViewModel(new NoopPipeline());
    }
}

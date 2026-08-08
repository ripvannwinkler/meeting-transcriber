using System.Windows;
using MeetingTranscriber.App.Services;
using MeetingTranscriber.App.ViewModels;

namespace MeetingTranscriber.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        DataContext = new MainViewModel(new PipelineClient());
    }
}

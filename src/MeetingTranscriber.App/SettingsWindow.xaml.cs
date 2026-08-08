using System.Windows;
using MeetingTranscriber.App.ViewModels;

namespace MeetingTranscriber.App;

public partial class SettingsWindow : Window
{
    public SettingsWindow()
    {
        InitializeComponent();
        var vm = new SettingsViewModel();
        DataContext = vm;
        vm.OnCancel += Close;
        vm.OnSaved += _ => Close();
    }
}

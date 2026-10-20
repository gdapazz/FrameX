using System.Windows;
using System.Windows.Controls;
using Framex.App.ViewModels;
using Framex.Core.Customization;

namespace Framex.App.Views;

public partial class SettingsView : UserControl
{
    public SettingsView() => InitializeComponent();

    private void OnProfileClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: Choice<PerformanceProfile> choice } && DataContext is SettingsViewModel vm)
        {
            vm.Profile = choice;
        }
    }
}

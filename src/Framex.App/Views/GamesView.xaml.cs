using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Framex.App.ViewModels;

namespace Framex.App.Views;

public partial class GamesView : UserControl
{
    public GamesView() => InitializeComponent();

    private void OnMoreClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void OnBackdropClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is GamesViewModel vm)
        {
            vm.ClosePickerCommand.Execute(null);
        }
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Framex.App.ViewModels;

namespace Framex.App.Views;

public partial class ReportsView : UserControl
{
    public ReportsView() => InitializeComponent();

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }

    private void OnRowMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && FindAncestor<CheckBox>(source) is not null)
        {
            return;
        }

        if (sender is FrameworkElement { DataContext: SessionRowViewModel row } && DataContext is ReportsViewModel vm)
        {
            if (Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                row.IsSelected = !row.IsSelected;
            }
            else
            {
                vm.OpenCommand.Execute(row);
            }

            e.Handled = true;
        }
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d is not null)
        {
            if (d is T t)
            {
                return t;
            }

            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }

        return null;
    }
}

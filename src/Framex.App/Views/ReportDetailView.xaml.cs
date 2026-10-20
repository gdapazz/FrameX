using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Framex.App.Views;

public partial class ReportDetailView : UserControl
{
    public ReportDetailView() => InitializeComponent();

    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { ContextMenu: { } menu } button)
        {
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Bottom;
            menu.IsOpen = true;
        }
    }
}

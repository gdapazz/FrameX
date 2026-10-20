using System.Windows;
using System.Windows.Controls;
using Framex.App.ViewModels;

namespace Framex.App.Controls;

/// <summary>Escolhe o template "W.{Widget}" definido nos recursos da tela do painel.</summary>
public sealed class WidgetTemplateSelector : DataTemplateSelector
{
    public override DataTemplate? SelectTemplate(object item, DependencyObject container) =>
        item is DashboardWidgetItem w && container is FrameworkElement fe ? fe.TryFindResource("W." + w.Widget) as DataTemplate : null;
}

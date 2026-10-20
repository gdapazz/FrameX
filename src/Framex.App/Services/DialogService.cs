using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.Win32;

namespace Framex.App.Services;

public sealed record DialogButton(string Text, bool Primary = false, bool Danger = false);

/// <summary>Diálogos no visual do FRAMEX e seletores de arquivo nativos.</summary>
public sealed class DialogService
{
    private static Window? Owner => Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w is not Views.OverlayWindow)
                                    ?? Application.Current.MainWindow;

    /// <summary>Exibe um diálogo modal e retorna o índice do botão escolhido (-1 se fechado).</summary>
    public int Show(string title, string message, IReadOnlyList<DialogButton> buttons, string? checkBoxText, out bool checkBoxValue)
    {
        var result = -1;
        CheckBox? check = null;
        var window = CreateWindow();

        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.FindResource("Text.Subtitle"), Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = message, Style = (Style)Application.Current.FindResource("Text.Muted"), Margin = new Thickness(0, 0, 0, 16) });
        if (checkBoxText is not null)
        {
            check = new CheckBox { Content = checkBoxText, Margin = new Thickness(0, 0, 0, 16) };
            panel.Children.Add(check);
        }

        panel.Children.Add(ButtonRow(buttons, i =>
        {
            result = i;
            window.Close();
        }));

        window.Content = Frame(panel);
        window.ShowDialog();
        checkBoxValue = check?.IsChecked == true;
        return result;
    }

    public int Show(string title, string message, params DialogButton[] buttons) => Show(title, message, buttons, null, out _);

    public bool Confirm(string title, string message, string confirmText = "Confirmar", bool danger = false) =>
        Show(title, message, new DialogButton("Cancelar"), new DialogButton(confirmText, Primary: !danger, Danger: danger)) == 1;

    public void Info(string title, string message) => Show(title, message, new DialogButton("OK", Primary: true));

    public string? Prompt(string title, string message, string initial, int maxLength = 60)
    {
        string? result = null;
        var window = CreateWindow();
        var box = new TextBox { Text = initial, MaxLength = maxLength, Margin = new Thickness(0, 0, 0, 16) };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.FindResource("Text.Subtitle"), Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(new TextBlock { Text = message, Style = (Style)Application.Current.FindResource("Text.Muted"), Margin = new Thickness(0, 0, 0, 10) });
        panel.Children.Add(box);
        panel.Children.Add(ButtonRow([new DialogButton("Cancelar"), new DialogButton("Salvar", Primary: true)], i =>
        {
            if (i == 1 && !string.IsNullOrWhiteSpace(box.Text))
            {
                result = box.Text.Trim();
            }

            window.Close();
        }));
        window.Content = Frame(panel);
        window.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        window.ShowDialog();
        return result;
    }

    private static Window CreateWindow()
    {
        var owner = Owner;
        var window = new Window
        {
            WindowStyle = WindowStyle.None,
            AllowsTransparency = true,
            Background = Brushes.Transparent,
            ResizeMode = ResizeMode.NoResize,
            SizeToContent = SizeToContent.WidthAndHeight,
            ShowInTaskbar = owner is null || !owner.IsVisible,
            WindowStartupLocation = owner is { IsVisible: true } ? WindowStartupLocation.CenterOwner : WindowStartupLocation.CenterScreen,
            Title = "FRAMEX",
            FontFamily = (FontFamily)Application.Current.FindResource("F.Body"),
        };
        if (owner is { IsVisible: true })
        {
            window.Owner = owner;
        }

        window.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                window.Close();
            }
        };
        return window;
    }

    private static Border Frame(UIElement content)
    {
        var border = new Border
        {
            Width = 440,
            Margin = new Thickness(24),
            Padding = new Thickness(24),
            CornerRadius = (CornerRadius)Application.Current.FindResource("R.Corner"),
            BorderThickness = new Thickness(1),
            Child = content,
            Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 30, ShadowDepth = 0, Opacity = 0.6, Color = Colors.Black },
        };
        border.SetResourceReference(Border.BackgroundProperty, "B.SurfaceAlt");
        border.SetResourceReference(Border.BorderBrushProperty, "B.Border");
        border.MouseLeftButtonDown += (_, _) => Window.GetWindow(border)?.DragMove();
        return border;
    }

    private static StackPanel ButtonRow(IReadOnlyList<DialogButton> buttons, Action<int> onClick)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        for (var i = 0; i < buttons.Count; i++)
        {
            var index = i;
            var b = buttons[i];
            var button = new Button
            {
                Content = b.Text,
                Margin = new Thickness(8, 0, 0, 0),
                MinWidth = 96,
                IsDefault = b.Primary,
                IsCancel = i == 0 && buttons.Count > 1,
                Style = (Style)Application.Current.FindResource(b.Danger ? "Button.Danger" : b.Primary ? "Button.Primary" : "Button.Ghost"),
            };
            button.Click += (_, _) => onClick(index);
            row.Children.Add(button);
        }

        return row;
    }

    private static bool Run(CommonDialog dialog) => (Owner is { IsVisible: true } owner ? dialog.ShowDialog(owner) : dialog.ShowDialog()) == true;

    public string? OpenFile(string title, string filter)
    {
        var dialog = new OpenFileDialog { Title = title, Filter = filter, CheckFileExists = true, Multiselect = false };
        return Run(dialog) ? dialog.FileName : null;
    }

    public string? SaveFile(string title, string filter, string fileName, string? initialDirectory)
    {
        var dialog = new SaveFileDialog { Title = title, Filter = filter, FileName = fileName, AddExtension = true, OverwritePrompt = true };
        if (initialDirectory is not null)
        {
            Directory.CreateDirectory(initialDirectory);
            dialog.InitialDirectory = initialDirectory;
        }

        return Run(dialog) ? dialog.FileName : null;
    }

    public string? PickFolder(string title, string? initialDirectory)
    {
        var dialog = new OpenFolderDialog { Title = title, Multiselect = false };
        if (initialDirectory is not null && Directory.Exists(initialDirectory))
        {
            dialog.InitialDirectory = initialDirectory;
        }

        return Run(dialog) ? dialog.FolderName : null;
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Framex.App.Services;

namespace Framex.App.Controls;

/// <summary>Campo que grava uma combinação de teclas (exige ao menos um modificador). Backspace/Delete limpa.</summary>
public sealed class HotkeyBox : TextBox
{
    public static readonly DependencyProperty GestureProperty = DependencyProperty.Register(
        nameof(Gesture), typeof(string), typeof(HotkeyBox),
        new FrameworkPropertyMetadata("", FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, (d, e) => ((HotkeyBox)d).Text = (string?)e.NewValue ?? ""));

    public HotkeyBox()
    {
        IsReadOnly = true;
        IsReadOnlyCaretVisible = false;
        Cursor = Cursors.Hand;
        ToolTip = "Clique e pressione a combinação desejada (ex.: Ctrl+Shift+F10). Backspace remove o atalho.";
    }

    public string Gesture
    {
        get => (string)GetValue(GestureProperty);
        set => SetValue(GestureProperty, value);
    }

    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnGotKeyboardFocus(e);
        Text = "Pressione as teclas…";
    }

    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e)
    {
        base.OnLostKeyboardFocus(e);
        Text = Gesture;
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        e.Handled = true;
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        if (key is Key.Back or Key.Delete && Keyboard.Modifiers == ModifierKeys.None)
        {
            Gesture = "";
            Text = "";
            return;
        }

        if (key is Key.Escape or Key.Tab)
        {
            Keyboard.ClearFocus();
            return;
        }

        if (key is Key.LeftCtrl or Key.RightCtrl or Key.LeftShift or Key.RightShift or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
        {
            return;
        }

        if (Keyboard.Modifiers == ModifierKeys.None)
        {
            Text = "Use Ctrl, Shift ou Alt + tecla";
            return;
        }

        Gesture = HotkeyService.Format(Keyboard.Modifiers, key);
        Text = Gesture;
    }
}

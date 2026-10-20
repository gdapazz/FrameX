using System.Windows.Input;
using System.Windows.Interop;
using Framex.App.Interop;
using Microsoft.Extensions.Logging;

namespace Framex.App.Services;

/// <summary>Atalhos globais via RegisterHotKey (não intercepta teclado de outros processos, apenas registra combinações).</summary>
public sealed class HotkeyService : IDisposable
{
    private readonly ILogger<HotkeyService> _logger;
    private readonly Dictionary<int, Action> _actions = new();
    private HwndSource? _source;
    private int _nextId = 0x4600;

    public HotkeyService(ILogger<HotkeyService> logger) => _logger = logger;

    /// <summary>Atalhos que falharam no último registro (ex.: já usados por outro programa).</summary>
    public List<string> Failures { get; } = [];

    private HwndSource Source
    {
        get
        {
            if (_source is null)
            {
                var p = new HwndSourceParameters("FRAMEX.Hotkeys") { Width = 0, Height = 0, WindowStyle = 0, ParentWindow = new IntPtr(-3) };
                _source = new HwndSource(p);
                _source.AddHook(WndProc);
            }

            return _source;
        }
    }

    public static bool TryParse(string? gesture, out ModifierKeys modifiers, out Key key)
    {
        modifiers = ModifierKeys.None;
        key = Key.None;
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return false;
        }

        foreach (var raw in gesture.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (raw.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= ModifierKeys.Control;
                    break;
                case "SHIFT":
                    modifiers |= ModifierKeys.Shift;
                    break;
                case "ALT":
                    modifiers |= ModifierKeys.Alt;
                    break;
                case "WIN":
                    modifiers |= ModifierKeys.Windows;
                    break;
                default:
                    if (key != Key.None || !Enum.TryParse(raw, ignoreCase: true, out key))
                    {
                        return false;
                    }

                    break;
            }
        }

        return key != Key.None && modifiers != ModifierKeys.None;
    }

    public static string Format(ModifierKeys modifiers, Key key)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
        parts.Add(key.ToString());
        return string.Join("+", parts);
    }

    public void UnregisterAll()
    {
        if (_source is not null)
        {
            foreach (var id in _actions.Keys)
            {
                Win32.UnregisterHotKey(_source.Handle, id);
            }
        }

        _actions.Clear();
        Failures.Clear();
    }

    public bool Register(string label, string? gesture, Action action)
    {
        if (string.IsNullOrWhiteSpace(gesture))
        {
            return true;
        }

        if (!TryParse(gesture, out var modifiers, out var key))
        {
            Failures.Add($"{label}: combinação inválida ({gesture})");
            return false;
        }

        uint mods = Win32.MOD_NOREPEAT;
        if (modifiers.HasFlag(ModifierKeys.Control)) mods |= Win32.MOD_CONTROL;
        if (modifiers.HasFlag(ModifierKeys.Shift)) mods |= Win32.MOD_SHIFT;
        if (modifiers.HasFlag(ModifierKeys.Alt)) mods |= Win32.MOD_ALT;
        if (modifiers.HasFlag(ModifierKeys.Windows)) mods |= Win32.MOD_WIN;

        var id = _nextId++;
        if (!Win32.RegisterHotKey(Source.Handle, id, mods, (uint)KeyInterop.VirtualKeyFromKey(key)))
        {
            _logger.LogWarning("Atalho {Gesture} ({Label}) não pôde ser registrado; provavelmente já está em uso", gesture, label);
            Failures.Add($"{label}: {gesture} já está em uso por outro programa");
            return false;
        }

        _actions[id] = action;
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == Win32.WM_HOTKEY && _actions.TryGetValue(wParam.ToInt32(), out var action))
        {
            handled = true;
            try
            {
                action();
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Falha ao executar ação de atalho");
            }
        }

        return IntPtr.Zero;
    }

    public void Dispose()
    {
        UnregisterAll();
        _source?.Dispose();
        _source = null;
    }
}

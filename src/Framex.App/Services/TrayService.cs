using System.Windows;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;
using Microsoft.Extensions.Logging;

namespace Framex.App.Services;

/// <summary>Ícone na área de notificação com menu de contexto e notificações nativas do Windows.</summary>
public sealed class TrayService : IDisposable
{
    private readonly ILogger<TrayService> _logger;
    private Forms.NotifyIcon? _icon;
    private Forms.ToolStripMenuItem? _overlayItem;
    private Forms.ToolStripMenuItem? _pauseItem;
    private Forms.ToolStripMenuItem? _statusItem;
    private Forms.ToolStripMenuItem? _reportItem;

    public TrayService(ILogger<TrayService> logger) => _logger = logger;

    public event EventHandler? OpenRequested;
    public event EventHandler? ToggleOverlayRequested;
    public event EventHandler? TogglePauseRequested;
    public event EventHandler? OpenLastReportRequested;
    public event EventHandler? ExitRequested;

    /// <summary>Disparado quando o usuário clica em uma notificação exibida pelo FRAMEX.</summary>
    public event EventHandler? NotificationClicked;

    public bool IsVisible => _icon?.Visible == true;

    public void Show()
    {
        if (_icon is not null)
        {
            _icon.Visible = true;
            return;
        }

        var menu = new Forms.ContextMenuStrip
        {
            Renderer = new DarkMenuRenderer(),
            ShowImageMargin = false,
            Font = new Drawing.Font("Segoe UI", 9.5f),
            Padding = new Forms.Padding(4),
        };

        _statusItem = new Forms.ToolStripMenuItem("Nenhum jogo em execução") { Enabled = false };
        var open = new Forms.ToolStripMenuItem("Abrir FRAMEX", null, (_, _) => OpenRequested?.Invoke(this, EventArgs.Empty)) { Font = new Drawing.Font("Segoe UI", 9.5f, Drawing.FontStyle.Bold) };
        _overlayItem = new Forms.ToolStripMenuItem("Mostrar overlay", null, (_, _) => ToggleOverlayRequested?.Invoke(this, EventArgs.Empty));
        _pauseItem = new Forms.ToolStripMenuItem("Pausar gravação", null, (_, _) => TogglePauseRequested?.Invoke(this, EventArgs.Empty));
        _reportItem = new Forms.ToolStripMenuItem("Abrir último relatório", null, (_, _) => OpenLastReportRequested?.Invoke(this, EventArgs.Empty));
        var exit = new Forms.ToolStripMenuItem("Sair do FRAMEX", null, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty));

        menu.Items.AddRange([_statusItem, new Forms.ToolStripSeparator(), open, _overlayItem, _pauseItem, _reportItem, new Forms.ToolStripSeparator(), exit]);

        _icon = new Forms.NotifyIcon
        {
            Text = "FRAMEX",
            Icon = LoadIcon(),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left)
            {
                OpenRequested?.Invoke(this, EventArgs.Empty);
            }
        };
        _icon.BalloonTipClicked += (_, _) => NotificationClicked?.Invoke(this, EventArgs.Empty);
    }

    public void Hide()
    {
        if (_icon is not null)
        {
            _icon.Visible = false;
        }
    }

    private Drawing.Icon LoadIcon()
    {
        try
        {
            var info = Application.GetResourceStream(new Uri("pack://application:,,,/Framex;component/Assets/framex.ico"));
            if (info is not null)
            {
                using var stream = info.Stream;
                return new Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Ícone da bandeja não carregado; usando ícone padrão");
        }

        return Drawing.SystemIcons.Application;
    }

    public void UpdateState(string? gameName, bool recordingPaused, bool overlayVisible, bool hasLastReport)
    {
        if (_icon is null)
        {
            return;
        }

        var status = gameName is null ? "Nenhum jogo em execução" : $"Monitorando: {gameName}";
        _statusItem!.Text = status;
        _overlayItem!.Text = overlayVisible ? "Ocultar overlay" : "Mostrar overlay";
        _pauseItem!.Text = recordingPaused ? "Retomar gravação" : "Pausar gravação";
        _reportItem!.Enabled = hasLastReport;
        var tip = "FRAMEX — " + status + (recordingPaused ? " (pausado)" : "");
        _icon.Text = tip.Length > 63 ? tip[..63] : tip;
    }

    /// <summary>Notificação nativa (exibida como notificação do Windows 10/11).</summary>
    public void Notify(string title, string message, bool warning = false)
    {
        if (_icon is not { Visible: true })
        {
            return;
        }

        _icon.ShowBalloonTip(5000, title, message, warning ? Forms.ToolTipIcon.Warning : Forms.ToolTipIcon.Info);
    }

    public void Dispose()
    {
        if (_icon is not null)
        {
            _icon.Visible = false;
            _icon.ContextMenuStrip?.Dispose();
            _icon.Icon?.Dispose();
            _icon.Dispose();
            _icon = null;
        }
    }

    private sealed class DarkMenuRenderer : Forms.ToolStripProfessionalRenderer
    {
        private static readonly Drawing.Color Back = Drawing.Color.FromArgb(0x15, 0x1E, 0x33);
        private static readonly Drawing.Color Hover = Drawing.Color.FromArgb(0x1F, 0x2D, 0x4A);
        private static readonly Drawing.Color Text = Drawing.Color.FromArgb(0xE7, 0xEE, 0xFC);
        private static readonly Drawing.Color Muted = Drawing.Color.FromArgb(0x8A, 0x9B, 0xBD);
        private static readonly Drawing.Color Line = Drawing.Color.FromArgb(0x22, 0x30, 0x4D);

        public DarkMenuRenderer() : base(new DarkColors())
        {
            RoundedEdges = false;
        }

        protected override void OnRenderItemText(Forms.ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? Text : Muted;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderMenuItemBackground(Forms.ToolStripItemRenderEventArgs e)
        {
            using var brush = new Drawing.SolidBrush(e.Item.Selected && e.Item.Enabled ? Hover : Back);
            e.Graphics.FillRectangle(brush, new Drawing.Rectangle(Drawing.Point.Empty, e.Item.Size));
        }

        protected override void OnRenderSeparator(Forms.ToolStripSeparatorRenderEventArgs e)
        {
            using var pen = new Drawing.Pen(Line);
            var y = e.Item.Height / 2;
            e.Graphics.DrawLine(pen, 4, y, e.Item.Width - 4, y);
        }

        private sealed class DarkColors : Forms.ProfessionalColorTable
        {
            public override Drawing.Color ToolStripDropDownBackground => Back;
            public override Drawing.Color MenuBorder => Line;
            public override Drawing.Color MenuItemBorder => Hover;
            public override Drawing.Color MenuItemSelected => Hover;
            public override Drawing.Color ImageMarginGradientBegin => Back;
            public override Drawing.Color ImageMarginGradientMiddle => Back;
            public override Drawing.Color ImageMarginGradientEnd => Back;
            public override Drawing.Color SeparatorDark => Line;
            public override Drawing.Color SeparatorLight => Line;
        }
    }
}

using System.Diagnostics;
using System.IO;
using System.Text;
using Framex.Core.Persistence;
using Framex.Core.Reporting;
using Framex.Infrastructure;
using Microsoft.Extensions.Logging;

namespace Framex.App.Services;

/// <summary>Exporta relatórios para arquivos locais (CSV/JSON/HTML). Nada é enviado para fora da máquina.</summary>
public sealed class ExportService
{
    private readonly ISessionRepository _sessions;
    private readonly SettingsService _settings;
    private readonly AppPaths _paths;
    private readonly DialogService _dialogs;
    private readonly ILogger<ExportService> _logger;

    public ExportService(ISessionRepository sessions, SettingsService settings, AppPaths paths, DialogService dialogs, ILogger<ExportService> logger)
    {
        _sessions = sessions;
        _settings = settings;
        _paths = paths;
        _dialogs = dialogs;
        _logger = logger;
    }

    private string ExportFolder => _settings.Current.ExportFolder is { Length: > 0 } f ? f : _paths.DefaultExportDirectory;

    public SessionReport? LoadReport(Guid sessionId)
    {
        var session = _sessions.Get(sessionId);
        return session is null ? null : new SessionReport(session, session.Summary, _sessions.GetSamples(sessionId), _sessions.GetEvents(sessionId));
    }

    private static string Filter(ExportFormat format) => format switch
    {
        ExportFormat.Csv => "Planilha CSV (*.csv)|*.csv",
        ExportFormat.Json => "Dados JSON (*.json)|*.json",
        _ => "Relatório HTML (*.html)|*.html",
    };

    public async Task ExportAsync(SessionReport report, ExportFormat format)
    {
        var path = _dialogs.SaveFile("Exportar relatório", Filter(format), ReportExporter.SuggestFileName(report, format), ExportFolder);
        if (path is null)
        {
            return;
        }

        try
        {
            await Task.Run(() => File.WriteAllText(path, ReportExporter.Export(report, format), new UTF8Encoding(format == ExportFormat.Csv)));
            _logger.LogInformation("Relatório exportado ({Format})", format);
            Offer(path);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao exportar relatório");
            _dialogs.Info("Exportar", "Não foi possível salvar o arquivo: " + ex.Message);
        }
    }

    public async Task ExportManyAsync(IReadOnlyList<Guid> sessionIds, ExportFormat format)
    {
        var folder = _dialogs.PickFolder($"Pasta para exportar {sessionIds.Count} relatório(s)", ExportFolder);
        if (folder is null)
        {
            return;
        }

        var written = 0;
        string? last = null;
        try
        {
            await Task.Run(() =>
            {
                foreach (var id in sessionIds)
                {
                    var report = LoadReport(id);
                    if (report is null)
                    {
                        continue;
                    }

                    var path = UniquePath(Path.Combine(folder, ReportExporter.SuggestFileName(report, format)));
                    File.WriteAllText(path, ReportExporter.Export(report, format), new UTF8Encoding(format == ExportFormat.Csv));
                    last = path;
                    written++;
                }
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha na exportação em lote");
            _dialogs.Info("Exportar", $"Exportação interrompida após {written} arquivo(s): {ex.Message}");
            return;
        }

        if (last is not null)
        {
            Offer(last, $"{written} relatório(s) exportado(s).");
        }
    }

    private static string UniquePath(string path)
    {
        if (!File.Exists(path))
        {
            return path;
        }

        var dir = Path.GetDirectoryName(path)!;
        var name = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(dir, $"{name} ({i}){ext}");
            if (!File.Exists(candidate))
            {
                return candidate;
            }
        }
    }

    private void Offer(string path, string? message = null)
    {
        var choice = _dialogs.Show("Exportação concluída", (message ?? "Arquivo salvo.") + "\n" + Path.GetFileName(path),
            new DialogButton("Fechar"), new DialogButton("Mostrar na pasta", Primary: true));
        if (choice == 1)
        {
            RevealInExplorer(path);
        }
    }

    public static void RevealInExplorer(string path)
    {
        var psi = new ProcessStartInfo("explorer.exe")
        {
            UseShellExecute = false,
            Arguments = File.Exists(path) ? $"/select,\"{path}\"" : $"\"{path}\"",
        };

        using var _ = Process.Start(psi);
    }
}

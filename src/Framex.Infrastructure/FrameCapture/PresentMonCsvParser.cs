using System.Globalization;
using Framex.Core.FrameCapture;

namespace Framex.Infrastructure.FrameCapture;

/// <summary>
/// Interpreta a saída CSV do PresentMon (1.x e 2.x). As colunas são localizadas pelo cabeçalho,
/// pois variam entre versões e opções de linha de comando.
/// Frametime: MsBetweenPresents (intervalo entre chamadas Present() — métrica clássica de "frametime"),
/// com fallback para MsBetweenAppStart / FrameTime em versões que não a emitam.
/// </summary>
public sealed class PresentMonCsvParser
{
    private static readonly string[] FrameTimeColumns = ["MsBetweenPresents", "MsBetweenAppStart", "FrameTime"];

    private int _frameTimeIndex = -1;
    private int _qpcMsIndex = -1;
    private int _secondsIndex = -1;
    private int _pidIndex = -1;
    private int _displayedIndex = -1;
    private int _droppedIndex = -1;
    private int _columnCount;

    public bool HasHeader => _frameTimeIndex >= 0;

    public string? FrameTimeColumn { get; private set; }

    /// <summary>Âncora para converter tempo QPC (ms) em UTC.</summary>
    public (DateTimeOffset Utc, double QpcMs)? Anchor { get; set; }

    public static bool LooksLikeHeader(string line) =>
        line.StartsWith("Application,", StringComparison.Ordinal) && line.Contains("ProcessID", StringComparison.Ordinal);

    public bool TrySetHeader(string line)
    {
        if (!LooksLikeHeader(line))
        {
            return false;
        }

        var cols = line.Split(',');
        _columnCount = cols.Length;
        _frameTimeIndex = -1;
        FrameTimeColumn = null;
        foreach (var candidate in FrameTimeColumns)
        {
            var idx = Array.IndexOf(cols, candidate);
            if (idx >= 0)
            {
                _frameTimeIndex = idx;
                FrameTimeColumn = candidate;
                break;
            }
        }

        _qpcMsIndex = Array.IndexOf(cols, "CPUStartQPCTime");
        _secondsIndex = Array.IndexOf(cols, "TimeInSeconds");
        _pidIndex = Array.IndexOf(cols, "ProcessID");
        _displayedIndex = Array.IndexOf(cols, "DisplayedTime");
        _droppedIndex = Array.IndexOf(cols, "Dropped");
        return _frameTimeIndex >= 0;
    }

    /// <summary>Converte uma linha de dados. Retorna falso para linhas de outro processo ou malformadas.</summary>
    public bool TryParse(string line, int? expectedPid, DateTimeOffset receivedAt, out FrameSample frame)
    {
        frame = default;
        if (!HasHeader || line.Length == 0)
        {
            return false;
        }

        var cols = line.Split(',');
        if (cols.Length < _columnCount || cols.Length <= _frameTimeIndex)
        {
            return false;
        }

        if (expectedPid is { } pid && _pidIndex >= 0
            && (!int.TryParse(cols[_pidIndex], NumberStyles.Integer, CultureInfo.InvariantCulture, out var linePid) || linePid != pid))
        {
            return false;
        }

        if (!double.TryParse(cols[_frameTimeIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var ft))
        {
            return false;
        }

        var timestamp = receivedAt;
        if (_qpcMsIndex >= 0 && Anchor is { } anchor
            && double.TryParse(cols[_qpcMsIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var qpcMs))
        {
            timestamp = anchor.Utc.AddMilliseconds(qpcMs - anchor.QpcMs);
        }
        else if (_secondsIndex >= 0 && Anchor is { } a2
                 && double.TryParse(cols[_secondsIndex], NumberStyles.Float, CultureInfo.InvariantCulture, out var secs))
        {
            timestamp = a2.Utc.AddSeconds(secs);
        }

        var displayed = true;
        if (_displayedIndex >= 0)
        {
            displayed = !string.Equals(cols[_displayedIndex], "NA", StringComparison.OrdinalIgnoreCase);
        }
        else if (_droppedIndex >= 0)
        {
            displayed = cols[_droppedIndex] != "1";
        }

        frame = new FrameSample(timestamp, ft, displayed);
        return true;
    }
}

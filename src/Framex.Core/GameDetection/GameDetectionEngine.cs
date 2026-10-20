using Framex.Core.Domain;

namespace Framex.Core.GameDetection;

public interface IProcessSnapshotProvider
{
    IReadOnlyList<ProcessInfo> GetProcesses();
}

public enum DetectionKind
{
    /// <summary>Executável associado a um jogo da biblioteca.</summary>
    Known,
    /// <summary>Executável em pasta de biblioteca de loja conhecida, com janela visível; requer confirmação.</summary>
    Heuristic,
}

public sealed record DetectedGame(ProcessInfo Process, Game? Game, DetectionKind Kind, string SuggestedName, int InstanceCount);

/// <summary>
/// Lógica pura de identificação de jogos a partir de um instantâneo de processos.
/// Não considera "tela cheia" como critério: só associações conhecidas ou caminhos de bibliotecas de lojas.
/// </summary>
public static class GameDetectionEngine
{
    private static readonly string[] StoreLibraryMarkers =
    [
        @"\steamapps\common\",
        @"\epic games\",
        @"\gog galaxy\games\",
        @"\gog games\",
        @"\xboxgames\",
        @"\ubisoft game launcher\games\",
        @"\ea games\",
        @"\riot games\",
        @"\games\",
    ];

    private static readonly HashSet<string> HelperExecutables = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam.exe", "steamwebhelper.exe", "steamservice.exe", "steamerrorreporter.exe", "steamerrorreporter64.exe",
        "epicgameslauncher.exe", "epicwebhelper.exe", "epiconlineservices.exe", "epiconlineserviceshost.exe",
        "galaxyclient.exe", "galaxyclient helper.exe", "gogalaxy.exe",
        "eadesktop.exe", "eabackgroundservice.exe", "ealauncher.exe", "origin.exe", "originwebhelperservice.exe",
        "upc.exe", "ubisoftconnect.exe", "uplaywebcore.exe", "ubisoftgamelauncher.exe", "ubisoftgamelauncher64.exe",
        "battle.net.exe", "agent.exe", "blizzard error.exe",
        "riotclientservices.exe", "riotclientux.exe", "riotclientuxrender.exe", "riotclientcrashhandler.exe",
        "xboxapp.exe", "gamingservices.exe", "gamebar.exe", "gamebarftserver.exe",
        "unitycrashhandler64.exe", "unitycrashhandler32.exe", "crashreportclient.exe", "crashpad_handler.exe",
        "easyanticheat.exe", "easyanticheat_eos.exe", "easyanticheat_setup.exe", "beservice.exe", "beservice_x64.exe",
        "cefsharp.browsersubprocess.exe", "qtwebengineprocess.exe", "vc_redist.x64.exe", "vc_redist.x86.exe",
        "dxsetup.exe", "dotnetfx.exe", "framex.exe", "presentmon.exe",
    };

    private static readonly string[] HelperNameFragments = ["launcher", "crash", "setup", "redist", "unins", "updater", "report", "installer", "helper", "service", "bootstrap"];

    public static bool IsHelperExecutable(string executableName)
    {
        if (HelperExecutables.Contains(executableName))
        {
            return true;
        }

        var lower = executableName.ToLowerInvariant();
        return HelperNameFragments.Any(lower.Contains) || lower.StartsWith("presentmon", StringComparison.Ordinal);
    }

    public static bool IsInStoreLibrary(string? path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return false;
        }

        var lower = path.ToLowerInvariant();
        return StoreLibraryMarkers.Any(lower.Contains) && !lower.Contains(@"\windows\", StringComparison.Ordinal);
    }

    /// <summary>Nome sugerido: pasta do jogo dentro da biblioteca da loja, senão título da janela, senão nome do executável.</summary>
    public static string SuggestName(ProcessInfo process)
    {
        var path = process.ExecutablePath;
        if (!string.IsNullOrEmpty(path))
        {
            var lower = path.ToLowerInvariant();
            foreach (var marker in StoreLibraryMarkers)
            {
                var idx = lower.IndexOf(marker, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var rest = path[(idx + marker.Length)..];
                    var sep = rest.IndexOf('\\');
                    var folder = sep > 0 ? rest[..sep] : rest;
                    if (!string.IsNullOrWhiteSpace(folder) && !folder.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        return folder.Trim();
                    }
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(process.WindowTitle))
        {
            return process.WindowTitle.Trim();
        }

        return Path.GetFileNameWithoutExtension(process.ExecutableName);
    }

    public static IReadOnlyList<DetectedGame> Detect(
        IReadOnlyList<ProcessInfo> processes,
        IReadOnlyList<Game> library,
        IReadOnlySet<string> ignoredExecutables,
        bool allowHeuristics)
    {
        var results = new List<DetectedGame>();
        var activeGames = library.Where(g => g.Status != GameStatus.Removed).ToList();
        var removedExecutables = new HashSet<string>(
            library.Where(g => g.Status == GameStatus.Removed).SelectMany(g => g.Executables).Select(e => e.ExecutableName),
            StringComparer.OrdinalIgnoreCase);

        foreach (var game in activeGames)
        {
            var matches = processes.Where(p => game.Matches(p.ExecutableName) && !ignoredExecutables.Contains(p.ExecutableName)).ToList();
            if (matches.Count == 0)
            {
                continue;
            }

            var primary = ChoosePrimary(matches);
            results.Add(new DetectedGame(primary, game, DetectionKind.Known, game.Name, matches.Count));
        }

        if (!allowHeuristics)
        {
            return results;
        }

        var knownExecutables = new HashSet<string>(activeGames.SelectMany(g => g.Executables).Select(e => e.ExecutableName), StringComparer.OrdinalIgnoreCase);
        var heuristic = processes
            .Where(p => p.HasVisibleWindow
                        && !knownExecutables.Contains(p.ExecutableName)
                        && !removedExecutables.Contains(p.ExecutableName)
                        && !ignoredExecutables.Contains(p.ExecutableName)
                        && !IsHelperExecutable(p.ExecutableName)
                        && IsInStoreLibrary(p.ExecutablePath))
            .GroupBy(p => p.ExecutableName, StringComparer.OrdinalIgnoreCase);

        foreach (var group in heuristic)
        {
            var list = group.ToList();
            var primary = ChoosePrimary(list);
            results.Add(new DetectedGame(primary, null, DetectionKind.Heuristic, SuggestName(primary), list.Count));
        }

        return results;
    }

    /// <summary>Entre várias instâncias/processos filhos, prefere o que tem janela visível e maior uso de memória.</summary>
    public static ProcessInfo ChoosePrimary(IReadOnlyList<ProcessInfo> candidates) =>
        candidates
            .OrderByDescending(p => p.HasVisibleWindow)
            .ThenByDescending(p => p.WorkingSetBytes)
            .ThenBy(p => p.StartTime ?? DateTimeOffset.MaxValue)
            .First();
}

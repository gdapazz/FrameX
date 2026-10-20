namespace Framex.Core.Domain;

public enum GameStatus
{
    /// <summary>Confirmado pelo usuário (ou cadastrado manualmente): sessões iniciam automaticamente.</summary>
    Confirmed = 0,
    /// <summary>Detectado por heurística; aguarda confirmação. Sessões são gravadas e marcadas como não confirmadas.</summary>
    Pending = 1,
    /// <summary>Removido da biblioteca: não é monitorado, mas os relatórios antigos são preservados.</summary>
    Removed = 2,
}

public sealed class Game
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public GameStatus Status { get; set; } = GameStatus.Confirmed;
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;
    public List<GameExecutable> Executables { get; init; } = [];
    public GameProfile Profile { get; set; } = new();
    public string? Source { get; set; }

    public bool Matches(string executableName) =>
        Executables.Any(e => string.Equals(e.ExecutableName, executableName, StringComparison.OrdinalIgnoreCase));
}

public sealed record GameExecutable(string ExecutableName, string? LastKnownPath);

/// <summary>Configurações específicas de um jogo.</summary>
public sealed class GameProfile
{
    public string? OverlayLayoutId { get; set; }
    public bool CaptureFrames { get; set; } = true;
    public bool ShowOverlay { get; set; } = true;
    public bool AlertsEnabled { get; set; } = true;
}

/// <summary>Estatísticas agregadas de um jogo, calculadas a partir das sessões.</summary>
public sealed record GameStatistics(Guid GameId, int SessionCount, TimeSpan TotalMonitored, DateTimeOffset? LastSession, double? AverageFps);

public sealed record ProcessInfo(int ProcessId, string ExecutableName, string? ExecutablePath, bool HasVisibleWindow, string? WindowTitle, DateTimeOffset? StartTime, long WorkingSetBytes);

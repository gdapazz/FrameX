using Framex.Core.Alerts;
using Framex.Core.Domain;
using Framex.Core.FrameCapture;

namespace Framex.Core.Persistence;

/// <summary>
/// Contratos de persistência. As implementações SQLite são síncronas (o provedor não tem I/O assíncrono real);
/// chame-as fora da thread da interface.
/// </summary>
public interface IGameRepository
{
    IReadOnlyList<Game> GetAll(bool includeRemoved = false);
    Game? Get(Guid id);
    void Upsert(Game game);
    IReadOnlyDictionary<Guid, GameStatistics> GetStatistics();
    IReadOnlySet<string> GetIgnoredExecutables();
    void SetIgnored(string executableName, bool ignored);
}

public interface ISessionRepository
{
    void Create(Session session);
    void Append(Guid sessionId, IReadOnlyList<MetricSample> samples, IReadOnlyList<FrameSample> frames, IReadOnlyList<SessionEvent> events);
    void Complete(Guid sessionId, DateTimeOffset endedAt, SessionState state, SessionSummary summary);
    IReadOnlyList<SessionListItem> Query(SessionQuery query);
    Session? Get(Guid sessionId);
    IReadOnlyList<MetricSample> GetSamples(Guid sessionId);
    IReadOnlyList<double> GetFrameTimes(Guid sessionId);
    IReadOnlyList<SessionEvent> GetEvents(Guid sessionId);
    IReadOnlyList<Session> GetActive();
    DateTimeOffset? GetLastDataTimestamp(Guid sessionId);
    void Delete(IEnumerable<Guid> sessionIds);

    /// <summary>Remove amostras detalhadas (não os resumos) de sessões encerradas antes de <paramref name="olderThan"/>.</summary>
    int PurgeDetailedData(DateTimeOffset olderThan);
}

public interface ISettingsStore
{
    T? Load<T>(string key) where T : class;
    void Save<T>(string key, T value) where T : class;
}

public interface IAlertHistoryRepository
{
    void Add(AlertRecord record);
    IReadOnlyList<AlertRecord> GetRecent(int limit);
}

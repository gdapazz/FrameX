namespace Framex.Core.SetupScore;

public enum MatchState
{
    ExactMatch,
    VerifiedAlias,
    UserSelected,
    ProbableMatch,
    AmbiguousMatch,
    NotFound,
    SourceUnavailable,
    InvalidRecord,
    IncompleteName,
}

public sealed record MatchResult(MatchState State, BenchmarkRecord? Record, IReadOnlyList<BenchmarkRecord> Candidates, string Detail)
{
    /// <summary>Somente correspondências confirmadas autorizam pontuação.</summary>
    public bool IsConfirmed => State is MatchState.ExactMatch or MatchState.VerifiedAlias or MatchState.UserSelected;
}

/// <summary>
/// Correspondência em etapas entre o nome reportado pelo Windows e os registros da base:
/// A) fabricante pelo ID PCI (escolhe a base) · B) igualdade após normalização estrita (exata) ou estendida (alias verificado)
/// · C) candidatos aproximados apenas como sugestão (provável/ambíguo), nunca pontuados automaticamente.
/// </summary>
public static class ComponentMatcher
{
    public const int MaxCandidates = 6;

    public static MatchResult Match(string localName, SetupCategory category, BenchmarkDataset? dataset, string? userSelectedRecord = null)
    {
        if (dataset is null)
        {
            return new MatchResult(MatchState.SourceUnavailable, null, [], "A base de referência não está disponível.");
        }

        if (userSelectedRecord is not null)
        {
            var chosen = dataset.Records.FirstOrDefault(r => string.Equals(r.DeviceName, userSelectedRecord, StringComparison.Ordinal));
            if (chosen is not null)
            {
                return new MatchResult(MatchState.UserSelected, chosen, [], "Modelo escolhido manualmente pelo usuário.");
            }
        }

        if (HardwareNameNormalizer.IsIncomplete(localName, category))
        {
            return new MatchResult(MatchState.IncompleteName, null, [], "O nome reportado não contém número de modelo.");
        }

        var strict = HardwareNameNormalizer.Strict(localName);
        var exact = dataset.ByStrict(strict);
        if (exact.Count > 0)
        {
            return new MatchResult(MatchState.ExactMatch, Representative(exact), exact.Count > 1 ? exact : [],
                exact.Count > 1 ? $"{exact.Count} registros com o mesmo nome normalizado; usado o de mais envios." : "Nome idêntico ao da base (após normalização tipográfica).");
        }

        var extended = HardwareNameNormalizer.Extended(localName, category);
        var alias = dataset.ByExtended(extended);
        if (alias.Count > 0)
        {
            return new MatchResult(MatchState.VerifiedAlias, Representative(alias), alias.Count > 1 ? alias : [],
                "Mesmo modelo após remover anotações documentadas como irrelevantes (frequência, \"CPU\", contagem de núcleos por extenso).");
        }

        if (dataset.RejectedByStrict(strict) is { } rejected)
        {
            return new MatchResult(MatchState.InvalidRecord, null, [], rejected.Reason);
        }

        var candidates = Candidates(extended, category, dataset);
        return candidates.Count switch
        {
            0 => new MatchResult(MatchState.NotFound, null, [], "Nenhum registro com o mesmo número de modelo."),
            1 => new MatchResult(MatchState.ProbableMatch, null, candidates, "Um registro semelhante foi encontrado, mas o nome não coincide; requer confirmação."),
            _ => new MatchResult(MatchState.AmbiguousMatch, null, candidates, $"{candidates.Count} registros semelhantes; não é possível escolher um com segurança."),
        };
    }

    /// <summary>Entre registros equivalentes, o de mais envios (empate: maior nome em ordem ordinal, para ser determinístico).</summary>
    private static BenchmarkRecord Representative(IReadOnlyList<BenchmarkRecord> records) =>
        records.OrderByDescending(r => r.Samples).ThenBy(r => r.DeviceName, StringComparer.Ordinal).First();

    /// <summary>Registros que contêm todos os números de modelo do nome local, por similaridade de tokens.</summary>
    public static IReadOnlyList<BenchmarkRecord> Candidates(string extendedLocal, SetupCategory category, BenchmarkDataset dataset)
    {
        var tokens = HardwareNameNormalizer.ModelTokens(extendedLocal);
        if (tokens.Count == 0)
        {
            return [];
        }

        return dataset.Records
            .Select(r => (Record: r, Name: HardwareNameNormalizer.Extended(r.DeviceName, category)))
            .Where(x =>
            {
                var recordTokens = x.Name.Split(' ');
                return tokens.All(t => recordTokens.Contains(t, StringComparer.Ordinal));
            })
            .Select(x => (x.Record, Similarity: HardwareNameNormalizer.TokenSimilarity(extendedLocal, x.Name)))
            .Where(x => x.Similarity >= 0.4)
            .OrderByDescending(x => x.Similarity)
            .ThenByDescending(x => x.Record.Samples)
            .ThenBy(x => x.Record.DeviceName, StringComparer.Ordinal)
            .Take(MaxCandidates)
            .Select(x => x.Record)
            .ToList();
    }
}

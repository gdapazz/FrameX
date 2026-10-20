using System.Globalization;

namespace Framex.Core.SetupScore;

/// <summary>Rótulos e mensagens padronizadas da Nota do Setup (compartilhados entre interface e relatório exportado).</summary>
public static class SetupScoreText
{
    public static readonly CultureInfo Pt = CultureInfo.GetCultureInfo("pt-BR");

    public static string Category(SetupCategory category) => OverallScoreCalculator.Label(category);

    public static string Match(MatchState? state) => state switch
    {
        MatchState.ExactMatch => "Correspondência exata",
        MatchState.VerifiedAlias => "Alias verificado",
        MatchState.UserSelected => "Escolhido manualmente",
        MatchState.ProbableMatch => "Correspondência provável (não confirmada)",
        MatchState.AmbiguousMatch => "Correspondência ambígua",
        MatchState.NotFound => "Não encontrado na base",
        MatchState.SourceUnavailable => "Fonte indisponível",
        MatchState.InvalidRecord => "Registro inválido",
        MatchState.IncompleteName => "Nome incompleto",
        _ => "Não se aplica",
    };

    public static string Confidence(DataConfidence confidence) => confidence switch
    {
        DataConfidence.High => "Alta",
        DataConfidence.Medium => "Média",
        DataConfidence.Low => "Baixa",
        _ => "Sem dados",
    };

    public static string Completeness(EvaluationCompleteness completeness) => completeness switch
    {
        EvaluationCompleteness.Complete => "Avaliação completa",
        EvaluationCompleteness.Partial => "Avaliação parcial",
        _ => "Avaliação incompleta",
    };

    public static string Status(CategoryStatus status) => status switch
    {
        CategoryStatus.Scored => "Avaliado",
        CategoryStatus.ExcludedByUser => "Não participou",
        _ => "Não avaliado",
    };

    public static string GpuKindLabel(GpuKind kind) => kind switch
    {
        GpuKind.Dedicated => "Dedicada",
        GpuKind.Integrated => "Integrada",
        GpuKind.Virtual => "Virtual",
        _ => "Tipo não confirmado",
    };

    public static string Percent(double fraction) => (fraction * 100).ToString("0.##", Pt) + "%";

    public static string Number(double value, string format = "0.#") => value.ToString(format, Pt);

    public static string Reason(IssueKind kind, string? extra = null) => kind switch
    {
        IssueKind.NotInDatabase => "Não encontramos uma correspondência confirmada para este modelo na base consultada. Isso pode ocorrer porque o componente é recente, antigo, regional ou ainda não foi incluído no conjunto de dados.",
        IssueKind.IncompleteLocalName => "O Windows forneceu um nome de hardware insuficiente para distinguir este modelo de outras variantes. A identificação exata não pôde ser confirmada.",
        IssueKind.SourceUnavailable => "Não foi possível carregar esta fonte (nenhuma cópia local válida e a consulta não foi concluída). Isso não significa que o componente seja desconhecido ou incompatível." + Suffix(extra),
        IssueKind.InvalidSource => "A cópia local da base não passou na validação de integridade e esquema e não pôde ser usada." + Suffix(extra),
        IssueKind.AmbiguousRecords => "Encontramos mais de um modelo compatível com o nome detectado, mas não há informações suficientes para escolher um deles com segurança.",
        IssueKind.ProbableNeedsConfirmation => "Encontramos um modelo semelhante, mas o nome não coincide exatamente. Por segurança, ele não foi usado sem a sua confirmação.",
        IssueKind.MissingPerformanceData => "O componente foi identificado, mas a base não contém resultados de desempenho comparáveis para ele." + Suffix(extra),
        IssueKind.InvalidRecord => "A fonte contém um registro para este modelo, mas ele está incompleto ou inválido e foi rejeitado na validação." + Suffix(extra),
        IssueKind.InsufficientLocalData => "O Windows não forneceu as informações necessárias para confirmar este componente." + Suffix(extra),
        IssueKind.LowSampleCount => "O modelo foi identificado, mas a mediana da base se apoia em poucos envios." + Suffix(extra),
        IssueKind.GpuSelectionAmbiguous => "Há mais de um adaptador gráfico que pode ser usado em jogos e não é possível determinar qual deles com segurança." + Suffix(extra),
        IssueKind.StorageNoEligibleDrive => "Nenhuma unidade física interna elegível teve a capacidade total confirmada." + Suffix(extra),
        IssueKind.StorageDriveUncertain => "Uma unidade foi detectada, mas não pôde ser classificada com segurança." + Suffix(extra),
        IssueKind.MemorySourcesDisagree => "As fontes do Windows informam capacidades de memória diferentes." + Suffix(extra),
        IssueKind.MultipleProcessors => "Mais de um processador foi reportado." + Suffix(extra),
        _ => extra ?? "",
    };

    public static string Consequence(IssueKind kind, SetupCategory category)
    {
        if (kind == IssueKind.LowSampleCount)
        {
            return "A categoria foi pontuada, mas com confiabilidade baixa.";
        }

        if (kind == IssueKind.MemorySourcesDisagree)
        {
            return "Foi usada a capacidade instalada informada pelo firmware; a confiabilidade da RAM foi reduzida para média.";
        }

        if (kind == IssueKind.StorageDriveUncertain)
        {
            return "A unidade não foi somada à capacidade total; a confiabilidade do armazenamento foi reduzida.";
        }

        if (kind == IssueKind.MultipleProcessors)
        {
            return "Foi avaliado o primeiro processador reportado; a nota não soma processadores.";
        }

        return category == SetupCategory.Storage
            ? "O armazenamento não entrou no cálculo: a nota foi marcada como parcial e os pesos de GPU, CPU e RAM foram renormalizados. A ausência não foi tratada como zero."
            : $"A categoria {Category(category)} não entrou no cálculo e a nota geral não foi emitida (avaliação incompleta). Nenhuma nota zero foi atribuída.";
    }

    public static string? Action(IssueKind kind) => kind switch
    {
        IssueKind.NotInDatabase => "Atualize as bases de dados e tente novamente. Se houver modelos semelhantes listados, você pode escolher o correto manualmente.",
        IssueKind.IncompleteLocalName => "Atualize o driver do componente e tente novamente; se o modelo correto aparecer entre os candidatos, selecione-o manualmente.",
        IssueKind.SourceUnavailable => "Verifique a conexão e use \"Atualizar bases\", ou tente novamente mais tarde.",
        IssueKind.InvalidSource => "Use \"Atualizar bases\" para baixar uma cópia válida.",
        IssueKind.AmbiguousRecords or IssueKind.ProbableNeedsConfirmation => "Escolha o modelo correto entre os candidatos para incluí-lo no cálculo.",
        IssueKind.MissingPerformanceData => "Não há ação local disponível; a categoria permanecerá sem nota nesta metodologia.",
        IssueKind.InvalidRecord => "Atualize as bases de dados; se o problema continuar, o registro precisa ser corrigido na fonte.",
        IssueKind.InsufficientLocalData => "Verifique se os drivers estão instalados e tente novamente.",
        IssueKind.GpuSelectionAmbiguous => "Escolha qual GPU deve ser avaliada.",
        IssueKind.StorageNoEligibleDrive or IssueKind.StorageDriveUncertain => "Verifique o disco no Gerenciador de Dispositivos ou desative o armazenamento na nota geral.",
        IssueKind.LowSampleCount => "Nenhuma ação é necessária. \"Atualizar bases\" pode trazer mais envios para este modelo no futuro.",
        IssueKind.MemorySourcesDisagree => "Confira a memória instalada nas Configurações do Windows; módulos reservados pelo firmware ou pela GPU integrada podem explicar a diferença.",
        IssueKind.MultipleProcessors => "Nenhuma ação local; a nota considera apenas o primeiro processador reportado.",
        _ => null,
    };

    private static string Suffix(string? extra) => string.IsNullOrWhiteSpace(extra) ? "" : " " + extra;
}

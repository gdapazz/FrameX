namespace Framex.Core.SetupScore;

/// <summary>
/// Escala logística sobre o logaritmo da pontuação de benchmark, com âncoras fixas por versão da metodologia:
/// <c>s = 1000 / (1 + e^(−k·(ln x − m)))</c>, onde <c>m = (ln L + ln H)/2</c> e <c>k = 2·ln 19 / (ln H − ln L)</c>.
/// Assim, L recebe 50 pontos, H recebe 950 e a média geométrica de L e H recebe 500.
/// Propriedades: estritamente crescente (preserva a ordem), limitada a (0, 1000), proporções iguais de desempenho
/// valem distâncias iguais na escala (o dobro da pontuação não dobra a nota) e valores extremos não deslocam a escala.
/// </summary>
public sealed record LogisticScale(double Low, double High)
{
    private static readonly double Ln19 = Math.Log(19);

    public double Midpoint => (Math.Log(Low) + Math.Log(High)) / 2;

    public double Steepness => 2 * Ln19 / (Math.Log(High) - Math.Log(Low));

    public double Score(double benchmark)
    {
        if (!double.IsFinite(benchmark) || benchmark <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(benchmark), "A pontuação de benchmark deve ser positiva e finita.");
        }

        return 1000 / (1 + Math.Exp(-Steepness * (Math.Log(benchmark) - Midpoint)));
    }
}

/// <summary>
/// Curva por trechos com interpolação no logaritmo base 2 da capacidade (cada duplicação soma um valor decrescente).
/// Abaixo do primeiro ponto, interpolação linear a partir de zero; acima do último, a nota satura.
/// </summary>
public sealed record CapacityCurve(IReadOnlyList<(double Capacity, double Score)> Points)
{
    public double Score(double capacity)
    {
        if (!double.IsFinite(capacity) || capacity <= 0)
        {
            return 0;
        }

        var first = Points[0];
        if (capacity <= first.Capacity)
        {
            return first.Score * (capacity / first.Capacity);
        }

        for (var i = 1; i < Points.Count; i++)
        {
            var (c1, s1) = Points[i];
            if (capacity <= c1)
            {
                var (c0, s0) = Points[i - 1];
                var t = (Math.Log2(capacity) - Math.Log2(c0)) / (Math.Log2(c1) - Math.Log2(c0));
                return s0 + ((s1 - s0) * t);
            }
        }

        return Points[^1].Score;
    }
}

public sealed record ScoreTier(string Name, int Min, int Max);

/// <summary>
/// Metodologia da Nota do Setup. Qualquer mudança em pesos, âncoras, curvas, faixas ou bases de referência
/// exige nova <see cref="Version"/>, testes de regressão e registro em docs/NOTA-DO-SETUP.md.
/// Avaliações salvas guardam a versão usada e nunca são recalculadas.
/// </summary>
public static class SetupScoreMethodology
{
    public const string Version = "1.0";

    public const string BlenderVersion = "4.5.0";

    public const int MaxScore = 1000;

    /// <summary>Pesos originais (somam 1). Com o armazenamento desativado, os demais são renormalizados.</summary>
    public static IReadOnlyList<(SetupCategory Category, double Weight)> BaseWeights { get; } =
    [
        (SetupCategory.Gpu, 0.45),
        (SetupCategory.Cpu, 0.35),
        (SetupCategory.Ram, 0.12),
        (SetupCategory.Storage, 0.08),
    ];

    public static double WeightOf(SetupCategory category) => BaseWeights.First(w => w.Category == category).Weight;

    /// <summary>Categorias sem as quais a nota geral não é emitida.</summary>
    public static IReadOnlyList<SetupCategory> RequiredCategories { get; } = [SetupCategory.Gpu, SetupCategory.Cpu, SetupCategory.Ram];

    /// <summary>
    /// CPU: pontuação mediana do Blender Benchmark 4.5.0 (Cycles, CPU). Âncoras ≈ percentis 5 e 95 dos 662 processadores
    /// com ≥ 3 envios na base de 20/10/2026, arredondados e congelados nesta versão.
    /// </summary>
    public static LogisticScale CpuScale { get; } = new(25, 775);

    /// <summary>
    /// GPU: pontuação mediana do Blender Benchmark 4.5.0 (Cycles, GPU: OptiX/NVIDIA, HIP/AMD, oneAPI/Intel). Âncoras ≈ percentis
    /// 5 e 95 das 267 GPUs com ≥ 3 envios nessas três bases em 20/10/2026, arredondados e congelados nesta versão.
    /// </summary>
    public static LogisticScale GpuScale { get; } = new(120, 9700);

    /// <summary>
    /// RAM (GiB instalados). Pontos escolhidos a partir de requisitos de sistema publicados por jogos para PC:
    /// 8 GB é o mínimo mais comum, 16 GB o recomendado mais comum e 32 GB aparece como recomendado em títulos recentes exigentes.
    /// Acima de 64 GiB não há ganho na nota.
    /// </summary>
    public static CapacityCurve RamCurve { get; } = new([(2, 0), (4, 150), (8, 450), (16, 750), (32, 920), (64, 1000)]);

    /// <summary>
    /// Armazenamento (GB decimais instalados). Jogos atuais ocupam dezenas a mais de cem GB cada; 256 GB comportam o sistema
    /// e poucos jogos; ~1 TB é confortável; acima de 4 TB não há ganho na nota. Mede capacidade, não velocidade.
    /// </summary>
    public static CapacityCurve StorageCurve { get; } = new([(128, 150), (256, 350), (512, 600), (1000, 800), (2000, 920), (4000, 1000)]);

    /// <summary>Faixas textuais da nota geral (nomenclatura própria do FRAMEX, não um padrão de mercado).</summary>
    public static IReadOnlyList<ScoreTier> Tiers { get; } =
    [
        new("Entrada", 0, 199),
        new("Básico", 200, 399),
        new("Intermediário", 400, 549),
        new("Avançado", 550, 699),
        new("Alto desempenho", 700, 849),
        new("Entusiasta", 850, 1000),
    ];

    public static ScoreTier TierFor(int score)
    {
        if (score < 0 || score > MaxScore)
        {
            throw new ArgumentOutOfRangeException(nameof(score));
        }

        return Tiers.First(t => score >= t.Min && score <= t.Max);
    }

    /// <summary>Envios mínimos para confiabilidade alta/média de um registro de benchmark.</summary>
    public const int HighConfidenceSamples = 10;

    public const int MediumConfidenceSamples = 3;

    public static int Round(double value) => (int)Math.Clamp(Math.Round(value, MidpointRounding.AwayFromZero), 0, MaxScore);
}

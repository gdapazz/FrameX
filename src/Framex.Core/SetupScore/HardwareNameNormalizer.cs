using System.Text;
using System.Text.RegularExpressions;

namespace Framex.Core.SetupScore;

/// <summary>
/// Normalização de nomes de componentes em dois níveis:
/// <list type="bullet">
/// <item><b>Estrito</b>: só remove diferenças tipográficas (caixa, ®/™/(R)/(TM), hífens, pontuação, espaços).</item>
/// <item><b>Estendido</b>: além disso, remove anotações documentadas como irrelevantes para a identidade do modelo de CPU
/// — frequência entre parênteses de marketing ("@ 3.30GHz"), as palavras "CPU"/"Processor" e a contagem de núcleos por extenso
/// ("6-Core", "Eight-Core"), que são implicadas pelo número do modelo.</item>
/// </list>
/// Sufixos que distinguem produtos (Ti, SUPER, XT, X, X3D, G, F, K, KF, H, HX, U, M, Laptop GPU, Max-Q…) nunca são removidos.
/// </summary>
public static partial class HardwareNameNormalizer
{
    private static readonly string[] Trademarks = ["(r)", "(tm)", "(c)", "®", "™", "©"];

    public static string Strict(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "";
        }

        // Os símbolos saem antes da normalização de compatibilidade, que transformaria "™" em "TM".
        var s = raw.ToLowerInvariant();
        foreach (var t in Trademarks)
        {
            s = s.Replace(t, " ", StringComparison.Ordinal);
        }

        s = s.Normalize(NormalizationForm.FormKC);

        var sb = new StringBuilder(s.Length);
        foreach (var ch in s)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch is '.' or '+' or '@' ? ch : ' ');
        }

        return Spaces().Replace(sb.ToString(), " ").Trim();
    }

    public static string Extended(string? raw, SetupCategory category)
    {
        var s = Strict(raw);
        if (category == SetupCategory.Cpu)
        {
            s = Frequency().Replace(s, " ");
            s = CoreCount().Replace(s, " ");
            s = CpuWords().Replace(s, " ");
        }

        s = s.Replace("@", " ", StringComparison.Ordinal);
        return Spaces().Replace(s, " ").Trim();
    }

    /// <summary>Tokens que contêm dígitos (números de modelo). Dois nomes com conjuntos diferentes nunca são o mesmo produto.</summary>
    public static IReadOnlyList<string> ModelTokens(string normalized) =>
        normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(t => t.Any(char.IsDigit)).ToList();

    /// <summary>Nome sem número de modelo (ex.: "AMD Radeon(TM) Graphics") não permite distinguir variantes.</summary>
    public static bool IsIncomplete(string? raw, SetupCategory category) => ModelTokens(Extended(raw, category)).Count == 0;

    public static double TokenSimilarity(string a, string b)
    {
        var ta = a.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        var tb = b.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
        if (ta.Count == 0 || tb.Count == 0)
        {
            return 0;
        }

        var inter = ta.Count(tb.Contains);
        return (double)inter / (ta.Count + tb.Count - inter);
    }

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"@\s*[0-9.]+\s*[gm]hz\b")]
    private static partial Regex Frequency();

    [GeneratedRegex(@"\b([0-9]+|dual|triple|quad|six|eight|ten|twelve|sixteen)\s+core\s+processor\b|\b([0-9]+|dual|triple|quad|six|eight|ten|twelve|sixteen)\s+core\b(?=\s*$)")]
    private static partial Regex CoreCount();

    [GeneratedRegex(@"\b(cpu|processor|apu)\b")]
    private static partial Regex CpuWords();
}

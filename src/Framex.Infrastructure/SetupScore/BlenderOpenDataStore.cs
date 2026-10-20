using System.Collections.Concurrent;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Framex.Core.SetupScore;
using Microsoft.Extensions.Logging;

namespace Framex.Infrastructure.SetupScore;

/// <summary>
/// Bases do Blender Open Data (CC0) para a Nota do Setup.
/// Ordem de leitura: cache local validado → cópia embutida no FRAMEX → indisponível.
/// A atualização é explícita (nunca automática), baixa apenas listas completas (o inventário do computador não é enviado),
/// só aceita HTTPS no host fixo, não segue redirecionamentos, limita tamanho, valida esquema e conteúdo
/// e só substitui o cache depois que a nova cópia foi validada (troca atômica de arquivo).
/// </summary>
public sealed class BlenderOpenDataStore : IBenchmarkDatasetSource, IDisposable
{
    public const string Host = "opendata.blender.org";
    private const int Retries = 3;

    private static readonly DateTimeOffset BundledRetrievedAt = new(2026, 10, 20, 15, 0, 0, TimeSpan.Zero);

    public static readonly IReadOnlyList<BenchmarkDatasetDefinition> AllDefinitions =
    [
        Def("blender-4.5.0-cpu", SetupCategory.Cpu, "CPU", "Blender Benchmark 4.5.0 · CPU", null),
        Def("blender-4.5.0-optix", SetupCategory.Gpu, "OptiX", "Blender Benchmark 4.5.0 · GPU NVIDIA (OptiX)", 0x10DE),
        Def("blender-4.5.0-hip", SetupCategory.Gpu, "HIP", "Blender Benchmark 4.5.0 · GPU AMD (HIP)", 0x1002),
        Def("blender-4.5.0-oneapi", SetupCategory.Gpu, "oneAPI", "Blender Benchmark 4.5.0 · GPU Intel (oneAPI)", 0x8086),
    ];

    /// <summary>SHA-256 das cópias embutidas (obtidas em 20/10/2026); conferido a cada leitura.</summary>
    private static readonly Dictionary<string, string> BundledSha = new(StringComparer.Ordinal)
    {
        ["blender-4.5.0-cpu"] = "996f826503fc22ee136f90eedf7a6ecb5d757f2d9814be6ab71dd35750eaa134",
        ["blender-4.5.0-optix"] = "0b16618d9add05bf8f8020813ffa9987233c593e33d32f81008916e667fe92a6",
        ["blender-4.5.0-hip"] = "e06adbde9164504b66ba16566efd8cab5a4b3ec600c7da2776e556ac66be4e48",
        ["blender-4.5.0-oneapi"] = "5aba712cde325ba9b7e27afe0fefba7fec5c72e3c3a7dcb368a9829d048a9ba8",
    };

    private readonly string _directory;
    private readonly HttpClient _http;
    private readonly TimeProvider _time;
    private readonly ILogger<BlenderOpenDataStore> _logger;
    private readonly ConcurrentDictionary<string, DatasetLoadResult> _loaded = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _updateGate = new(1, 1);
    private readonly Func<int, TimeSpan> _backoff;
    private readonly bool _useBundled;

    public BlenderOpenDataStore(AppPaths paths, TimeProvider time, ILogger<BlenderOpenDataStore> logger)
        : this(Path.Combine(paths.Root, "datasets"), new HttpClientHandler { AllowAutoRedirect = false, AutomaticDecompression = DecompressionMethods.All }, time, logger)
    {
    }

    internal BlenderOpenDataStore(string directory, HttpMessageHandler handler, TimeProvider time, ILogger<BlenderOpenDataStore> logger, Func<int, TimeSpan>? backoff = null, bool useBundled = true)
    {
        _directory = directory;
        _time = time;
        _logger = logger;
        _backoff = backoff ?? (attempt => TimeSpan.FromSeconds(Math.Pow(2, attempt)));
        _useBundled = useBundled;
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("FRAMEX-SetupScore/1.0");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
    }

    public IReadOnlyList<BenchmarkDatasetDefinition> Definitions => AllDefinitions;

    public bool IsUpdating => _updateGate.CurrentCount == 0;

    private static BenchmarkDatasetDefinition Def(string id, SetupCategory category, string backend, string title, ushort? vendor) =>
        new(id, category, backend, title, SetupScoreMethodology.BlenderVersion,
            $"https://{Host}/benchmarks/query/?blender_version={SetupScoreMethodology.BlenderVersion}&group_by=device_name&response_type=datatables&compute_type={backend.ToUpperInvariant()}",
            vendor);

    public Task<DatasetLoadResult> LoadAsync(BenchmarkDatasetDefinition definition, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_loaded.GetOrAdd(definition.Id, _ => Load(definition)));
    }

    /// <summary>Data e origem da cópia em uso (para exibir "dados de dd/mm/aaaa" na interface, mesmo offline).</summary>
    public IReadOnlyList<DatasetLoadResult> Status() => AllDefinitions.Select(d => _loaded.GetOrAdd(d.Id, _ => Load(d))).ToList();

    private DatasetLoadResult Load(BenchmarkDatasetDefinition def)
    {
        string? cacheError = null;
        var cached = TryLoadCache(def, ref cacheError);
        if (cached is not null)
        {
            return new DatasetLoadResult(def, cached, DatasetFailure.None, null);
        }

        if (_useBundled && TryLoadBundled(def, out var bundled, out var bundledError))
        {
            return new DatasetLoadResult(def, bundled, DatasetFailure.None, cacheError);
        }

        var error = string.Join(" ", new[] { cacheError, _useBundled ? null : "Cópia embutida desativada." }.Where(e => e is not null));
        return new DatasetLoadResult(def, null, cacheError is not null ? DatasetFailure.Invalid : DatasetFailure.NotAvailable,
            error.Length > 0 ? error : "Nenhuma cópia local disponível.");
    }

    private string DataPath(string id) => Path.Combine(_directory, id + ".json");

    private string MetaPath(string id) => Path.Combine(_directory, id + ".meta.json");

    private sealed record CacheMeta(string Id, string SourceUrl, DateTimeOffset RetrievedAt, string Sha256, int RecordCount, int SchemaVersion);

    private BenchmarkDataset? TryLoadCache(BenchmarkDatasetDefinition def, ref string? error)
    {
        var dataPath = DataPath(def.Id);
        var metaPath = MetaPath(def.Id);
        if (!File.Exists(dataPath) || !File.Exists(metaPath))
        {
            return null;
        }

        try
        {
            if (new FileInfo(dataPath).Length > BlenderOpenDataParser.MaxBytes || new FileInfo(metaPath).Length > 64 * 1024)
            {
                error = "Cópia local acima do tamanho permitido.";
                return null;
            }

            var meta = JsonSerializer.Deserialize<CacheMeta>(File.ReadAllText(metaPath, Encoding.UTF8));
            var bytes = File.ReadAllBytes(dataPath);
            if (meta is not { SchemaVersion: 1 } || meta.Id != def.Id || !string.Equals(meta.Sha256, Sha(bytes), StringComparison.OrdinalIgnoreCase))
            {
                error = "A cópia local não confere com seus metadados (integridade).";
                _logger.LogWarning("Cache da base {Id} com falha de integridade; usando a cópia embutida", def.Id);
                return null;
            }

            var parsed = BlenderOpenDataParser.Parse(Encoding.UTF8.GetString(bytes));
            if (!parsed.Success)
            {
                error = "A cópia local é inválida: " + parsed.Error;
                return null;
            }

            return Create(def, parsed, meta.RetrievedAt, meta.Sha256, $"atualizada em {meta.RetrievedAt.ToLocalTime():dd/MM/yyyy}");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            _logger.LogWarning(ex, "Falha ao ler o cache da base {Id}", def.Id);
            error = "Falha ao ler a cópia local: " + ex.Message;
            return null;
        }
    }

    private bool TryLoadBundled(BenchmarkDatasetDefinition def, out BenchmarkDataset? dataset, out string? error)
    {
        dataset = null;
        error = null;
        using var stream = typeof(BlenderOpenDataStore).Assembly.GetManifestResourceStream($"Framex.Infrastructure.SetupScore.Data.{def.Id}.json");
        if (stream is null)
        {
            error = "Cópia embutida ausente.";
            return false;
        }

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        var bytes = ms.ToArray();
        var sha = Sha(bytes);
        if (!BundledSha.TryGetValue(def.Id, out var expected) || !string.Equals(sha, expected, StringComparison.Ordinal))
        {
            error = "Cópia embutida com falha de integridade.";
            _logger.LogError("Cópia embutida da base {Id} não confere com o SHA-256 registrado", def.Id);
            return false;
        }

        var parsed = BlenderOpenDataParser.Parse(Encoding.UTF8.GetString(bytes));
        if (!parsed.Success)
        {
            error = parsed.Error;
            return false;
        }

        dataset = Create(def, parsed, BundledRetrievedAt, sha, "cópia incluída no FRAMEX");
        return true;
    }

    private static BenchmarkDataset Create(BenchmarkDatasetDefinition def, DatasetParseResult parsed, DateTimeOffset retrievedAt, string sha, string origin) =>
        new(def, new BenchmarkDatasetInfo(def.Id, def.Title, def.SourceUrl, BenchmarkDatasetDefinition.License, def.BlenderVersion, retrievedAt, sha,
            parsed.Records.Count, parsed.Rejected.Count, origin), parsed.Records, parsed.Rejected);

    public async Task<IReadOnlyList<DatasetUpdateResult>> UpdateAsync(CancellationToken cancellationToken)
    {
        if (!await _updateGate.WaitAsync(0, cancellationToken).ConfigureAwait(false))
        {
            return [new DatasetUpdateResult("*", false, false, DatasetFailure.None, "Uma atualização já está em andamento.")];
        }

        try
        {
            var results = new List<DatasetUpdateResult>();
            foreach (var def in AllDefinitions)
            {
                cancellationToken.ThrowIfCancellationRequested();
                results.Add(await UpdateOneAsync(def, cancellationToken).ConfigureAwait(false));
            }

            return results;
        }
        finally
        {
            _updateGate.Release();
        }
    }

    private async Task<DatasetUpdateResult> UpdateOneAsync(BenchmarkDatasetDefinition def, CancellationToken ct)
    {
        var uri = new Uri(def.SourceUrl);
        if (uri.Scheme != Uri.UriSchemeHttps || !string.Equals(uri.Host, Host, StringComparison.OrdinalIgnoreCase))
        {
            return new DatasetUpdateResult(def.Id, false, false, DatasetFailure.Invalid, "Endereço de origem não permitido.");
        }

        byte[]? bytes = null;
        string? lastError = null;
        var failure = DatasetFailure.Network;
        for (var attempt = 0; attempt < Retries && bytes is null; attempt++)
        {
            if (attempt > 0)
            {
                await Task.Delay(_backoff(attempt - 1), _time, ct).ConfigureAwait(false);
            }

            try
            {
                bytes = await DownloadAsync(uri, ct).ConfigureAwait(false);
            }
            catch (HttpRequestException ex)
            {
                lastError = ex.Message;
                failure = DatasetFailure.Network;
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                lastError = "A fonte não respondeu dentro do tempo limite.";
                failure = DatasetFailure.Timeout;
            }
            catch (InvalidDataException ex)
            {
                return new DatasetUpdateResult(def.Id, false, false, DatasetFailure.Invalid, ex.Message + " A cópia anterior foi mantida.");
            }
        }

        if (bytes is null)
        {
            _logger.LogWarning("Atualização da base {Id} falhou: {Error}", def.Id, lastError);
            return new DatasetUpdateResult(def.Id, false, false, failure, $"{lastError} A cópia anterior foi mantida.");
        }

        var parsed = BlenderOpenDataParser.Parse(Encoding.UTF8.GetString(bytes));
        if (!parsed.Success)
        {
            _logger.LogWarning("Base {Id} baixada é inválida: {Error}", def.Id, parsed.Error);
            return new DatasetUpdateResult(def.Id, false, false, DatasetFailure.Invalid, $"Dados recebidos inválidos ({parsed.Error}). A cópia anterior foi mantida.");
        }

        var sha = Sha(bytes);
        var current = _loaded.TryGetValue(def.Id, out var cur) ? cur.Dataset?.Info.Sha256 : null;
        current ??= Load(def).Dataset?.Info.Sha256;
        if (string.Equals(current, sha, StringComparison.Ordinal))
        {
            return new DatasetUpdateResult(def.Id, false, true, DatasetFailure.None, "Sem alterações na fonte.");
        }

        try
        {
            Directory.CreateDirectory(_directory);
            var meta = new CacheMeta(def.Id, def.SourceUrl, _time.GetUtcNow(), sha, parsed.Records.Count, 1);
            var dataTmp = DataPath(def.Id) + ".tmp";
            var metaTmp = MetaPath(def.Id) + ".tmp";
            await File.WriteAllBytesAsync(dataTmp, bytes, ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(metaTmp, JsonSerializer.Serialize(meta), new UTF8Encoding(false), ct).ConfigureAwait(false);
            File.Move(dataTmp, DataPath(def.Id), overwrite: true);
            File.Move(metaTmp, MetaPath(def.Id), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Falha ao gravar a base {Id}", def.Id);
            return new DatasetUpdateResult(def.Id, false, false, DatasetFailure.Invalid, "Não foi possível gravar a nova cópia: " + ex.Message);
        }

        _loaded.TryRemove(def.Id, out _);
        _logger.LogInformation("Base {Id} atualizada: {Count} registros ({Rejected} rejeitados)", def.Id, parsed.Records.Count, parsed.Rejected.Count);
        return new DatasetUpdateResult(def.Id, true, false, DatasetFailure.None,
            $"{parsed.Records.Count} registros válidos" + (parsed.Rejected.Count > 0 ? $", {parsed.Rejected.Count} rejeitados na validação." : "."));
    }

    private async Task<byte[]> DownloadAsync(Uri uri, CancellationToken ct)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        if ((int)response.StatusCode is >= 300 and < 400)
        {
            throw new InvalidDataException($"A fonte respondeu com redirecionamento ({(int)response.StatusCode}), que não é seguido por segurança.");
        }

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"A fonte respondeu com HTTP {(int)response.StatusCode}.");
        }

        if (response.Content.Headers.ContentLength is > BlenderOpenDataParser.MaxBytes)
        {
            throw new InvalidDataException("Resposta maior que o limite permitido.");
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var ms = new MemoryStream();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            if (ms.Length + read > BlenderOpenDataParser.MaxBytes)
            {
                throw new InvalidDataException("Resposta maior que o limite permitido.");
            }

            ms.Write(buffer, 0, read);
        }

        return ms.ToArray();
    }

    private static string Sha(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

    public void Dispose()
    {
        _http.Dispose();
        _updateGate.Dispose();
    }
}

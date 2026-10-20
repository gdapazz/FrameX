using Framex.Core.Alerts;
using Framex.Core.Customization;
using Framex.Core.Persistence;
using Microsoft.Extensions.Logging;

namespace Framex.App.Services;

/// <summary>
/// Preferências, temas personalizados, layouts de overlay e regras de alerta, persistidos no banco local.
/// Todas as leituras devolvem cópias imutáveis/estáveis para uso seguro por threads de fundo.
/// </summary>
public sealed class SettingsService : IAlertRuleSource
{
    private const string AppKey = "app";
    private const string ThemesKey = "themes.custom";
    private const string OverlaysKey = "overlay.layouts";
    private const string RulesKey = "alerts.rules";

    private readonly ISettingsStore _store;
    private readonly ILogger<SettingsService> _logger;
    private volatile IReadOnlyList<AlertRule> _rules = [];

    public SettingsService(ISettingsStore store, ILogger<SettingsService> logger)
    {
        _store = store;
        _logger = logger;
    }

    public AppSettings Current { get; private set; } = new();

    public List<ThemeDefinition> CustomThemes { get; private set; } = [];

    public List<OverlayLayout> OverlayLayouts { get; private set; } = [];

    public IReadOnlyList<AlertRule> Rules => _rules;

    /// <summary>Disparado (na thread que salvou) após qualquer alteração persistida.</summary>
    public event EventHandler? Changed;

    public void Load()
    {
        Current = TryLoad<AppSettings>(AppKey) ?? new AppSettings();
        Current.Normalize();

        CustomThemes = (TryLoad<List<ThemeDefinition>>(ThemesKey) ?? [])
            .Where(t => t.Validate().Count == 0 && BuiltInThemes.All.All(b => b.Id != t.Id))
            .ToList();
        foreach (var t in CustomThemes)
        {
            t.BuiltIn = false;
        }

        var layouts = TryLoad<List<OverlayLayout>>(OverlaysKey) ?? [];
        layouts = layouts.Where(l => l.Validate().Count == 0).ToList();
        foreach (var preset in OverlayPresets.Defaults().Where(p => layouts.All(l => l.Id != p.Id)))
        {
            layouts.Add(preset);
        }

        OverlayLayouts = layouts;
        if (OverlayLayouts.All(l => l.Id != Current.ActiveOverlayLayoutId))
        {
            Current.ActiveOverlayLayoutId = OverlayLayouts[0].Id;
        }

        _rules = (TryLoad<List<AlertRule>>(RulesKey) ?? DefaultAlertRules.Create()).ToList();
    }

    private T? TryLoad<T>(string key) where T : class
    {
        try
        {
            return _store.Load<T>(key);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Configuração '{Key}' ilegível; usando valores padrão", key);
            return null;
        }
    }

    public void Save()
    {
        Current.Normalize();
        Persist(AppKey, Current);
    }

    public void SaveThemes() => Persist(ThemesKey, CustomThemes);

    public void SaveOverlayLayouts() => Persist(OverlaysKey, OverlayLayouts);

    public void SaveRules(IEnumerable<AlertRule> rules)
    {
        var list = rules.ToList();
        _rules = list;
        Persist(RulesKey, list);
    }

    private void Persist<T>(string key, T value) where T : class
    {
        try
        {
            _store.Save(key, value);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao salvar a configuração '{Key}'", key);
            throw;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public IEnumerable<ThemeDefinition> AllThemes => BuiltInThemes.All.Concat(CustomThemes);

    public ThemeDefinition ActiveTheme =>
        CustomThemes.FirstOrDefault(t => t.Id == Current.ThemeId) ?? BuiltInThemes.Get(Current.ThemeId);

    public OverlayLayout ActiveOverlay =>
        OverlayLayouts.FirstOrDefault(l => l.Id == Current.ActiveOverlayLayoutId) ?? OverlayLayouts[0];

    public PerformanceTuning Tuning => PerformanceTuning.For(Current.Profile);
}

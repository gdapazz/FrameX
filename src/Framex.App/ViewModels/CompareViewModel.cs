using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Framex.App.Services;
using Framex.Core.Persistence;
using Framex.Core.Reporting;
using Microsoft.Extensions.Logging;

namespace Framex.App.ViewModels;

/// <summary>Linha de comparação. Tom só é aplicado a métricas de quadros (FPS/ms), onde "melhor" é inequívoco.</summary>
public sealed record CompareRow(string Label, string A, string B, string Delta, string Tone);

public sealed partial class CompareViewModel : ObservableObject
{
    private readonly ISessionRepository _sessions;
    private readonly Navigator _navigator;
    private readonly ILogger<CompareViewModel> _logger;

    public CompareViewModel(ISessionRepository sessions, Navigator navigator, ILogger<CompareViewModel> logger)
    {
        _sessions = sessions;
        _navigator = navigator;
        _logger = logger;
    }

    [ObservableProperty]
    private bool _isLoading = true;

    [ObservableProperty]
    private string? _error;

    [ObservableProperty]
    private string _titleA = "";

    [ObservableProperty]
    private string _titleB = "";

    [ObservableProperty]
    private string _subtitleA = "";

    [ObservableProperty]
    private string _subtitleB = "";

    [ObservableProperty]
    private bool _conditionsDiffer;

    private Guid _a;
    private Guid _b;

    public ObservableCollection<CompareRow> Rows { get; } = [];

    public ObservableCollection<string> Warnings { get; } = [];

    public void Load(Guid a, Guid b) => _ = LoadAsync(a, b);

    private async Task LoadAsync(Guid a, Guid b)
    {
        _a = a;
        _b = b;
        IsLoading = true;
        try
        {
            var (sa, sb) = await Task.Run(() => (_sessions.Get(a), _sessions.Get(b)));
            if (sa is null || sb is null)
            {
                Error = "Uma das sessões não foi encontrada.";
                return;
            }

            // A sessão mais antiga é sempre "A" para que a variação leia como "antes → depois".
            if (sb.StartedAt < sa.StartedAt)
            {
                (sa, sb) = (sb, sa);
                (_a, _b) = (_b, _a);
            }

            var cmp = SessionComparer.Compare(sa, sb);
            TitleA = sa.GameName;
            TitleB = sb.GameName;
            SubtitleA = $"{Fmt.LocalTime(sa.StartedAt)} · {Fmt.Duration(sa.Duration)}";
            SubtitleB = $"{Fmt.LocalTime(sb.StartedAt)} · {Fmt.Duration(sb.Duration)}";
            ConditionsDiffer = cmp.ConditionsDiffer;
            Warnings.Clear();
            foreach (var w in cmp.Warnings)
            {
                Warnings.Add(w);
            }

            Rows.Clear();
            foreach (var r in cmp.Rows)
            {
                var format = r.Unit is "FPS" or "ms" or "%" or "°C" ? "0.0" : "0";
                var delta = r.Delta is { } d
                    ? $"{(d >= 0 ? "+" : "")}{d.ToString(format)} {r.Unit}" + (r.DeltaPercent is { } p ? $" ({(p >= 0 ? "+" : "")}{p:0.#}%)" : "")
                    : Fmt.Missing;
                var tone = "Muted";
                if (r.Delta is { } dd && r.Unit is "FPS" or "ms" && Math.Abs(r.DeltaPercent ?? 0) >= 1)
                {
                    tone = (dd > 0) == r.HigherIsBetter ? "Good" : "Bad";
                }

                Rows.Add(new CompareRow(r.Label, Value(r.A, r.Unit, format), Value(r.B, r.Unit, format), delta, tone));
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Falha ao comparar sessões");
            Error = "Não foi possível comparar: " + ex.Message;
        }
        finally
        {
            IsLoading = false;
        }
    }

    private static string Value(double? v, string unit, string format) => v is null ? Fmt.Missing : $"{v.Value.ToString(format)} {unit}";

    [RelayCommand]
    private void Back() => _navigator.Go(PageKey.Reports);

    [RelayCommand]
    private void OpenA() => _navigator.OpenReport(_a);

    [RelayCommand]
    private void OpenB() => _navigator.OpenReport(_b);
}

using Framex.Core.Domain;
using Framex.Core.Monitoring;

namespace Framex.Core.FrameCapture;

public readonly record struct LiveFrameMetrics(
    double? Fps,
    double? FrameTimeMs,
    double? Low1PercentFps,
    double? MaxFrameTimeMs,
    int WindowFrames,
    DateTimeOffset? LastFrameAt);

/// <summary>
/// Mantém uma janela deslizante de quadros recentes (memória fixa) para exibição ao vivo.
/// FPS atual = quadros na última janela de 1 s / soma dos frametimes; 1% low ao vivo usa os últimos ~30 s.
/// </summary>
public sealed class LiveFrameAggregator
{
    private readonly RingBuffer<FrameSample> _frames;
    private readonly Lock _gate = new();
    private readonly TimeProvider _time;
    private readonly List<double> _scratch = new(4096);

    public LiveFrameAggregator(TimeProvider time, int capacity = 30_000)
    {
        _time = time;
        _frames = new RingBuffer<FrameSample>(capacity);
    }

    public TimeSpan LowWindow { get; set; } = TimeSpan.FromSeconds(30);

    /// <summary>Se nenhum quadro chegar nesse intervalo, o FPS ao vivo é considerado ausente.</summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromSeconds(2);

    public void Add(IReadOnlyList<FrameSample> batch)
    {
        lock (_gate)
        {
            foreach (var f in batch)
            {
                if (FrameStatistics.IsValid(f.FrameTimeMs))
                {
                    _frames.Add(f);
                }
            }
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _frames.Clear();
        }
    }

    public LiveFrameMetrics Compute()
    {
        lock (_gate)
        {
            if (_frames.Count == 0)
            {
                return new LiveFrameMetrics(null, null, null, null, 0, null);
            }

            var last = _frames[_frames.Count - 1];
            var now = _time.GetUtcNow();
            if (now - last.Timestamp > IdleTimeout)
            {
                return new LiveFrameMetrics(null, null, null, null, 0, last.Timestamp);
            }

            double acc = 0;
            var n = 0;
            double maxFt = 0;
            for (var i = _frames.Count - 1; i >= 0 && acc < 1000; i--)
            {
                var ft = _frames[i].FrameTimeMs;
                acc += ft;
                n++;
                maxFt = Math.Max(maxFt, ft);
            }

            double? fps = acc > 0 ? 1000.0 * n / acc : null;

            _scratch.Clear();
            var cutoff = last.Timestamp - LowWindow;
            for (var i = _frames.Count - 1; i >= 0; i--)
            {
                var f = _frames[i];
                if (f.Timestamp < cutoff)
                {
                    break;
                }

                _scratch.Add(f.FrameTimeMs);
            }

            _scratch.Sort();
            var low = FrameStatistics.LowFps(_scratch, 0.01, FrameStatistics.MinFramesFor1Low);
            return new LiveFrameMetrics(fps, last.FrameTimeMs, low, maxFt, n, last.Timestamp);
        }
    }

    /// <summary>Copia os frametimes mais recentes (ordem cronológica) para <paramref name="output"/>; vazio se a captura estiver ociosa.</summary>
    public void CopyRecentFrameTimes(List<double> output, int max)
    {
        output.Clear();
        lock (_gate)
        {
            if (_frames.Count == 0 || _time.GetUtcNow() - _frames[_frames.Count - 1].Timestamp > IdleTimeout)
            {
                return;
            }

            var start = Math.Max(0, _frames.Count - max);
            for (var i = start; i < _frames.Count; i++)
            {
                output.Add(_frames[i].FrameTimeMs);
            }
        }
    }
}

/// <summary>Agrupa quadros em janelas de 1 s alinhadas ao relógio para persistência das séries temporais.</summary>
public sealed class SecondBucketAggregator
{
    private long _currentSecond = long.MinValue;
    private int _count;
    private double _sum;
    private double _max;

    /// <summary>Adiciona um quadro; quando um segundo termina, suas métricas são acrescentadas a <paramref name="output"/>.</summary>
    public void Add(FrameSample frame, List<MetricSample> output)
    {
        if (!FrameStatistics.IsValid(frame.FrameTimeMs))
        {
            return;
        }

        var second = frame.Timestamp.ToUnixTimeSeconds();
        if (second != _currentSecond)
        {
            if (_count > 0)
            {
                Emit(output);
            }

            _currentSecond = second;
        }

        _count++;
        _sum += frame.FrameTimeMs;
        _max = Math.Max(_max, frame.FrameTimeMs);
    }

    public void Flush(List<MetricSample> output)
    {
        if (_count > 0)
        {
            Emit(output);
        }
    }

    private void Emit(List<MetricSample> output)
    {
        var ts = DateTimeOffset.FromUnixTimeSeconds(_currentSecond);
        var avg = _sum / _count;
        output.Add(new MetricSample(ts, MetricKind.Fps, 1000.0 / avg));
        output.Add(new MetricSample(ts, MetricKind.FrameTimeAvg, avg));
        output.Add(new MetricSample(ts, MetricKind.FrameTimeMax, _max));
        output.Add(new MetricSample(ts, MetricKind.FrameCount, _count));
        _count = 0;
        _sum = 0;
        _max = 0;
    }
}

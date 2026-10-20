using System.IO.Pipes;
using System.Text;
using Framex.Core.Domain;
using Framex.Core.FrameCapture;
using Framex.Core.Monitoring;
using Framex.Infrastructure.Service;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Framex.Tests;

/// <summary>Ida e volta real pelo named pipe entre o app e o servidor do serviço (pipe de teste exclusivo por caso).</summary>
public sealed class ServiceBridgeTests
{
    private sealed class FakeSensors : ISensorProvider
    {
        public int Reads;
        public bool Disposed;
        public string Name => "fake";
        public int Priority => 0;
        public void Initialize(MonitoringOptions options) { }

        public ProviderReadResult Read(DateTimeOffset now)
        {
            Interlocked.Increment(ref Reads);
            return new ProviderReadResult(
                [new SensorReading("/intelcpu/0/temperature/0", "/intelcpu/0", "CPU", ComponentKind.Cpu, "Temperature", "CPU Package", 47.5, MetricUnit.Celsius, now, SensorAvailability.Available, "LHM")],
                new Dictionary<MetricKind, MetricValue>
                {
                    [MetricKind.CpuTemperaturePackage] = MetricValue.Of(47.5, "/intelcpu/0/temperature/0"),
                    [MetricKind.CpuPower] = MetricValue.Missing(SensorAvailability.Unavailable, "sem driver"),
                },
                [10, 20]);
        }

        public HardwareInventory? DescribeHardware() =>
            new(DateTimeOffset.UtcNow, [new HardwareComponentInfo("/intelcpu/0", ComponentKind.Cpu, "CPU de teste", [new HardwareProperty("Sensores de temperatura", "Disponíveis")])]);

        public void Dispose() => Disposed = true;
    }

    private static (ServicePipeServer Server, FakeSensors Sensors, FakeCapture Capture, string Pipe) StartServer(bool sameSession = true)
    {
        var pipe = "FRAMEX.Test." + Guid.NewGuid().ToString("N");
        var sensors = new FakeSensors();
        var capture = new FakeCapture();
        var server = new ServicePipeServer(() => sensors, capture, () => true, TimeProvider.System, NullLogger<ServicePipeServer>.Instance, pipe, sameSession);
        server.Start();
        return (server, sensors, capture, pipe);
    }

    [Fact]
    public async Task Sensors_RoundTrip_ThroughPipe()
    {
        var (server, _, _, pipe) = StartServer();
        await using var _ = server;
        using var client = new ServiceSensorProvider(NullLogger<ServiceSensorProvider>.Instance, pipe);

        var result = client.Read(DateTimeOffset.UtcNow);

        Assert.True(client.IsConnected);
        Assert.True(client.DriverInstalled);
        var temp = Assert.Single(result.Sensors);
        Assert.Equal(47.5, temp.Value);
        Assert.Equal(ServiceSensorProvider.SourceName, temp.Source);
        Assert.Equal(47.5, result.Metrics[MetricKind.CpuTemperaturePackage].Value);
        Assert.False(result.Metrics[MetricKind.CpuPower].HasValue);
        Assert.Equal("sem driver", result.Metrics[MetricKind.CpuPower].Reason);
        Assert.Equal([10d, 20d], result.PerCoreLoad);

        var inventory = client.DescribeHardware();
        Assert.Equal("CPU de teste", Assert.Single(inventory!.Components).Name);
    }

    [Fact]
    public async Task Sensors_AreCachedBriefly_SoManyClientsDoNotMultiplyReads()
    {
        var (server, sensors, _, pipe) = StartServer();
        await using var _ = server;
        using var a = new ServiceSensorProvider(NullLogger<ServiceSensorProvider>.Instance, pipe);
        using var b = new ServiceSensorProvider(NullLogger<ServiceSensorProvider>.Instance, pipe);

        a.Read(DateTimeOffset.UtcNow);
        b.Read(DateTimeOffset.UtcNow);

        Assert.Equal(1, sensors.Reads);
    }

    [Fact]
    public void NoService_ContributesNothing_WithoutThrowing()
    {
        using var client = new ServiceSensorProvider(NullLogger<ServiceSensorProvider>.Instance, "FRAMEX.Test.inexistente." + Guid.NewGuid().ToString("N"));

        var result = client.Read(DateTimeOffset.UtcNow);

        Assert.Empty(result.Sensors);
        Assert.Empty(result.Metrics);
        Assert.False(client.IsConnected);
        Assert.Null(client.DescribeHardware());
    }

    [Fact]
    public async Task UnknownRequest_IsRejected()
    {
        var (server, _, _, pipe) = StartServer();
        await using var _ = server;
        await using var raw = await ServiceProtocol.TryConnectAsync(TimeSpan.FromSeconds(2), CancellationToken.None, pipe);
        Assert.NotNull(raw);

        await ServiceProtocol.WriteAsync(raw!, new ServiceRequest("run", ExecutableName: @"C:\Windows\System32\cmd.exe"), CancellationToken.None);
        var reply = await new ServiceProtocol.LineReader(raw!).ReadLineAsync(CancellationToken.None);

        Assert.Contains("\"type\":\"error\"", reply, StringComparison.Ordinal);
    }

    [Fact]
    public async Task OversizedMessage_ClosesOnlyThatConnection()
    {
        var (server, _, _, pipe) = StartServer();
        await using var _ = server;
        await using (var raw = await ServiceProtocol.TryConnectAsync(TimeSpan.FromSeconds(2), CancellationToken.None, pipe))
        {
            var junk = Encoding.UTF8.GetBytes(new string('x', ServiceProtocol.MaxMessageBytes + 1024));
            try
            {
                await raw!.WriteAsync(junk);
                await raw.FlushAsync();
            }
            catch (IOException)
            {
                // O servidor pode fechar a conexão antes de receber tudo.
            }

            var end = await new ServiceProtocol.LineReader(raw!).ReadLineAsync(new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token);
            Assert.Null(end);
        }

        using var client = new ServiceSensorProvider(NullLogger<ServiceSensorProvider>.Instance, pipe);
        Assert.Single(client.Read(DateTimeOffset.UtcNow).Sensors);
    }

    [Fact]
    public async Task Capture_StreamsFrames_AndStopsWhenClientDisconnects()
    {
        var (server, _, capture, pipe) = StartServer();
        await using var _ = server;
        await using var client = new ServiceFrameCaptureProvider(NullLogger<ServiceFrameCaptureProvider>.Instance, pipe);
        var received = new List<FrameSample>();
        var got = new TaskCompletionSource();
        client.FramesReceived += (_, frames) => { lock (received) { received.AddRange(frames); } got.TrySetResult(); };

        await client.StartAsync(Environment.ProcessId, "jogo.exe", new FrameCaptureOptions(), CancellationToken.None);
        await WaitUntil(() => capture.StartCount == 1);
        Assert.Equal(Environment.ProcessId, capture.LastPid);
        await WaitUntil(() => client.Status.State == CaptureState.Capturing);

        var at = new DateTimeOffset(2026, 10, 20, 12, 0, 0, 125, TimeSpan.Zero);
        capture.Emit([new FrameSample(at, 16.7), new FrameSample(at.AddMilliseconds(16.7), 8.3, Displayed: false)]);
        await got.Task.WaitAsync(TimeSpan.FromSeconds(5));

        lock (received)
        {
            Assert.Equal(2, received.Count);
            Assert.Equal(at, received[0].Timestamp);
            Assert.Equal(16.7, received[0].FrameTimeMs, 6);
            Assert.False(received[1].Displayed);
        }

        await client.StopAsync();
        await WaitUntil(() => capture.Status.State == CaptureState.Stopped);
    }

    [Fact]
    public async Task Capture_InvalidPid_IsRefused_WithoutStarting()
    {
        var (server, _, capture, pipe) = StartServer();
        await using var _ = server;
        await using var client = new ServiceFrameCaptureProvider(NullLogger<ServiceFrameCaptureProvider>.Instance, pipe);

        await client.StartAsync(0, "x.exe", new FrameCaptureOptions(), CancellationToken.None);
        await WaitUntil(() => client.Status.State == CaptureState.Error);

        Assert.Equal(0, capture.StartCount);
        Assert.Contains("PID", client.Status.Detail, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Capture_NewRequest_ReplacesPrevious()
    {
        var (server, _, capture, pipe) = StartServer();
        await using var _ = server;
        await using var first = new ServiceFrameCaptureProvider(NullLogger<ServiceFrameCaptureProvider>.Instance, pipe);
        await using var second = new ServiceFrameCaptureProvider(NullLogger<ServiceFrameCaptureProvider>.Instance, pipe);

        await first.StartAsync(Environment.ProcessId, "a.exe", new FrameCaptureOptions(), CancellationToken.None);
        await WaitUntil(() => capture.StartCount == 1);
        await second.StartAsync(Environment.ProcessId, "b.exe", new FrameCaptureOptions(), CancellationToken.None);

        await WaitUntil(() => capture.StartCount == 2);
        await WaitUntil(() => first.Status.State == CaptureState.Error);
    }

    [Fact]
    public void MergeSensors_KeepsOneEntryPerId_PreferringValidReading()
    {
        var now = DateTimeOffset.UtcNow;
        SensorReading R(string source, double? v) => new("/intelcpu/0/temperature/0", "/intelcpu/0", "CPU", ComponentKind.Cpu, "Temperature", "CPU Package", v,
            MetricUnit.Celsius, now, v is null ? SensorAvailability.Unavailable : SensorAvailability.Available, source);
        var list = new List<SensorReading>();
        var index = new Dictionary<string, int>(StringComparer.Ordinal);

        HardwareMonitoringService.MergeSensors(list, index, [R("serviço", 50)]);
        HardwareMonitoringService.MergeSensors(list, index, [R("local", null)]);
        Assert.Equal("serviço", Assert.Single(list).Source);

        list.Clear();
        index.Clear();
        HardwareMonitoringService.MergeSensors(list, index, [R("serviço", null)]);
        HardwareMonitoringService.MergeSensors(list, index, [R("local", 41)]);
        Assert.Equal(41, Assert.Single(list).Value);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline)
            {
                throw new TimeoutException("Condição não atingida a tempo.");
            }

            await Task.Delay(20);
        }
    }
}

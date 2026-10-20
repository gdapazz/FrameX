using System.Diagnostics;
using System.Text;
using Framex.Core.Domain;
using Framex.Core.GameDetection;
using Framex.Infrastructure.Native;
using Microsoft.Extensions.Logging;

namespace Framex.Infrastructure.Processes;

/// <summary>
/// Instantâneo de processos sem privilégios: nomes via Process.GetProcesses, caminhos via
/// QueryFullProcessImageName (PROCESS_QUERY_LIMITED_INFORMATION, com cache por PID) e janelas visíveis via EnumWindows.
/// </summary>
public sealed class Win32ProcessSnapshotProvider : IProcessSnapshotProvider
{
    private const int MinWindowSize = 160;
    private readonly ILogger<Win32ProcessSnapshotProvider> _logger;
    private readonly Dictionary<int, (string Name, string? Path)> _pathCache = new();
    private readonly int _ownPid = Environment.ProcessId;

    public Win32ProcessSnapshotProvider(ILogger<Win32ProcessSnapshotProvider> logger) => _logger = logger;

    public IReadOnlyList<ProcessInfo> GetProcesses()
    {
        var windows = VisibleTopLevelWindows();
        var result = new List<ProcessInfo>(384);
        var alive = new HashSet<int>();
        var processes = Process.GetProcesses();
        try
        {
            foreach (var p in processes)
            {
                int pid;
                string exe;
                long ws;
                try
                {
                    pid = p.Id;
                    if (pid <= 4 || pid == _ownPid)
                    {
                        continue;
                    }

                    exe = p.ProcessName + ".exe";
                    ws = p.WorkingSet64;
                }
                catch (InvalidOperationException)
                {
                    continue; // Processo encerrou durante a enumeração.
                }

                alive.Add(pid);
                if (!_pathCache.TryGetValue(pid, out var cached) || !string.Equals(cached.Name, exe, StringComparison.OrdinalIgnoreCase))
                {
                    cached = (exe, QueryPath(pid));
                    _pathCache[pid] = cached;
                }

                windows.TryGetValue(pid, out var title);
                result.Add(new ProcessInfo(pid, exe, cached.Path, title is not null, title, null, ws));
            }
        }
        finally
        {
            foreach (var p in processes)
            {
                p.Dispose();
            }
        }

        if (_pathCache.Count > alive.Count + 64)
        {
            foreach (var dead in _pathCache.Keys.Where(k => !alive.Contains(k)).ToList())
            {
                _pathCache.Remove(dead);
            }
        }

        return result;
    }

    private string? QueryPath(int pid)
    {
        var handle = NativeMethods.OpenProcess(NativeMethods.ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero)
        {
            return null;
        }

        try
        {
            var sb = new StringBuilder(1024);
            var size = sb.Capacity;
            return NativeMethods.QueryFullProcessImageName(handle, 0, sb, ref size) ? sb.ToString(0, size) : null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Falha ao consultar caminho do PID {Pid}", pid);
            return null;
        }
        finally
        {
            NativeMethods.CloseHandle(handle);
        }
    }

    /// <summary>PID → título da maior janela visível, sem dono e que não seja tool window.</summary>
    private static Dictionary<int, string> VisibleTopLevelWindows()
    {
        var map = new Dictionary<int, (string Title, long Area)>();
        NativeMethods.EnumWindows((hwnd, _) =>
        {
            if (!NativeMethods.IsWindowVisible(hwnd) || NativeMethods.GetWindow(hwnd, NativeMethods.GwOwner) != IntPtr.Zero)
            {
                return true;
            }

            var ex = NativeMethods.GetWindowLongPtr(hwnd, NativeMethods.GwlExStyle).ToInt64();
            if ((ex & NativeMethods.WsExToolWindow) != 0 || !NativeMethods.GetWindowRect(hwnd, out var r))
            {
                return true;
            }

            var w = r.Right - r.Left;
            var h = r.Bottom - r.Top;
            if (w < MinWindowSize || h < MinWindowSize)
            {
                return true;
            }

            NativeMethods.GetWindowThreadProcessId(hwnd, out var pid);
            var len = NativeMethods.GetWindowTextLength(hwnd);
            var title = "";
            if (len > 0)
            {
                var sb = new StringBuilder(len + 1);
                NativeMethods.GetWindowText(hwnd, sb, sb.Capacity);
                title = sb.ToString();
            }

            long area = (long)w * h;
            if (!map.TryGetValue((int)pid, out var existing) || existing.Area < area)
            {
                map[(int)pid] = (title, area);
            }

            return true;
        }, IntPtr.Zero);

        return map.ToDictionary(kv => kv.Key, kv => kv.Value.Title);
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Framex.Infrastructure.Native;

/// <summary>
/// Job Object com KILL_ON_JOB_CLOSE: se o FRAMEX encerrar (inclusive por falha), o Windows finaliza
/// os processos auxiliares associados, evitando processos órfãos.
/// </summary>
internal sealed class ChildProcessJob : IDisposable
{
    private IntPtr _handle;

    public ChildProcessJob()
    {
        _handle = NativeMethods.CreateJobObject(IntPtr.Zero, null);
        if (_handle == IntPtr.Zero)
        {
            return;
        }

        var info = new NativeMethods.JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new NativeMethods.JobObjectBasicLimitInformation { LimitFlags = NativeMethods.JobObjectLimitKillOnJobClose },
        };
        if (!NativeMethods.SetInformationJobObject(_handle, NativeMethods.JobObjectExtendedLimitInformationClass, ref info,
                (uint)Marshal.SizeOf<NativeMethods.JobObjectExtendedLimitInformation>()))
        {
            NativeMethods.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }

    public bool IsValid => _handle != IntPtr.Zero;

    public bool Assign(Process process) => IsValid && NativeMethods.AssignProcessToJobObject(_handle, process.Handle);

    public void Dispose()
    {
        if (_handle != IntPtr.Zero)
        {
            NativeMethods.CloseHandle(_handle);
            _handle = IntPtr.Zero;
        }
    }
}

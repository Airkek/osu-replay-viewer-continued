using System;
using System.Threading;
using osu_replay_renderer_netcore.CustomHosts.Record;

namespace osu_replay_renderer_netcore.Record;

internal sealed class MappedFrameLease : IFrameLease
{
    private readonly IntPtr pointer;
    private readonly int length;
    private readonly object releaseLock = new();
    private int released;

    public MappedFrameLease(IntPtr pointer, int length)
    {
        if (pointer == IntPtr.Zero || length <= 0)
            throw new ArgumentException("A frame lease requires mapped memory");
        this.pointer = pointer;
        this.length = length;
    }

    public unsafe ReadOnlySpan<byte> Data
    {
        get
        {
            ObjectDisposedException.ThrowIf(Volatile.Read(ref released) != 0, this);
            return new ReadOnlySpan<byte>(pointer.ToPointer(), length);
        }
    }

    // The consumer only signals release, while the GL thread owns unmapping
    public void Dispose()
    {
        lock (releaseLock)
        {
            Volatile.Write(ref released, 1);
            Monitor.PulseAll(releaseLock);
        }
    }

    public void WaitForRelease()
    {
        lock (releaseLock)
            while (released == 0) Monitor.Wait(releaseLock);
    }
}

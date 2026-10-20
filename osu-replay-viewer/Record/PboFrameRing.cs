using System;
using System.Runtime.ExceptionServices;
using osu_replay_renderer_netcore.CustomHosts.Record;

namespace osu_replay_renderer_netcore.Record;

internal sealed class PboFrameRing
{
    public const int SlotCount = 3;
    private readonly MappedFrameLease[] leases = new MappedFrameLease[SlotCount];
    private readonly Func<int, IntPtr> map;
    private readonly Action<int> unmap;
    private readonly int frameSize;
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    private int nextSlot;
    private int pendingSlot = -1;
    private bool acquired;

    public PboFrameRing(int frameSize, Func<int, IntPtr> map, Action<int> unmap)
    {
        this.frameSize = frameSize;
        this.map = map;
        this.unmap = unmap;
    }

    public int AcquireReadSlot()
    {
        CheckThread();
        if (acquired) throw new InvalidOperationException("A readback is already in progress");
        ReleaseSlot(nextSlot);
        acquired = true;
        return nextSlot;
    }

    public void CaptureCompleted(EncoderBase encoder)
    {
        CheckThread();
        if (!acquired) throw new InvalidOperationException("No readback was acquired");
        int previous = pendingSlot;
        pendingSlot = nextSlot;
        nextSlot = (nextSlot + 1) % SlotCount;
        acquired = false;
        if (previous >= 0) Submit(previous, encoder);
    }

    public void Finish(EncoderBase encoder)
    {
        CheckThread();
        try
        {
            if (pendingSlot >= 0)
            {
                int slot = pendingSlot;
                pendingSlot = -1;
                Submit(slot, encoder);
            }
        }
        finally
        {
            ReleaseMappings();
        }
    }

    public void ReleaseMappings()
    {
        CheckThread();
        Exception failure = null;
        for (int slot = 0; slot < leases.Length; slot++)
        {
            try { ReleaseSlot(slot); }
            catch (Exception exception) { failure ??= exception; }
        }
        if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private void Submit(int slot, EncoderBase encoder)
    {
        var pointer = map(slot);
        if (pointer == IntPtr.Zero)
            throw new InvalidOperationException("OpenGL could not map the frame readback buffer");
        var lease = new MappedFrameLease(pointer, frameSize);
        leases[slot] = lease;
        try
        {
            if (encoder is IOwnedFrameEncoder owned)
                owned.WriteFrame(lease);
            else
            {
                try { encoder.WriteFrame(lease.Data); }
                finally { lease.Dispose(); }
            }
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    private void ReleaseSlot(int slot)
    {
        var lease = leases[slot];
        if (lease == null) return;
        lease.WaitForRelease();
        leases[slot] = null;
        unmap(slot);
    }

    private void CheckThread()
    {
        if (Environment.CurrentManagedThreadId != ownerThread)
            throw new InvalidOperationException("PBO mapping must run on its owning GL thread");
    }
}

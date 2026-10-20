using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using osu_replay_renderer_netcore.CustomHosts.Record;
using osu_replay_renderer_netcore.Record;

int checks = 0;
Check("empty finish does not map a frame", () =>
{
    using var buffers = new FakePbos();
    var encoder = new SynchronousEncoder();
    buffers.Ring.Finish(encoder);
    Assert(buffers.MapCount == 0 && buffers.UnmapCount == 0, "empty mapping");
});
Check("one frame is flushed exactly once", () =>
{
    using var buffers = new FakePbos();
    var encoder = new SynchronousEncoder();
    buffers.Capture(encoder, 7);
    Assert(encoder.Frames.Count == 0, "current frame mapped before next readback");
    buffers.Ring.Finish(encoder);
    buffers.Ring.Finish(encoder);
    Assert(encoder.Frames.SequenceEqual(new[] { 7 }), "pending frame duplicated or dropped");
    Assert(buffers.MapCount == 1 && buffers.UnmapCount == 1, "final mapping release");
});
Check("owned frames keep order through bounded ring reuse", () =>
{
    using var buffers = new FakePbos();
    using var encoder = new AsynchronousEncoder();
    for (int frame = 0; frame < 40; frame++) buffers.Capture(encoder, frame);
    buffers.Ring.Finish(encoder);
    encoder.Finish();
    Assert(encoder.Frames.SequenceEqual(Enumerable.Range(0, 40)), "owned frame order/drop");
    Assert(buffers.MapCount == 40 && buffers.UnmapCount == 40, "leased mappings leaked");
    Assert(buffers.MaximumMapped <= PboFrameRing.SlotCount, "ring grew");
});
Check("consumer dispose signals without unmapping", () =>
{
    using var buffers = new FakePbos();
    var encoder = new HoldingEncoder();
    buffers.Capture(encoder, 1);
    buffers.Capture(encoder, 2);
    var lease = encoder.Frames.Single();
    Assert(BinaryPrimitives.ReadInt32LittleEndian(lease.Data) == 1, "owned frame bytes");
    Task.Run(lease.Dispose).GetAwaiter().GetResult();
    Assert(buffers.UnmapCount == 0, "consumer called unmap");
    Throws<ObjectDisposedException>(() => { _ = lease.Data.Length; });
    encoder.ReleaseImmediately = true;
    buffers.Ring.Finish(encoder);
    Assert(buffers.UnmapCount == 2, "GL owner did not unmap released leases");
});
Check("busy slot waits for consumer before reuse", () =>
{
    using var buffers = new FakePbos();
    var encoder = new HoldingEncoder();
    buffers.Capture(encoder, 1);
    buffers.Capture(encoder, 2);
    buffers.Capture(encoder, 3);
    var oldest = encoder.Frames[0];
    var release = Task.Run(() =>
    {
        Thread.Sleep(20);
        Assert(BinaryPrimitives.ReadInt32LittleEndian(oldest.Data) == 1, "mapped data overwritten while consumer owns it");
        oldest.Dispose();
    });
    buffers.Capture(encoder, 4);
    release.GetAwaiter().GetResult();
    Assert(buffers.UnmapCount == 1, "slot was not reclaimed on GL thread");
    foreach (var lease in encoder.Frames) lease.Dispose();
    encoder.ReleaseImmediately = true;
    buffers.Ring.Finish(encoder);
});
Check("encoders without ownership retain synchronous fallback", () =>
{
    using var buffers = new FakePbos();
    var encoder = new SynchronousEncoder();
    for (int frame = 0; frame < 9; frame++) buffers.Capture(encoder, frame);
    buffers.Ring.Finish(encoder);
    Assert(encoder.Frames.SequenceEqual(Enumerable.Range(0, 9)), "fallback frame data");
    Assert(buffers.UnmapCount == 9, "fallback mapping release");
});
Check("enqueue rejection releases its mapped frame", () =>
{
    using var buffers = new FakePbos();
    var encoder = new RejectingEncoder();
    buffers.Capture(encoder, 1);
    Throws<IOException>(() => buffers.Capture(encoder, 2));
    buffers.Ring.ReleaseMappings();
    Assert(buffers.UnmapCount == 1, "rejected frame retained");
});
Check("mapping failure is reported without silently dropping a frame", () =>
{
    using var buffers = new FakePbos { FailMapping = true };
    var encoder = new SynchronousEncoder();
    buffers.Capture(encoder, 1);
    Throws<InvalidOperationException>(() => buffers.Capture(encoder, 2));
    buffers.Ring.ReleaseMappings();
    Assert(encoder.Frames.Count == 0, "invalid pointer submitted");
});
Check("worker error disposal lets GL owner reclaim all mappings", () =>
{
    using var buffers = new FakePbos();
    var encoder = new HoldingEncoder();
    buffers.Capture(encoder, 1);
    buffers.Capture(encoder, 2);
    buffers.Capture(encoder, 3);
    Task.Run(() => { foreach (var frame in encoder.Frames) frame.Dispose(); }).GetAwaiter().GetResult();
    buffers.Ring.ReleaseMappings();
    Assert(buffers.UnmapCount == 2, "failed worker retained mapped slots");
});
Check("map and unmap operations require the owning GL thread", () =>
{
    using var buffers = new FakePbos();
    var encoder = new SynchronousEncoder();
    buffers.Capture(encoder, 1);
    Throws<InvalidOperationException>(() => Task.Run(() => buffers.Ring.AcquireReadSlot()).GetAwaiter().GetResult());
    Throws<InvalidOperationException>(() => Task.Run(() => buffers.Ring.Finish(encoder)).GetAwaiter().GetResult());
    buffers.Ring.Finish(encoder);
});
Check("unmap failure still releases the other mapped slots", () =>
{
    using var buffers = new FakePbos();
    var encoder = new HoldingEncoder();
    buffers.Capture(encoder, 1);
    buffers.Capture(encoder, 2);
    buffers.Capture(encoder, 3);
    foreach (var lease in encoder.Frames) lease.Dispose();
    buffers.FailUnmapping = true;
    Throws<IOException>(buffers.Ring.ReleaseMappings);
    Assert(buffers.UnmapCount == 2, "later slots leaked after unmap failure");
});
Console.WriteLine($"Passed {checks} readback lifetime checks; GL/Veldrid wrappers compiled against real framework packages");

void Check(string name, Action test)
{
    test();
    checks++;
    Console.WriteLine($"PASS {name}");
}
static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}
static void Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T) { return; }
    throw new Exception($"Expected {typeof(T).Name}");
}
sealed class FakePbos : IDisposable
{
    private readonly IntPtr[] pointers = new IntPtr[PboFrameRing.SlotCount];
    private readonly bool[] mapped = new bool[PboFrameRing.SlotCount];
    private readonly int ownerThread = Environment.CurrentManagedThreadId;
    public readonly PboFrameRing Ring;
    public int MapCount;
    public int UnmapCount;
    public int MaximumMapped;
    public bool FailMapping;
    public bool FailUnmapping;

    public FakePbos()
    {
        for (int i = 0; i < pointers.Length; i++) pointers[i] = Marshal.AllocHGlobal(4);
        Ring = new PboFrameRing(4, Map, Unmap);
    }
    public void Capture(EncoderBase encoder, int value)
    {
        int slot = Ring.AcquireReadSlot();
        if (mapped[slot]) throw new Exception("Readback reused a mapped PBO");
        Marshal.WriteInt32(pointers[slot], value);
        Ring.CaptureCompleted(encoder);
    }
    private IntPtr Map(int slot)
    {
        if (Environment.CurrentManagedThreadId != ownerThread) throw new Exception("Map ran on consumer thread");
        if (FailMapping) return IntPtr.Zero;
        if (mapped[slot]) throw new Exception("PBO mapped twice");
        mapped[slot] = true;
        MapCount++;
        MaximumMapped = Math.Max(MaximumMapped, mapped.Count(value => value));
        return pointers[slot];
    }
    private void Unmap(int slot)
    {
        if (Environment.CurrentManagedThreadId != ownerThread) throw new Exception("Unmap ran on consumer thread");
        if (!mapped[slot]) throw new Exception("Unmapped PBO released twice");
        mapped[slot] = false;
        UnmapCount++;
        if (FailUnmapping) throw new IOException("Invalidated PBO data");
    }
    public void Dispose()
    {
        Ring.ReleaseMappings();
        foreach (var pointer in pointers) Marshal.FreeHGlobal(pointer);
    }
}
class SynchronousEncoder() : EncoderBase(new EncoderConfig())
{
    public readonly List<int> Frames = new();
    public override bool CanWrite => true;
    protected override void _writeFrameInternal(ReadOnlySpan<byte> frame) => Frames.Add(BinaryPrimitives.ReadInt32LittleEndian(frame));
    protected override void _finishInternal() { }
    protected override void _startInternal() { }
}
sealed class HoldingEncoder : SynchronousEncoder, IOwnedFrameEncoder
{
    public new readonly List<IFrameLease> Frames = new();
    public bool ReleaseImmediately;
    public void WriteFrame(IFrameLease frame)
    {
        Frames.Add(frame);
        if (ReleaseImmediately) frame.Dispose();
    }
}
sealed class RejectingEncoder : SynchronousEncoder, IOwnedFrameEncoder
{
    public void WriteFrame(IFrameLease frame) => throw new IOException("Worker failed before enqueue");
}
sealed class AsynchronousEncoder : SynchronousEncoder, IOwnedFrameEncoder, IDisposable
{
    private readonly BlockingCollection<IFrameLease> frames = new(3);
    private readonly Thread worker;
    public AsynchronousEncoder()
    {
        worker = new Thread(() =>
        {
            foreach (var frame in frames.GetConsumingEnumerable())
            {
                using (frame)
                {
                    Thread.Sleep(1);
                    Frames.Add(BinaryPrimitives.ReadInt32LittleEndian(frame.Data));
                }
            }
        });
        worker.Start();
    }
    public void WriteFrame(IFrameLease frame) => frames.Add(frame);
    protected override void _finishInternal()
    {
        frames.CompleteAdding();
        worker.Join();
    }
    public void Dispose()
    {
        if (!frames.IsAddingCompleted) Finish();
        frames.Dispose();
    }
}

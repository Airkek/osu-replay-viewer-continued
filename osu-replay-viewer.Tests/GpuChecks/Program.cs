using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using osu_replay_renderer_netcore.CustomHosts.Record;

internal static class Program
{
    private const int again = -11;
    private const int eof = -100;

    private static int Main()
    {
        Action[] tests =
        [
            UsesOneOwnerThreadAndDrainsInOrder,
            AppliesBackpressure,
            FailureUnblocksProducerAndReleasesPendingItems,
            FailureAllowsCleanupToAcquireProducerLock,
            StartupFailureAlwaysCleansUp,
            FinishFailurePreservesOriginalError,
            ReleaseFailureStillReleasesPendingItems,
            ConcurrentCompletionWaitsForCleanup,
            RetriesSameFrameAfterDrainingEagain,
            RejectsEagainWithoutOutput,
            FlushRetriesAndReachesEof,
            FlushRejectsUnexpectedEagain,
            FlushAcceptsAlreadyDrainedEof,
            SendFailurePreservesError
        ];
        foreach (var test in tests)
        {
            try { test(); Console.WriteLine($"PASS {test.Method.Name}"); }
            catch (Exception exception) { Console.Error.WriteLine($"FAIL {test.Method.Name}: {exception}"); return 1; }
        }
        Console.WriteLine($"{tests.Length} GPU worker/packet contract tests passed without CUDA hardware");
        return 0;
    }

    private static void UsesOneOwnerThreadAndDrainsInOrder()
    {
        var calls = new List<string>();
        var threads = new List<int>();
        void Record(string value) { calls.Add(value); threads.Add(Environment.CurrentManagedThreadId); }
        using var worker = new EncoderWorker<string>(3, () => Record("start"), item => Record(item),
            item => Record($"release:{item}"), () => Record("finish"), () => Record("cleanup"));
        worker.Start();
        worker.Submit("video:0");
        worker.Submit("audio:0");
        worker.Submit("video:1");
        worker.Complete();
        Assert(calls.SequenceEqual(new[] { "start", "video:0", "release:video:0", "audio:0", "release:audio:0", "video:1", "release:video:1", "finish", "cleanup" }), "Order or drain changed");
        Assert(threads.Distinct().Count() == 1 && threads[0] != Environment.CurrentManagedThreadId, "Native work did not stay on its owner thread");
    }

    private static void AppliesBackpressure()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var producerStarted = new ManualResetEventSlim();
        var items = new List<int>();
        using var worker = new EncoderWorker<int>(1, () => { }, item => { entered.Set(); Wait(release); items.Add(item); },
            _ => { }, () => { }, () => { });
        worker.Start();
        Task producer = null;
        try
        {
            worker.Submit(1);
            Wait(entered);
            worker.Submit(2);
            producer = Task.Run(() => { producerStarted.Set(); worker.Submit(3); });
            Wait(producerStarted);
            Assert(!producer.Wait(100), "A full worker queue did not block its producer");
        }
        finally { release.Set(); if (producer != null) Wait(producer); }
        worker.Complete();
        Assert(items.SequenceEqual(new[] { 1, 2, 3 }), "Backpressure lost or reordered items");
    }

    private static void FailureUnblocksProducerAndReleasesPendingItems()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var producerStarted = new ManualResetEventSlim();
        var failure = new Exception("encode failed");
        var released = new ConcurrentQueue<int>();
        int cleaned = 0;
        using var worker = new EncoderWorker<int>(1, () => { }, _ => { entered.Set(); Wait(release); throw failure; },
            item => released.Enqueue(item), () => throw new Exception("Finish must not run after encode failure"), () => cleaned++);
        worker.Start();
        worker.Submit(1);
        Wait(entered);
        worker.Submit(2);
        var producer = Task.Run(() => { producerStarted.Set(); return Catch(() => worker.Submit(3)); });
        try { Wait(producerStarted); Assert(!producer.Wait(100), "Expected a blocked producer"); }
        finally { release.Set(); }
        Wait(producer);
        Assert(ReferenceEquals(producer.Result, failure), "Producer did not receive the encoding error");
        Assert(ReferenceEquals(Catch(worker.Complete), failure), "Finish did not receive the encoding error");
        Assert(released.SequenceEqual(new[] { 1, 2 }) && cleaned == 1, "Owned items or resources leaked after failure");
    }

    private static void FailureAllowsCleanupToAcquireProducerLock()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var producerStarted = new ManualResetEventSlim();
        object producerLock = new();
        var failure = new Exception("encode failed");
        bool cleaned = false;
        using var worker = new EncoderWorker<int>(1, () => { }, _ => { entered.Set(); Wait(release); throw failure; },
            _ => { }, () => { }, () => { lock (producerLock) cleaned = true; });
        worker.Start();
        worker.Submit(1);
        Wait(entered);
        worker.Submit(2);
        var producer = Task.Run(() =>
        {
            lock (producerLock) { producerStarted.Set(); return Catch(() => worker.Submit(3)); }
        });
        try { Wait(producerStarted); Assert(!producer.Wait(100), "Expected a blocked producer holding its lock"); }
        finally { release.Set(); }
        Wait(producer);
        Assert(ReferenceEquals(Catch(worker.Complete), failure) && cleaned, "Cleanup deadlocked on the producer lock");
    }

    private static void StartupFailureAlwaysCleansUp()
    {
        var failure = new Exception("CUDA initialization failed");
        int cleaned = 0;
        using var worker = new EncoderWorker<int>(1, () => throw failure, _ => { }, _ => { }, () => throw new Exception("Unexpected finish"),
            () => { cleaned++; throw new Exception("cleanup also failed"); });
        Assert(ReferenceEquals(Catch(worker.Start), failure), "Startup error was replaced");
        Assert(ReferenceEquals(Catch(worker.Complete), failure) && cleaned == 1, "Partial initialization was not cleaned up once");
    }

    private static void FinishFailurePreservesOriginalError()
    {
        var failure = new Exception("trailer failed");
        int cleaned = 0;
        using var worker = new EncoderWorker<int>(1, () => { }, _ => { }, _ => { }, () => throw failure,
            () => { cleaned++; throw new Exception("cleanup also failed"); });
        worker.Start();
        worker.Submit(1);
        Assert(ReferenceEquals(Catch(worker.Complete), failure) && cleaned == 1, "Trailer failure or cleanup was lost");
    }

    private static void ReleaseFailureStillReleasesPendingItems()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var failure = new Exception("event wait failed");
        var released = new List<int>();
        using var worker = new EncoderWorker<int>(3, () => { }, _ => { entered.Set(); Wait(release); }, item =>
        {
            released.Add(item);
            if (item == 1) throw failure;
        }, () => { }, () => { });
        worker.Start();
        worker.Submit(1);
        try { Wait(entered); worker.Submit(2); worker.Submit(3); }
        finally { release.Set(); }
        Assert(ReferenceEquals(Catch(worker.Complete), failure), "Release failure was hidden");
        Assert(released.SequenceEqual(new[] { 1, 2, 3 }), "A release failure prevented remaining releases");
    }

    private static void RetriesSameFrameAfterDrainingEagain()
    {
        var sends = new Queue<int>(new[] { again, 0 });
        var frames = new List<IntPtr>();
        int receives = 0;
        var pump = new EncoderPacketPump(frame => { frames.Add(frame); return sends.Dequeue(); },
            () => { receives++; return (1, again); }, Check, again, eof);
        pump.Send((IntPtr)123);
        Assert(frames.SequenceEqual(new[] { (IntPtr)123, (IntPtr)123 }) && receives == 2, "EAGAIN did not retry the same owned frame");
    }

    private static void ConcurrentCompletionWaitsForCleanup()
    {
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        using var firstStarted = new ManualResetEventSlim();
        using var secondStarted = new ManualResetEventSlim();
        int cleaned = 0;
        using var worker = new EncoderWorker<int>(1, () => { }, _ => { entered.Set(); Wait(release); }, _ => { },
            () => { }, () => cleaned++);
        worker.Start();
        worker.Submit(1);
        Wait(entered);
        var first = Task.Run(() => { firstStarted.Set(); worker.Complete(); });
        var second = Task.Run(() => { secondStarted.Set(); worker.Complete(); });
        try
        {
            Wait(firstStarted);
            Wait(secondStarted);
            Assert(!first.Wait(100) && !second.Wait(100), "Concurrent completion returned before cleanup");
        }
        finally { release.Set(); Wait(first); Wait(second); }
        Assert(cleaned == 1, "Concurrent completion ran cleanup more than once");
    }

    private static void RejectsEagainWithoutOutput()
    {
        var pump = new EncoderPacketPump(_ => again, () => (0, again), Check, again, eof);
        Assert(Catch(() => pump.Send((IntPtr)123)) is InvalidOperationException, "Broken send/receive contract caused a retry loop");
    }

    private static void FlushRetriesAndReachesEof()
    {
        var sends = new Queue<int>(new[] { again, 0 });
        var receives = new Queue<(int, int)>(new[] { (1, again), (2, eof) });
        var pump = new EncoderPacketPump(frame => { Assert(frame == IntPtr.Zero, "Flush retried a non-null frame"); return sends.Dequeue(); },
            () => receives.Dequeue(), Check, again, eof);
        pump.Send(IntPtr.Zero);
        Assert(sends.Count == 0 && receives.Count == 0, "Flush did not drain all packets");
    }

    private static void FlushRejectsUnexpectedEagain()
    {
        var pump = new EncoderPacketPump(_ => 0, () => (1, again), Check, again, eof);
        Assert(Catch(() => pump.Send(IntPtr.Zero)) is InvalidOperationException, "Flush accepted an incomplete drain");
    }

    private static void FlushAcceptsAlreadyDrainedEof()
    {
        var pump = new EncoderPacketPump(_ => eof, () => throw new Exception("Unexpected receive"), Check, again, eof);
        pump.Send(IntPtr.Zero);
    }

    private static void SendFailurePreservesError()
    {
        var failure = new Exception("send failed");
        var pump = new EncoderPacketPump(_ => -22, () => throw new Exception("Unexpected receive"), _ => throw failure, again, eof);
        Assert(ReferenceEquals(Catch(() => pump.Send((IntPtr)123)), failure), "Send failure was replaced");
    }

    private static void Check(int result) { if (result < 0) throw new Exception($"Native error {result}"); }
    private static Exception Catch(Action action) { try { action(); return null; } catch (Exception exception) { return exception; } }
    private static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
    private static void Wait(ManualResetEventSlim signal) => Assert(signal.Wait(TimeSpan.FromSeconds(5)), "Signal timed out");
    private static void Wait(Task task) { Assert(task.Wait(TimeSpan.FromSeconds(5)), "Task timed out"); task.GetAwaiter().GetResult(); }
}

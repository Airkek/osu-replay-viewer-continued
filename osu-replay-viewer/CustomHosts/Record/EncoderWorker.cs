using System;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace osu_replay_renderer_netcore.CustomHosts.Record;

internal sealed class EncoderWorker<T> : IDisposable
{
    private readonly BlockingCollection<T> queue;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Action initialize;
    private readonly Action<T> process;
    private readonly Action<T> release;
    private readonly Action finish;
    private readonly Action cleanup;
    private readonly TaskCompletionSource started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private volatile ExceptionDispatchInfo failure;
    private Task worker;
    private int completed;

    public EncoderWorker(int capacity, Action initialize, Action<T> process, Action<T> release, Action finish, Action cleanup)
    {
        queue = new BlockingCollection<T>(capacity);
        this.initialize = initialize;
        this.process = process;
        this.release = release;
        this.finish = finish;
        this.cleanup = cleanup;
    }

    public CancellationToken CancellationToken => cancellation.Token;

    public void Start()
    {
        if (worker != null)
            throw new InvalidOperationException("The encoder worker has already started");
        worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        try
        {
            started.Task.GetAwaiter().GetResult();
        }
        catch
        {
            worker.GetAwaiter().GetResult();
            throw;
        }
    }

    public void Submit(T item)
    {
        ThrowIfFaulted();
        try
        {
            queue.Add(item, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            ThrowIfFaulted();
            throw;
        }
    }

    public void ThrowIfFaulted() => failure?.Throw();

    private void Fail(Exception exception)
    {
        failure ??= ExceptionDispatchInfo.Capture(exception);
        cancellation.Cancel();
    }

    private void Run()
    {
        try
        {
            initialize();
            started.SetResult();
            foreach (var item in queue.GetConsumingEnumerable())
            {
                try
                {
                    process(item);
                }
                catch (Exception exception)
                {
                    Fail(exception);
                    return;
                }
                finally
                {
                    release(item);
                }
            }
            finish();
        }
        catch (Exception exception)
        {
            Fail(exception);
            started.TrySetException(exception);
        }
        finally
        {
            while (queue.TryTake(out var item))
            {
                try { release(item); }
                catch (Exception exception) { Fail(exception); }
            }
            try { cleanup(); }
            catch (Exception exception) { Fail(exception); }
        }
    }

    public void Complete()
    {
        bool firstCompletion = Interlocked.Exchange(ref completed, 1) == 0;
        if (firstCompletion)
            queue.CompleteAdding();
        worker?.GetAwaiter().GetResult();
        if (firstCompletion)
            ThrowIfFaulted();
    }

    public void Dispose()
    {
        try { Complete(); }
        finally
        {
            queue.Dispose();
            cancellation.Dispose();
        }
    }
}

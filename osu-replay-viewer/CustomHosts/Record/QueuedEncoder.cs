using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace osu_replay_renderer_netcore.CustomHosts.Record;

public sealed class QueuedEncoder : EncoderBase, IDisposable
{
    private readonly EncoderBase encoder;
    private readonly int capacity;
    private readonly int frameSize;
    private readonly ArrayPool<byte> bufferPool;
    private BlockingCollection<byte[]> availableBuffers;
    private BlockingCollection<byte[]> pendingFrames;
    private CancellationTokenSource cancellation;
    private Task worker;
    private volatile ExceptionDispatchInfo failure;
    private volatile bool started;
    private volatile bool finished;

    public QueuedEncoder(EncoderBase encoder, int capacity = 3) : this(encoder, capacity, ArrayPool<byte>.Shared)
    {
    }

    internal QueuedEncoder(EncoderBase encoder, int capacity, ArrayPool<byte> bufferPool) : base(encoder.Config)
    {
        if (capacity < 1)
            throw new ArgumentOutOfRangeException(nameof(capacity));

        this.encoder = encoder;
        this.capacity = capacity;
        this.bufferPool = bufferPool;
        if (Config.Resolution.Width < 1 || Config.Resolution.Height < 1)
            throw new ArgumentOutOfRangeException(nameof(encoder), "The encoder resolution must be positive");
        int rgbSize = checked(Config.Resolution.Width * Config.Resolution.Height * 3);
        frameSize = Config.PixelFormat is PixelFormatMode.RGB or PixelFormatMode.YUV444 ? rgbSize : rgbSize / 2;
    }

    public override bool CanWrite
    {
        get
        {
            failure?.Throw();
            if (!started || finished)
                return false;
            if (!encoder.CanWrite)
                throw new InvalidOperationException("The encoder stopped accepting frames");
            return true;
        }
    }

    protected override void _startInternal()
    {
        if (started)
            throw new InvalidOperationException("The encoder has already been started");

        try
        {
            encoder.Start();
            availableBuffers = new BlockingCollection<byte[]>(capacity);
            pendingFrames = new BlockingCollection<byte[]>(capacity);
            cancellation = new CancellationTokenSource();
            for (int i = 0; i < capacity; i++)
                availableBuffers.Add(bufferPool.Rent(frameSize));

            worker = Task.Factory.StartNew(ConsumeFrames, CancellationToken.None,
                TaskCreationOptions.LongRunning, TaskScheduler.Default);
            started = true;
        }
        catch
        {
            ReleaseBuffers();
            try
            {
                encoder.Finish();
            }
            catch
            {
                // Preserve the startup failure after releasing encoder resources
            }
            throw;
        }
    }

    protected override void _writeFrameInternal(ReadOnlySpan<byte> frame)
    {
        if (frame.Length != frameSize)
            throw new ArgumentException($"Expected a {frameSize}-byte frame, received {frame.Length}", nameof(frame));

        byte[] buffer = null;
        try
        {
            buffer = availableBuffers.Take(cancellation.Token);
            frame.CopyTo(buffer);
            pendingFrames.Add(buffer, cancellation.Token);
            buffer = null;
        }
        catch (OperationCanceledException)
        {
            failure?.Throw();
            throw;
        }
        finally
        {
            if (buffer != null)
                availableBuffers.Add(buffer);
        }
    }

    private void ConsumeFrames()
    {
        try
        {
            foreach (var buffer in pendingFrames.GetConsumingEnumerable())
            {
                try
                {
                    if (!encoder.CanWrite)
                        throw new InvalidOperationException("The encoder stopped accepting frames");
                    encoder.WriteFrame(buffer.AsSpan(0, frameSize));
                }
                catch (Exception exception)
                {
                    failure = ExceptionDispatchInfo.Capture(exception);
                    cancellation.Cancel();
                    return;
                }
                finally
                {
                    availableBuffers.Add(buffer);
                }
            }
        }
        catch (Exception exception)
        {
            failure = ExceptionDispatchInfo.Capture(exception);
            cancellation.Cancel();
        }
        finally
        {
            while (pendingFrames.TryTake(out var buffer))
                availableBuffers.Add(buffer);
        }
    }

    protected override void _finishInternal()
    {
        if (!started || finished)
            return;

        finished = true;
        pendingFrames.CompleteAdding();
        ExceptionDispatchInfo finishFailure = null;
        try
        {
            worker.GetAwaiter().GetResult();
            failure?.Throw();
        }
        catch (Exception exception)
        {
            finishFailure = ExceptionDispatchInfo.Capture(exception);
        }
        finally
        {
            try
            {
                encoder.Finish();
            }
            catch (Exception exception)
            {
                finishFailure ??= ExceptionDispatchInfo.Capture(exception);
            }
            finally
            {
                ReleaseBuffers();
            }
        }

        finishFailure?.Throw();
    }

    private void ReleaseBuffers()
    {
        if (availableBuffers != null)
        {
            while (availableBuffers.TryTake(out var buffer))
                bufferPool.Return(buffer);
            availableBuffers.Dispose();
            availableBuffers = null;
        }

        pendingFrames?.Dispose();
        pendingFrames = null;
        cancellation?.Dispose();
        cancellation = null;
    }

    public void Dispose() => Finish();
}

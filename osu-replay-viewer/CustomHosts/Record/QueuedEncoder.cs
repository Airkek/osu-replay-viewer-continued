using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace osu_replay_renderer_netcore.CustomHosts.Record;

public sealed class QueuedEncoder : EncoderBase, IDisposable, IOwnedFrameEncoder, IEncoderAudioInput
{
    private readonly EncoderBase encoder;
    private readonly int capacity;
    private readonly int frameSize;
    private readonly ArrayPool<byte> bufferPool;
    private BlockingCollection<byte[]> availableBuffers;
    private BlockingCollection<IFrameLease> pendingFrames;
    private SemaphoreSlim slots;
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

    public void ConfigureAudio(int sampleRate, int channels)
    {
        lock (WriteLocker)
        {
            if (started)
                throw new InvalidOperationException("Configure audio before starting the encoder");
            if (encoder is not IEncoderAudioInput audio)
                throw new NotSupportedException("The wrapped encoder does not accept audio");
            audio.ConfigureAudio(sampleRate, channels);
        }
    }

    public void WriteAudio(ReadOnlySpan<byte> pcm)
    {
        failure?.Throw();
        if (!CanWrite)
            throw new InvalidOperationException("The encoder is not recording");
        if (encoder is not IEncoderAudioInput audio)
            throw new NotSupportedException("The wrapped encoder does not accept audio");
        audio.WriteAudio(pcm);
    }

    protected override void _startInternal()
    {
        if (started)
            throw new InvalidOperationException("The encoder has already been started");

        try
        {
            encoder.Start();
            availableBuffers = new BlockingCollection<byte[]>(capacity);
            pendingFrames = new BlockingCollection<IFrameLease>(capacity);
            slots = new SemaphoreSlim(capacity, capacity);
            cancellation = new CancellationTokenSource();
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
        bool acquired = false;
        try
        {
            slots.Wait(cancellation.Token);
            acquired = true;
            failure?.Throw();
            if (!availableBuffers.TryTake(out buffer))
                buffer = bufferPool.Rent(frameSize);
            frame.CopyTo(buffer);
            pendingFrames.Add(new PooledFrameLease(buffer, frameSize, availableBuffers), cancellation.Token);
            buffer = null;
            acquired = false;
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
            if (acquired)
                slots.Release();
        }
    }

    public void WriteFrame(IFrameLease frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        bool acquired = false;
        try
        {
            lock (WriteLocker)
            {
                if (!CanWrite)
                    return;
                if (frame.Data.Length != frameSize)
                    throw new ArgumentException($"Expected a {frameSize}-byte frame, received {frame.Data.Length}", nameof(frame));
                slots.Wait(cancellation.Token);
                acquired = true;
                failure?.Throw();
                pendingFrames.Add(frame, cancellation.Token);
                frame = null;
                acquired = false;
            }
        }
        catch (OperationCanceledException)
        {
            failure?.Throw();
            throw;
        }
        finally
        {
            frame?.Dispose();
            if (acquired)
                slots.Release();
        }
    }

    private void ConsumeFrames()
    {
        try
        {
            foreach (var frame in pendingFrames.GetConsumingEnumerable())
            {
                try
                {
                    if (!encoder.CanWrite)
                        throw new InvalidOperationException("The encoder stopped accepting frames");
                    encoder.WriteFrame(frame.Data);
                }
                catch (Exception exception)
                {
                    failure = ExceptionDispatchInfo.Capture(exception);
                    cancellation.Cancel();
                    throw;
                }
                finally
                {
                    try { frame.Dispose(); }
                    finally { slots.Release(); }
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
            while (pendingFrames.TryTake(out var frame))
            {
                frame.Dispose();
                slots.Release();
            }
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
        slots?.Dispose();
        slots = null;
        cancellation?.Dispose();
        cancellation = null;
    }

    private sealed class PooledFrameLease : IFrameLease
    {
        private byte[] buffer;
        private readonly int length;
        private readonly BlockingCollection<byte[]> available;

        public PooledFrameLease(byte[] buffer, int length, BlockingCollection<byte[]> available)
        {
            this.buffer = buffer;
            this.length = length;
            this.available = available;
        }

        public ReadOnlySpan<byte> Data => buffer.AsSpan(0, length);
        public void Dispose()
        {
            var returned = Interlocked.Exchange(ref buffer, null);
            if (returned != null)
                available.Add(returned);
        }
    }

    public void Dispose() => Finish();
}

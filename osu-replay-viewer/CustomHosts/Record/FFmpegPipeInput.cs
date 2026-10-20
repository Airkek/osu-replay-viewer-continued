using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Runtime.ExceptionServices;
using System.Threading;
using System.Threading.Tasks;

namespace osu_replay_renderer_netcore.CustomHosts.Record;

internal sealed class FFmpegPipeInput : IDisposable
{
    private readonly TcpListener listener = new(IPAddress.Loopback, 0);
    private readonly object gate = new();
    private readonly Task<NetworkStream> connection;
    private TcpClient client;
    private bool disposed;

    internal string Url { get; }

    internal FFmpegPipeInput()
    {
        listener.Start(1);
        Url = $"tcp://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}";
        connection = Accept();
    }

    private async Task<NetworkStream> Accept()
    {
        var accepted = await listener.AcceptTcpClientAsync().ConfigureAwait(false);
        lock (gate)
        {
            if (disposed)
            {
                accepted.Dispose();
                throw new ObjectDisposedException(nameof(FFmpegPipeInput));
            }
            client = accepted;
            client.NoDelay = true;
            listener.Stop();
            return client.GetStream();
        }
    }

    internal void Write(ReadOnlySpan<byte> data) => connection.GetAwaiter().GetResult().Write(data);

    internal void Complete()
    {
        connection.WaitAsync(TimeSpan.FromSeconds(30)).GetAwaiter().GetResult();
        Dispose();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            listener.Stop();
            client?.Dispose();
        }
    }
}

internal sealed class FFmpegAudioPipe : IDisposable
{
    private const int chunkSize = 16384;
    private readonly FFmpegPipeInput input = new();
    private readonly BlockingCollection<(byte[] Buffer, int Length)> chunks = new(32);
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task worker;
    private ExceptionDispatchInfo failure;
    private bool disposed;

    internal string Url => input.Url;

    internal FFmpegAudioPipe() => worker = Task.Factory.StartNew(Consume, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    internal void Write(ReadOnlySpan<byte> data)
    {
        failure?.Throw();
        while (!data.IsEmpty)
        {
            int count = Math.Min(data.Length, chunkSize);
            byte[] buffer = ArrayPool<byte>.Shared.Rent(count);
            try
            {
                data[..count].CopyTo(buffer);
                chunks.Add((buffer, count), cancellation.Token);
                buffer = null;
            }
            catch (OperationCanceledException)
            {
                failure?.Throw();
                throw;
            }
            finally
            {
                if (buffer != null) ArrayPool<byte>.Shared.Return(buffer);
            }
            data = data[count..];
        }
    }

    private void Consume()
    {
        try
        {
            foreach (var chunk in chunks.GetConsumingEnumerable(cancellation.Token))
            {
                try { input.Write(chunk.Buffer.AsSpan(0, chunk.Length)); }
                finally { ArrayPool<byte>.Shared.Return(chunk.Buffer); }
            }
            input.Complete();
        }
        catch (Exception exception)
        {
            Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(exception), null);
            cancellation.Cancel();
        }
        finally
        {
            while (chunks.TryTake(out var chunk)) ArrayPool<byte>.Shared.Return(chunk.Buffer);
            input.Dispose();
        }
    }

    internal void Abort(Exception exception)
    {
        Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(exception), null);
        cancellation.Cancel();
        input.Dispose();
    }

    internal void Finish()
    {
        chunks.CompleteAdding();
        worker.GetAwaiter().GetResult();
        failure?.Throw();
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Abort(new OperationCanceledException("FFmpeg audio input was closed"));
        worker.GetAwaiter().GetResult();
        chunks.Dispose();
        cancellation.Dispose();
    }
}

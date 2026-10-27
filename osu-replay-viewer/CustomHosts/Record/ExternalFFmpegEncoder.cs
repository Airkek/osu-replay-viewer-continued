using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;

namespace osu_replay_renderer_netcore.CustomHosts.Record;

public class ExternalFFmpegEncoder : EncoderBase, IEncoderAudioInput
{
    private Process process;
    private Stream inputStream;
    private FFmpegPipeInput videoInput;
    private FFmpegAudioPipe audioInput;
    private readonly ConcurrentQueue<string> diagnostics = new();
    private ExceptionDispatchInfo failure;
    private int sampleRate;
    private int channels;
    private volatile bool started;
    private volatile bool finishing;

    public ExternalFFmpegEncoder(EncoderConfig config) : base(config) { }

    public override bool CanWrite
    {
        get
        {
            failure?.Throw();
            return started && !finishing;
        }
    }

    public void ConfigureAudio(int sampleRate, int channels)
    {
        lock (WriteLocker)
        {
            if (started) throw new InvalidOperationException("Configure audio before starting FFmpeg");
            if (sampleRate < 1 || channels < 1) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            this.sampleRate = sampleRate;
            this.channels = channels;
        }
    }

    internal IReadOnlyList<string> BuildArguments(FFmpegEncoderCapabilities capabilities, string videoUrl, string audioUrl = null)
    {
        string pixelFormat = Config.PixelFormat switch
        {
            PixelFormatMode.YUV420 => "yuv420p",
            PixelFormatMode.YUV444 => "yuv444p",
            PixelFormatMode.NV12 => "nv12",
            _ => "rgb24"
        };
        var arguments = new List<string> { "-y", "-hide_banner", "-nostdin" };
        if (audioUrl != null)
            arguments.AddRange(new[] { "-thread_queue_size", "8", "-nofind_stream_info" });
        arguments.AddRange(new[] { "-f", "rawvideo", "-pixel_format", pixelFormat,
            "-video_size", $"{Config.Resolution.Width}x{Config.Resolution.Height}", "-framerate", Config.FPS.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", videoUrl });
        if (audioUrl != null)
        {
            arguments.AddRange(new[] { "-thread_queue_size", "32", "-nofind_stream_info", "-f", "s16le",
                "-ar", sampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture), "-ac", channels.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", audioUrl,
                "-map", "0:v:0", "-map", "1:a:0", "-c:a", "aac", "-b:a",
                (Config.AudioBitrate ?? 192000).ToString(System.Globalization.CultureInfo.InvariantCulture) });
        }
        arguments.AddRange(new[] { "-c:v", Config.Encoder });
        if (Config.PixelFormat == PixelFormatMode.RGB)
            arguments.AddRange(new[] { "-vf", "vflip" });
        else
            arguments.AddRange(Config.ColorSpace == ColorSpaceMode.BT601
                ? new[] { "-colorspace", "bt470bg", "-color_primaries", "bt470bg", "-color_trc", "gamma22", "-color_range", "pc" }
                : new[] { "-colorspace", "bt709", "-color_primaries", "bt709", "-color_trc", "bt709", "-color_range", "pc" });
        arguments.AddRange(FFmpegEncoderOptions.Build(Config, capabilities));
        // FFmpeg 4.x can otherwise choose 25 FPS when input stream probing is disabled
        arguments.AddRange(new[] { "-r", Config.FPS.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-pix_fmt", Config.PixelFormat == PixelFormatMode.RGB ? "yuv420p" : pixelFormat, Config.OutputPath });
        return arguments;
    }

    protected override void _startInternal()
    {
        if (started) throw new InvalidOperationException("FFmpeg has already been started");
        var capabilities = FFmpegEncoderCapabilities.Get(Config.FFmpegExec, Config.Encoder);
        if (sampleRate > 0)
        {
            videoInput = new FFmpegPipeInput();
            audioInput = new FFmpegAudioPipe();
        }
        process = new Process
        {
            StartInfo = new ProcessStartInfo(Config.FFmpegExec)
            {
                UseShellExecute = false,
                RedirectStandardInput = audioInput == null,
                RedirectStandardError = true,
                CreateNoWindow = true
            },
            EnableRaisingEvents = true
        };
        foreach (var argument in BuildArguments(capabilities, videoInput?.Url ?? "pipe:0", audioInput?.Url))
            process.StartInfo.ArgumentList.Add(argument);
        Console.WriteLine("Starting FFmpeg process with arguments: " + string.Join(" ", process.StartInfo.ArgumentList.Select(argument => $"\"{argument.Replace("\"", "\\\"")}\"")));
        process.ErrorDataReceived += (_, args) =>
        {
            if (args.Data == null) return;
            diagnostics.Enqueue(args.Data);
            while (diagnostics.Count > 30) diagnostics.TryDequeue(out var ignored);
        };
        var runningProcess = process;
        process.Exited += (_, _) =>
        {
            try
            {
                if (!finishing || runningProcess.ExitCode != 0)
                    Fail(new IOException($"FFmpeg exited with code {runningProcess.ExitCode}: {string.Join(Environment.NewLine, diagnostics)}"));
            }
            catch (InvalidOperationException)
            {
                // The owner has already disposed the completed process
            }
        };
        process.Start();
        process.BeginErrorReadLine();
        if (audioInput == null) inputStream = process.StandardInput.BaseStream;
        started = true;
    }

    protected override void _writeFrameInternal(ReadOnlySpan<byte> frame)
    {
        try
        {
            if (videoInput != null) videoInput.Write(frame);
            else inputStream.Write(frame);
        }
        catch (Exception exception)
        {
            Fail(exception);
            failure.Throw();
        }
    }

    public void WriteAudio(ReadOnlySpan<byte> pcm)
    {
        failure?.Throw();
        if (!started || finishing || audioInput == null)
            throw new InvalidOperationException("FFmpeg audio was not configured or is not recording");
        if (pcm.Length % (channels * 2) != 0)
            throw new ArgumentException("Audio must contain whole interleaved signed 16-bit samples", nameof(pcm));
        try { audioInput.Write(pcm); }
        catch (Exception exception)
        {
            Fail(exception);
            failure.Throw();
        }
    }

    private void Fail(Exception exception)
    {
        InterlockedFailure(exception);
        videoInput?.Dispose();
        audioInput?.Abort(exception);
    }

    private void InterlockedFailure(Exception exception) =>
        System.Threading.Interlocked.CompareExchange(ref failure, ExceptionDispatchInfo.Capture(exception), null);

    protected override void _finishInternal()
    {
        if (process == null && videoInput == null && audioInput == null) return;
        finishing = true;
        try
        {
            inputStream?.Dispose();
            inputStream = null;
            if (failure == null) videoInput?.Complete();
            else videoInput?.Dispose();
            audioInput?.Finish();
            if (process != null)
            {
                if (!process.WaitForExit(30000))
                    throw new TimeoutException("FFmpeg did not finish after closing its inputs");
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new IOException($"FFmpeg exited with code {process.ExitCode}: {string.Join(Environment.NewLine, diagnostics)}");
            }
            failure?.Throw();
        }
        catch
        {
            if (process != null)
            {
                try { if (!process.HasExited) process.Kill(true); }
                catch (InvalidOperationException) { }
            }
            throw;
        }
        finally
        {
            videoInput?.Dispose();
            audioInput?.Dispose();
            process?.Dispose();
            process = null;
            videoInput = null;
            audioInput = null;
            started = false;
        }
    }
}

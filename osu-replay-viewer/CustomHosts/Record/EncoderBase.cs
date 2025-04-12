using System;
using System.Drawing;

namespace osu_replay_renderer_netcore.CustomHosts.Record;

public struct EncoderConfig
{
    public int FPS;
    public Size Resolution;
    public string OutputPath;
    public string Preset;
    public string Encoder;
    public string Bitrate;
    public bool MotionInterpolation;
    public int FramesBlending;
    public string FFmpegPath;
    public string FFmpegExec;
}

public abstract class EncoderBase
{
    private readonly object WriteLocker = new();

    public readonly int FPS;
    public readonly Size Resolution;
    public readonly string OutputPath;
    public readonly string Preset;
    public readonly string Encoder;
    public readonly string Bitrate;
    public readonly bool MotionInterpolation;
    public readonly int FramesBlending;
    public readonly string FFmpegPath; 
    public readonly string FFmpegExec; 
    public abstract bool CanWrite { get; }

    public EncoderBase(EncoderConfig config)
    {
        FPS = config.FPS;
        Resolution = config.Resolution;
        OutputPath = config.OutputPath;
        Preset = config.Preset;
        Encoder = config.Encoder;
        Bitrate = config.Bitrate;
        MotionInterpolation = config.MotionInterpolation;
        FramesBlending = config.FramesBlending;
        FFmpegPath = config.FFmpegPath;
        FFmpegExec = config.FFmpegExec ?? "ffmpeg";
    }

    protected abstract void _writeFrameInternal(ReadOnlySpan<byte> frame);
    protected abstract void _finishInternal();
    protected abstract void _startInternal();

    public void WriteFrame(ReadOnlySpan<byte> frame)
    {
        lock (WriteLocker)
        {
            if (!CanWrite)
            {
                return;
            }
            _writeFrameInternal(frame);
        }
    }

    public void Finish()
    {
        lock (WriteLocker)
        {
            _finishInternal();
        }
    }

    public void Start()
    {
        lock (WriteLocker)
        {
            _startInternal();
        }
    }
}
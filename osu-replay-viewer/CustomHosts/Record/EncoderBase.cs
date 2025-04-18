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
    public bool ShowFFmpegOutput;
}

public abstract class EncoderBase
{
    private readonly object WriteLocker = new();

    public readonly EncoderConfig Config;
    public abstract bool CanWrite { get; }

    public EncoderBase(EncoderConfig config)
    {
        Config = config;
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
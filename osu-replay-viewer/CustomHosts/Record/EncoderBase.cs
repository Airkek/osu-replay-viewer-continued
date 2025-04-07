using System;

namespace osu_replay_renderer_netcore.CustomHosts.Record;

public abstract class EncoderBase
{
    private readonly object WriteLocker = new();
    public int FPS { get; set; } = 60;
    public System.Drawing.Size Resolution { get; set; }
    public string OutputPath { get; set; } = "output.mp4";
    public string Preset { get; set; } = "slow";
    public string Encoder { get; set; } = "libx264";
    public string Bitrate { get; set; } = "100M";
    public bool MotionInterpolation { get; set; } = false;
    
    public abstract bool CanWrite { get; }

    /// <summary>
    /// Blend multiple frames. Values that's lower than or equals to 1 will disable frames
    /// blending. Frames blending makes encoding process way slower
    /// </summary>
    public int FramesBlending { get; set; } = 1;

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
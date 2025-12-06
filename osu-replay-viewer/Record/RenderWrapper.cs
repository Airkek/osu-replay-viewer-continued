using System.Drawing;
using osu_replay_renderer_netcore.CustomHosts.Record;

namespace osu_replay_renderer_netcore.Record;

public abstract class RenderWrapper
{
    protected Size DesiredSize;
    protected PixelFormatMode PixelFormat;

    public RenderWrapper(Size desiredSize, PixelFormatMode pixelFormat = PixelFormatMode.RGB)
    {
        DesiredSize = desiredSize;
        PixelFormat = pixelFormat;
    }
    public abstract void WriteFrame(EncoderBase encoder);
    public virtual void Finish(EncoderBase encoder) { }
}
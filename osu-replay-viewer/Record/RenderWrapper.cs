using System.Drawing;
using osu_replay_renderer_netcore.CustomHosts.Record;

namespace osu_replay_renderer_netcore.Record;

public abstract class RenderWrapper
{
    protected Size DesiredSize;
    protected PixelFormatMode PixelFormat;
    protected ColorSpaceMode ColorSpace;

    public RenderWrapper(Size desiredSize, PixelFormatMode pixelFormat = PixelFormatMode.RGB, ColorSpaceMode colorSpace = ColorSpaceMode.BT709)
    {
        DesiredSize = desiredSize;
        PixelFormat = pixelFormat;
        ColorSpace = colorSpace;
    }
    public abstract void WriteFrame(EncoderBase encoder);
    public virtual void Finish(EncoderBase encoder) { }
}
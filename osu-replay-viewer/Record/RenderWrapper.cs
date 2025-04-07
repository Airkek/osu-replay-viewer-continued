using System.Drawing;
using osu_replay_renderer_netcore.CustomHosts.Record;

namespace osu_replay_renderer_netcore.Record;

public abstract class RenderWrapper
{
    protected Size DesiredSize;

    public RenderWrapper(Size desiredSize)
    {
        DesiredSize = desiredSize;
    }
    public abstract void WriteFrame(EncoderBase encoder);
}
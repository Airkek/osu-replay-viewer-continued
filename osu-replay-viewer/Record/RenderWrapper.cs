using System.Drawing;
using System.IO;

namespace osu_replay_renderer_netcore.Record;

public abstract class RenderWrapper
{
    protected Size DesiredSize;

    public RenderWrapper(Size desiredSize)
    {
        DesiredSize = desiredSize;
    }
    public abstract void WriteScreenshotToStream(Stream stream);
}
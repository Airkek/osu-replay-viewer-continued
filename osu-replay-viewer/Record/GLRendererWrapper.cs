using System;
using System.Drawing;
using System.Reflection;
using osu_replay_renderer_netcore.CustomHosts.Record;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Platform;
using osuTK.Graphics.ES30;

using System.IO;
using osu_replay_renderer_netcore.Record.OpenGL;

namespace osu_replay_renderer_netcore.Record;

public class GLRendererWrapper : RenderWrapper
{
    private static readonly Type GLRendererType =
        typeof(IRenderer).Assembly.GetType("osu.Framework.Graphics.OpenGL.GLRenderer");

    private static readonly FieldInfo GLRenderer_openGLSurfaceField = GLRendererType.GetField("openGLSurface",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private readonly IGraphicsSurface surface;
    private readonly IOpenGLGraphicsSurface openGLSurface;
    
    private readonly OpenGLCapturer capturer;
    
    public GLRendererWrapper(IRenderer renderer, Size desiredSize, PixelFormatMode pixelFormat) : base(desiredSize, pixelFormat)
    {
        if (renderer.GetType() != GLRendererType)
            throw new ArgumentException($"Not supported renderer: {renderer.GetType()}");

        var graphicsSurfaceObj = GLRenderer_openGLSurfaceField.GetValue(renderer);
        if (graphicsSurfaceObj is null) throw new Exception("graphicsSurface is null");
        if (graphicsSurfaceObj is not IOpenGLGraphicsSurface or not IGraphicsSurface)
            throw new NotSupportedException("graphicsSurface has unexpected type");

        surface = (IGraphicsSurface)graphicsSurfaceObj;
        openGLSurface = (IOpenGLGraphicsSurface)graphicsSurfaceObj;

        capturer = new OpenGLCapturer(new OsuTKOpenGLAdapter(), DesiredSize, PixelFormat);
    }

    private void WithGLContext(Action action)
    {
        var windowContext = openGLSurface.WindowContext;
        if (windowContext == IntPtr.Zero)
            throw new InvalidOperationException("OpenGL window context is not available.");

        var previousContext = openGLSurface.CurrentContext;
        bool needSwitch = previousContext != windowContext;

        if (needSwitch)
            openGLSurface.MakeCurrent(windowContext);

        try
        {
            action();
        }
        finally
        {
            if (needSwitch)
            {
                if (previousContext != IntPtr.Zero)
                {
                    openGLSurface.MakeCurrent(previousContext);
                }
                else
                {
                    openGLSurface.ClearCurrent();
                }
            }
        }
    }

    public static bool IsSupported(IRenderer renderer)
    {
        return renderer.GetType() == GLRendererType;
    }

    public override void WriteFrame(EncoderBase encoder)
    {
        var size = surface.GetDrawableSize();
        if (size.Width != DesiredSize.Width || size.Height != DesiredSize.Height) return;

        WithGLContext(() =>
        {
            capturer.WriteFrame(encoder);
        });
    }

    public override void Finish(EncoderBase encoder)
    {
        WithGLContext(() =>
        {
            capturer.Finish(encoder);
        });
    }
}
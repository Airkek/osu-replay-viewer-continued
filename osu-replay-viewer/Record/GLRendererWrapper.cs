using System;
using System.Drawing;
using System.IO;
using System.Reflection;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Platform;
using osuTK.Graphics.ES30;

namespace osu_replay_renderer_netcore.Record;

public class GLRendererWrapper : RenderWrapper
{
    private static readonly Type GLRendererType =
        typeof(IRenderer).Assembly.GetType("osu.Framework.Graphics.OpenGL.GLRenderer");

    private static readonly FieldInfo GLRenderer_openGLSurfaceField = GLRendererType.GetField("openGLSurface",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private readonly IOpenGLGraphicsSurface openGLSurface;

    public GLRendererWrapper(IRenderer renderer, Size desiredSize) : base(desiredSize)
    {
        if (renderer.GetType() != GLRendererType)
            throw new ArgumentException($"Not supported renderer: {renderer.GetType()}");

        var graphicsSurfaceObj = GLRenderer_openGLSurfaceField.GetValue(renderer);
        if (graphicsSurfaceObj is null) throw new Exception("graphicsSurface is null");
        if (graphicsSurfaceObj is not IOpenGLGraphicsSurface obj)
            throw new NotSupportedException("graphicsSurface has unexpected type");

        openGLSurface = obj;
    }

    public override unsafe void WriteScreenshotToStream(Stream stream)
    {
        var size = ((IGraphicsSurface)openGLSurface).GetDrawableSize();
        if (size.Width != DesiredSize.Width || size.Height != DesiredSize.Height) return;

        var bufferSize = DesiredSize.Width * DesiredSize.Height * 3;

        uint pbo;
        GL.GenBuffers(1, out pbo);

        try
        {
            // Set up PBO
            GL.BindBuffer(BufferTarget.PixelPackBuffer, pbo);
            GL.BufferData(
                BufferTarget.PixelPackBuffer,
                bufferSize,
                IntPtr.Zero,
                BufferUsageHint.StreamRead);

            // Read pixels into PBO
            GL.ReadPixels(
                0, 0,
                size.Width, size.Height,
                PixelFormat.Rgb,
                PixelType.UnsignedByte,
                IntPtr.Zero);

            GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);

            // Ensure read operations are complete
            GL.Finish();

            // Map PBO to client memory
            GL.BindBuffer(BufferTarget.PixelPackBuffer, pbo);

            // var dataPtr = GL.glMapBuffer(
            //     BufferTarget.PixelPackBuffer,
            //     BufferAccess.ReadOnly);

            var dataPtr = GL.MapBufferRange(BufferTarget.PixelPackBuffer,
                IntPtr.Zero,
                bufferSize,
                BufferAccessMask.MapReadBit);

            if (dataPtr != IntPtr.Zero)
                try
                {
                    // Copy data directly from mapped memory to stream
                    var span = new ReadOnlySpan<byte>(dataPtr.ToPointer(), bufferSize);
                    stream.Write(span);
                }
                finally
                {
                    GL.UnmapBuffer(BufferTarget.PixelPackBuffer);
                }
        }
        finally
        {
            // Cleanup
            GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
            GL.DeleteBuffers(1, ref pbo);
        }
    }
}
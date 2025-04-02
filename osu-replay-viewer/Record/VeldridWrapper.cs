using System;
using System.IO;
using System.Reflection;
using System.Threading;
using osu_replay_renderer_netcore.CustomHosts.Record;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Platform;
using Veldrid;
using Veldrid.OpenGLBindings;

namespace osu_replay_renderer_netcore.Record;

public class VeldridWrapper
{
    private static readonly Type VeldridRendererType =
        typeof(IRenderer).Assembly.GetType("osu.Framework.Graphics.Veldrid.VeldridRenderer");

    private static readonly FieldInfo VeldridDeviceField = VeldridRendererType.GetField("veldridDevice",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private static readonly Type VeldridDeviceType =
        typeof(IRenderer).Assembly.GetType("osu.Framework.Graphics.Veldrid.VeldridDevice");

    private static readonly PropertyInfo GraphicsDeviceProperty =
        VeldridDeviceType.GetProperty("Device", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private static readonly FieldInfo GraphicsSurfaceField = VeldridDeviceType.GetField("graphicsSurface",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private readonly IGraphicsSurface graphicsSurface;
    private readonly GraphicsDevice Device;

    private readonly ExternalFFmpegEncoder Encoder;

    public VeldridWrapper(IRenderer renderer, ExternalFFmpegEncoder encoder)
    {
        Encoder = encoder;

        if (renderer.GetType() != VeldridRendererType) throw new ArgumentException("Only Veldrid renderer supported");

        var veldridDevice = VeldridDeviceField.GetValue(renderer);
        if (veldridDevice is null) throw new Exception("veldridDevice is null");

        if (veldridDevice.GetType() != VeldridDeviceType)
            throw new NotSupportedException("veldridDevice has unexpected type");

        var graphicsDevice = GraphicsDeviceProperty.GetValue(veldridDevice);
        if (graphicsDevice is null) throw new Exception("Device is null");
        if (graphicsDevice is not GraphicsDevice) throw new NotSupportedException("Device has unexpected type");
        Device = graphicsDevice as GraphicsDevice;

        var graphicsSurfaceObj = GraphicsSurfaceField.GetValue(veldridDevice);
        if (graphicsSurfaceObj is null) throw new Exception("graphicsSurface is null");
        if (graphicsSurfaceObj is not IGraphicsSurface)
            throw new NotSupportedException("graphicsSurface has unexpected type");
        graphicsSurface = graphicsSurfaceObj as IGraphicsSurface;
    }

    public ResourceFactory Factory
        => Device.ResourceFactory;

    public unsafe void WriteScreenshotToStream(Stream stream)
    {
        var texture = Device.SwapchainFramebuffer.ColorTargets[0].Target;
        
        var width = Encoder.Resolution.Width;
        var height = Encoder.Resolution.Height;

        if (texture.Width != width || texture.Height != height)
        {
            return;
        } 

        switch (graphicsSurface.Type)
        {
            case GraphicsSurfaceType.OpenGL:
            {
                var bufferSize = width * height * 3;

                var info = Device.GetOpenGLInfo();

                info.ExecuteOnGLThread(() =>
                {
                    uint pbo;
                    OpenGLNative.glGenBuffers(1, out pbo);

                    try
                    {
                        // Set up PBO
                        OpenGLNative.glBindBuffer(BufferTarget.PixelPackBuffer, pbo);
                        OpenGLNative.glBufferData(
                            BufferTarget.PixelPackBuffer,
                            (UIntPtr)bufferSize,
                            IntPtr.Zero.ToPointer(),
                            BufferUsageHint.StreamRead);

                        // Read pixels into PBO
                        OpenGLNative.glReadPixels(
                            0, 0,
                            texture.Width, texture.Height,
                            GLPixelFormat.Rgb,
                            GLPixelType.UnsignedByte,
                            IntPtr.Zero.ToPointer());

                        OpenGLNative.glBindBuffer(BufferTarget.PixelPackBuffer, 0);

                        // Ensure read operations are complete
                        OpenGLNative.glFinish();

                        // Map PBO to client memory
                        OpenGLNative.glBindBuffer(BufferTarget.PixelPackBuffer, pbo);
                        var dataPtr = OpenGLNative.glMapBuffer(
                            BufferTarget.PixelPackBuffer,
                            BufferAccess.ReadOnly);

                        if (dataPtr != IntPtr.Zero.ToPointer())
                            try
                            {
                                // Copy data directly from mapped memory to stream
                                var span = new ReadOnlySpan<byte>(dataPtr, bufferSize);
                                stream.Write(span);
                            }
                            finally
                            {
                                OpenGLNative.glUnmapBuffer(BufferTarget.PixelPackBuffer);
                            }
                    }
                    finally
                    {
                        // Cleanup
                        OpenGLNative.glBindBuffer(BufferTarget.PixelPackBuffer, 0);
                        OpenGLNative.glDeleteBuffers(1, ref pbo);
                    }
                });
                break;
            }

            default:
            {
                throw new NotSupportedException("Currently only OpenGL is supported");
            }
        }
    }
}
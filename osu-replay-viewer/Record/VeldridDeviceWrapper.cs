using System;
using System.Drawing;
using System.Reflection;
using osu_replay_renderer_netcore.CustomHosts.Record;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Platform;
using Veldrid;
using Veldrid.OpenGLBindings;

namespace osu_replay_renderer_netcore.Record;

public class VeldridDeviceWrapper : RenderWrapper
{
    private static readonly Type VeldridRendererType =
        typeof(IRenderer).Assembly.GetType("osu.Framework.Graphics.Veldrid.VeldridRenderer");
    private static readonly FieldInfo VeldridRenderer_veldridDeviceField = VeldridRendererType.GetField("veldridDevice",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private static readonly Type DeferredRendererType =
        typeof(IRenderer).Assembly.GetType("osu.Framework.Graphics.Rendering.Deferred.DeferredRenderer");
    private static readonly PropertyInfo DeferredRenderer_VeldridDeviceProperty = DeferredRendererType.GetProperty("VeldridDevice",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
    
    private static readonly Type VeldridDeviceType =
        typeof(IRenderer).Assembly.GetType("osu.Framework.Graphics.Veldrid.VeldridDevice");
    private static readonly PropertyInfo VeldridDevice_DeviceProperty =
        VeldridDeviceType.GetProperty("Device", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
    private static readonly FieldInfo VeldridDevice_graphicsSurfaceField = VeldridDeviceType.GetField("graphicsSurface",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private readonly IGraphicsSurface graphicsSurface;
    private readonly GraphicsDevice Device;

    public static bool IsSupported(IRenderer renderer)
    {
        return renderer.GetType() == VeldridRendererType || renderer.GetType() == DeferredRendererType;
    }

    public VeldridDeviceWrapper(IRenderer renderer, Size desiredSize) : base(desiredSize)
    {
        object veldridDevice;
        if (renderer.GetType() == VeldridRendererType)
        {
            veldridDevice = VeldridRenderer_veldridDeviceField.GetValue(renderer);
        } 
        else if (renderer.GetType() == DeferredRendererType)
        {
            veldridDevice = DeferredRenderer_VeldridDeviceProperty.GetValue(renderer);
        }
        else
        {
            throw new ArgumentException($"Not supported renderer: {renderer.GetType()}");
        }

        if (veldridDevice is null)
        {
            throw new Exception("veldrid device is null");
        }

        if (veldridDevice.GetType() != VeldridDeviceType)
        {
            throw new NotSupportedException("veldrid device has unexpected type");
        }
           

        var graphicsDevice = VeldridDevice_DeviceProperty.GetValue(veldridDevice);
        if (graphicsDevice is null) throw new Exception("Device is null");
        if (graphicsDevice is not GraphicsDevice) throw new NotSupportedException("Device has unexpected type");
        Device = graphicsDevice as GraphicsDevice;

        var graphicsSurfaceObj = VeldridDevice_graphicsSurfaceField.GetValue(veldridDevice);
        if (graphicsSurfaceObj is null)
        {
            throw new Exception("graphicsSurface is null");
        }
        if (graphicsSurfaceObj is not IGraphicsSurface)
        {
            throw new NotSupportedException("graphicsSurface has unexpected type");
        }
        graphicsSurface = graphicsSurfaceObj as IGraphicsSurface;
    }

    public ResourceFactory Factory
        => Device.ResourceFactory;

    public override unsafe void WriteFrame(EncoderBase encoder)
    {
        var texture = Device.SwapchainFramebuffer.ColorTargets[0].Target;
        
        var width = DesiredSize.Width;
        var height = DesiredSize.Height;

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
                                encoder.WriteFrame(span);
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
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

    private uint[] pboIds = new uint[2];
    private int pboIndex = 0;
    private bool pboInitialized = false;
    private int pboSize = 0;

    public static bool IsSupported(IRenderer renderer)
    {
        return renderer.GetType() == VeldridRendererType || renderer.GetType() == DeferredRendererType;
    }

    public VeldridDeviceWrapper(IRenderer renderer, Size desiredSize, PixelFormatMode pixelFormat) : base(desiredSize, pixelFormat)
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

    private unsafe void InitializePBOs(int size)
    {
        if (pboInitialized && pboSize == size) return;
        
        if (pboInitialized)
        {
            OpenGLNative.glDeleteBuffers(1, ref pboIds[0]);
            OpenGLNative.glDeleteBuffers(1, ref pboIds[1]);
        }

        OpenGLNative.glGenBuffers(1, out pboIds[0]);
        OpenGLNative.glGenBuffers(1, out pboIds[1]);

        for (int i = 0; i < 2; i++)
        {
            OpenGLNative.glBindBuffer(BufferTarget.PixelPackBuffer, pboIds[i]);
            OpenGLNative.glBufferData(BufferTarget.PixelPackBuffer, (UIntPtr)size, IntPtr.Zero.ToPointer(), BufferUsageHint.StreamRead);
        }
        OpenGLNative.glBindBuffer(BufferTarget.PixelPackBuffer, 0);
        pboInitialized = true;
        pboSize = size;
    }

    public ResourceFactory Factory
        => Device.ResourceFactory;

    public override unsafe void WriteFrame(EncoderBase encoder)
    {
        if (PixelFormat == PixelFormatMode.YUV420)
        {
            throw new NotImplementedException("YUV420 output is not supported with Veldrid renderer yet.");
        }

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
                    InitializePBOs(bufferSize);
                    
                    int index = pboIndex % 2;
                    int nextIndex = (pboIndex + 1) % 2;

                    // Read pixels into current PBO
                    OpenGLNative.glBindBuffer(BufferTarget.PixelPackBuffer, pboIds[index]);
                    OpenGLNative.glReadPixels(
                        0, 0,
                        texture.Width, texture.Height,
                        GLPixelFormat.Rgb,
                        GLPixelType.UnsignedByte,
                        IntPtr.Zero.ToPointer());
                    OpenGLNative.glBindBuffer(BufferTarget.PixelPackBuffer, 0);

                    // Process previous PBO
                    if (pboIndex > 0)
                    {
                        OpenGLNative.glBindBuffer(BufferTarget.PixelPackBuffer, pboIds[nextIndex]);
                        var dataPtr = OpenGLNative.glMapBuffer(
                            BufferTarget.PixelPackBuffer,
                            BufferAccess.ReadOnly);

                        if (dataPtr != IntPtr.Zero.ToPointer())
                            try
                            {
                                var span = new ReadOnlySpan<byte>(dataPtr, bufferSize);
                                encoder.WriteFrame(span);
                            }
                            finally
                            {
                                OpenGLNative.glUnmapBuffer(BufferTarget.PixelPackBuffer);
                            }
                        OpenGLNative.glBindBuffer(BufferTarget.PixelPackBuffer, 0);
                    }
                    
                    pboIndex++;
                });
                break;
            }

            default:
            {
                throw new NotSupportedException("Currently only OpenGL is supported");
            }
        }
    }

    public override unsafe void Finish(EncoderBase encoder)
    {
        if (graphicsSurface.Type != GraphicsSurfaceType.OpenGL) return;
        
        var info = Device.GetOpenGLInfo();
        info.ExecuteOnGLThread(() =>
        {
             if (!pboInitialized) return;

            // Process the last frame if any
            if (pboIndex > 0)
            {
                int pendingIndex = (pboIndex - 1) % 2;
                OpenGLNative.glBindBuffer(BufferTarget.PixelPackBuffer, pboIds[pendingIndex]);
                var dataPtr = OpenGLNative.glMapBuffer(BufferTarget.PixelPackBuffer, BufferAccess.ReadOnly);
                if (dataPtr != IntPtr.Zero.ToPointer())
                {
                    try
                    {
                        var span = new ReadOnlySpan<byte>(dataPtr, pboSize);
                        encoder.WriteFrame(span);
                    }
                    finally
                    {
                        OpenGLNative.glUnmapBuffer(BufferTarget.PixelPackBuffer);
                    }
                }
                OpenGLNative.glBindBuffer(BufferTarget.PixelPackBuffer, 0);
            }
            
            OpenGLNative.glDeleteBuffers(1, ref pboIds[0]);
            OpenGLNative.glDeleteBuffers(1, ref pboIds[1]);
            pboInitialized = false;
        });
    }
}
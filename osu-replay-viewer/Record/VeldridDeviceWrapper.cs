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

    private readonly uint[] pboIds = new uint[PboFrameRing.SlotCount];
    private PboFrameRing pboRing;
    private bool pboInitialized = false;
    private int pboSize = 0;

    public static bool IsSupported(IRenderer renderer)
    {
        return renderer.GetType() == VeldridRendererType || renderer.GetType() == DeferredRendererType;
    }

    public VeldridDeviceWrapper(IRenderer renderer, Size desiredSize, PixelFormatMode pixelFormat, ColorSpaceMode colorSpace) : base(desiredSize, pixelFormat, colorSpace)
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
        if (pboInitialized)
        {
            if (pboSize != size) throw new InvalidOperationException("Readback dimensions changed during recording");
            return;
        }

        for (int i = 0; i < pboIds.Length; i++)
        {
            OpenGLNative.glGenBuffers(1, out pboIds[i]);
            OpenGLNative.glBindBuffer(BufferTarget.PixelPackBuffer, pboIds[i]);
            OpenGLNative.glBufferData(BufferTarget.PixelPackBuffer, (UIntPtr)size, IntPtr.Zero.ToPointer(), BufferUsageHint.StreamRead);
        }
        OpenGLNative.glBindBuffer(BufferTarget.PixelPackBuffer, 0);
        pboSize = size;
        pboRing = new PboFrameRing(size, MapPbo, UnmapPbo);
        pboInitialized = true;
    }

    private unsafe IntPtr MapPbo(int slot)
    {
        OpenGLNative.glBindBuffer(BufferTarget.PixelPackBuffer, pboIds[slot]);
        try { return (IntPtr)OpenGLNative.glMapBuffer(BufferTarget.PixelPackBuffer, BufferAccess.ReadOnly); }
        finally { OpenGLNative.glBindBuffer(BufferTarget.PixelPackBuffer, 0); }
    }

    private void UnmapPbo(int slot)
    {
        OpenGLNative.glBindBuffer(BufferTarget.PixelPackBuffer, pboIds[slot]);
        try { OpenGLNative.glUnmapBuffer(BufferTarget.PixelPackBuffer); }
        finally { OpenGLNative.glBindBuffer(BufferTarget.PixelPackBuffer, 0); }
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
                    int oldBuffer, oldAlignment;
                    OpenGLNative.glGetIntegerv(GetPName.PixelPackBufferBinding, &oldBuffer);
                    OpenGLNative.glGetIntegerv(GetPName.PackAlignment, &oldAlignment);
                    InitializePBOs(bufferSize);
                    try
                    {
                        int slot = pboRing.AcquireReadSlot();
                        OpenGLNative.glBindBuffer(BufferTarget.PixelPackBuffer, pboIds[slot]);
                        OpenGLNative.glPixelStorei(PixelStoreParameter.PackAlignment, 1);
                        OpenGLNative.glReadPixels(0, 0, texture.Width, texture.Height,
                            GLPixelFormat.Rgb, GLPixelType.UnsignedByte, IntPtr.Zero.ToPointer());
                        OpenGLNative.glBindBuffer(BufferTarget.PixelPackBuffer, (uint)oldBuffer);
                        pboRing.CaptureCompleted(encoder);
                    }
                    finally
                    {
                        OpenGLNative.glPixelStorei(PixelStoreParameter.PackAlignment, oldAlignment);
                        OpenGLNative.glBindBuffer(BufferTarget.PixelPackBuffer, (uint)oldBuffer);
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

    public override void Finish(EncoderBase encoder)
    {
        if (graphicsSurface.Type != GraphicsSurfaceType.OpenGL || !pboInitialized) return;
        Device.GetOpenGLInfo().ExecuteOnGLThread(() => pboRing.Finish(encoder));
    }

    public override void ReleaseResources()
    {
        if (graphicsSurface.Type != GraphicsSurfaceType.OpenGL || !pboInitialized) return;
        Device.GetOpenGLInfo().ExecuteOnGLThread(() =>
        {
            try { pboRing.ReleaseMappings(); }
            finally
            {
                for (int i = 0; i < pboIds.Length; i++) OpenGLNative.glDeleteBuffers(1, ref pboIds[i]);
                pboInitialized = false;
                pboRing = null;
            }
        });
    }
}

using System;
using System.Drawing;
using System.Reflection;
using osu_replay_renderer_netcore.CustomHosts.Record;
using osu_replay_renderer_netcore.Record.OpenGL;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Platform;
using Veldrid;
using Veldrid.OpenGLBindings;
using BufferTarget = Veldrid.OpenGLBindings.BufferTarget;

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
    
    private readonly OpenGLCapturer Capturer;

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
        
        Capturer = new OpenGLCapturer(new OsuTKOpenGLAdapter(), DesiredSize, PixelFormat);
    }

    public override void WriteFrame(EncoderBase encoder)
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
                var info = Device.GetOpenGLInfo();

                info.ExecuteOnGLThread(() =>
                {
                    Capturer.WriteFrame(encoder);
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
        if (graphicsSurface.Type != GraphicsSurfaceType.OpenGL) return;
        var info = Device.GetOpenGLInfo();

        info.ExecuteOnGLThread(() =>
        {
            Capturer.Finish(encoder);
        });
    }
}
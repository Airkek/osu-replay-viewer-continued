using System;
using System.Drawing;
using System.Reflection;
using osu_replay_renderer_netcore.CustomHosts.Record;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Platform;
using osuTK.Graphics.ES30;

using System.IO;

namespace osu_replay_renderer_netcore.Record;

public class GLRendererWrapper : RenderWrapper
{
    private static readonly Type GLRendererType =
        typeof(IRenderer).Assembly.GetType("osu.Framework.Graphics.OpenGL.GLRenderer");

    private static readonly FieldInfo GLRenderer_openGLSurfaceField = GLRendererType.GetField("openGLSurface",
        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private readonly IGraphicsSurface surface;
    private readonly IOpenGLGraphicsSurface openGLSurface;

    private int shaderProgram;
    private int[] yuvFbos;
    private int[] yuvTextures;
    private int yuvIndex;
    private int sourceTexture;
    private int vao, vbo;
    private bool resourcesInitialized = false;

    private readonly int[] pboIds = new int[PboFrameRing.SlotCount];
    private PboFrameRing pboRing;
    private bool pboInitialized = false;
    private int pboSize = 0;

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

    private (int bufferSize, int fboHeight) GetBufferSizeAndHeight()
    {
        return PixelFormat switch
        {
            PixelFormatMode.YUV444 => (DesiredSize.Width * DesiredSize.Height * 3, DesiredSize.Height * 3),
            PixelFormatMode.NV12 => (DesiredSize.Width * DesiredSize.Height * 3 / 2, DesiredSize.Height * 3 / 2),
            _ => (DesiredSize.Width * DesiredSize.Height * 3 / 2, DesiredSize.Height * 3 / 2) // YUV420
        };
    }

    private int GetPixelFormatUniform()
    {
        return PixelFormat switch
        {
            PixelFormatMode.YUV444 => 1,
            PixelFormatMode.NV12 => 2,
            _ => 0 // YUV420
        };
    }

    private int GetColorSpaceUniform()
    {
        return ColorSpace switch
        {
            ColorSpaceMode.BT601 => 0,
            _ => 1 // BT709
        };
    }

    private void InitializeResources(int textureCount)
    {
        if (resourcesInitialized) return;

        var (bufferSize, fboHeight) = GetBufferSizeAndHeight();

        var basePath = AppDomain.CurrentDomain.BaseDirectory;
        string vertexShaderSource = File.ReadAllText(Path.Combine(basePath, "Record", "Shaders", "yuv_converter.vert"));
        string fragmentShaderSource = File.ReadAllText(Path.Combine(basePath, "Record", "Shaders", "yuv_converter.frag"));

        int vertexShader = GL.CreateShader(ShaderType.VertexShader);
        GL.ShaderSource(vertexShader, vertexShaderSource);
        GL.CompileShader(vertexShader);
        CheckShaderError(vertexShader);

        int fragmentShader = GL.CreateShader(ShaderType.FragmentShader);
        GL.ShaderSource(fragmentShader, fragmentShaderSource);
        GL.CompileShader(fragmentShader);
        CheckShaderError(fragmentShader);

        shaderProgram = GL.CreateProgram();
        GL.AttachShader(shaderProgram, vertexShader);
        GL.AttachShader(shaderProgram, fragmentShader);
        GL.LinkProgram(shaderProgram);

        GL.DeleteShader(vertexShader);
        GL.DeleteShader(fragmentShader);

        Console.WriteLine($"[GLRendererWrapper] Shader loaded for pixel format: {PixelFormat}, color space: {ColorSpace}");

        // Source Texture
        sourceTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, sourceTexture);
        GL.TexImage2D((All)TextureTarget.Texture2D, 0, (All)PixelInternalFormat.Rgb, DesiredSize.Width, DesiredSize.Height, 0, (All)osuTK.Graphics.ES30.PixelFormat.Rgb, (All)PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);

        yuvTextures = new int[textureCount];
        yuvFbos = new int[textureCount];
        for (int i = 0; i < textureCount; i++)
        {
            yuvTextures[i] = GL.GenTexture();
            GL.BindTexture(TextureTarget.Texture2D, yuvTextures[i]);
            GL.TexImage2D((All)TextureTarget.Texture2D, 0, (All)0x8229, DesiredSize.Width, fboHeight, 0, (All)osuTK.Graphics.ES30.PixelFormat.Red, (All)PixelType.UnsignedByte, IntPtr.Zero);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);

            GL.GenFramebuffers(1, out yuvFbos[i]);
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, yuvFbos[i]);
            GL.FramebufferTexture2D((All)FramebufferTarget.Framebuffer, (All)FramebufferAttachment.ColorAttachment0, (All)TextureTarget.Texture2D, yuvTextures[i], 0);
        }
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, 0);

        // Quad
        float[] vertices = {
            -1f, -1f,
             1f, -1f,
            -1f,  1f,
             1f,  1f
        };

        GL.GenVertexArrays(1, out vao);
        GL.BindVertexArray(vao);

        GL.GenBuffers(1, out vbo);
        GL.BindBuffer(BufferTarget.ArrayBuffer, vbo);
        GL.BufferData(BufferTarget.ArrayBuffer, vertices.Length * sizeof(float), vertices, BufferUsageHint.StaticDraw);

        int aPosition = GL.GetAttribLocation(shaderProgram, "aPosition");
        GL.EnableVertexAttribArray(aPosition);
        GL.VertexAttribPointer(aPosition, 2, VertexAttribPointerType.Float, false, 0, 0);

        GL.BindVertexArray(0);
        resourcesInitialized = true;
    }

    private void CheckShaderError(int shader)
    {
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int success);
        if (success == 0)
        {
            string infoLog = GL.GetShaderInfoLog(shader);
            throw new Exception($"Shader compilation failed: {infoLog}");
        }
    }

    public static bool IsSupported(IRenderer renderer)
    {
        return renderer.GetType() == GLRendererType;
    }

    public GLRendererWrapper(IRenderer renderer, Size desiredSize, PixelFormatMode pixelFormat, ColorSpaceMode colorSpace) : base(desiredSize, pixelFormat, colorSpace)
    {
        if (renderer.GetType() != GLRendererType)
            throw new ArgumentException($"Not supported renderer: {renderer.GetType()}");

        var graphicsSurfaceObj = GLRenderer_openGLSurfaceField.GetValue(renderer);
        if (graphicsSurfaceObj is null) throw new Exception("graphicsSurface is null");
        if (graphicsSurfaceObj is not IOpenGLGraphicsSurface or not IGraphicsSurface)
            throw new NotSupportedException("graphicsSurface has unexpected type");

        surface = (IGraphicsSurface)graphicsSurfaceObj;
        openGLSurface = (IOpenGLGraphicsSurface)graphicsSurfaceObj;

        Console.WriteLine($"[GLRendererWrapper] Initialized with pixel format: {pixelFormat}, color space: {colorSpace}");
    }

    public override bool WriteFrame(EncoderBase encoder)
    {
        var size = surface.GetDrawableSize();
        if (size.Width != DesiredSize.Width || size.Height != DesiredSize.Height) return false;

        WithGLContext(() =>
        {
            if (PixelFormat == PixelFormatMode.RGB)
            {
                WriteFrameRGB(encoder, size);
            }
            else
            {
                WriteFrameYUV(encoder, size);
            }
        });
        return true;
    }

    private unsafe void WriteFrameYUV(EncoderBase encoder, Size size)
    {
        var textureEncoder = encoder as IOpenGLTextureEncoder;
        if (textureEncoder?.AcceptsOpenGLTexture(PixelFormat) != true) textureEncoder = null;
        var (bufferSize, fboHeight) = GetBufferSizeAndHeight();

        // Save state
        GL.GetInteger(GetPName.DrawFramebufferBinding, out int oldFbo);
        GL.GetInteger(GetPName.TextureBinding2D, out int oldTexture);
        GL.GetInteger(GetPName.CurrentProgram, out int oldProgram);
        GL.GetInteger(GetPName.ActiveTexture, out int oldActiveTexture);
        int[] oldViewport = new int[4];
        GL.GetInteger(GetPName.Viewport, oldViewport);

        bool scissorEnabled = GL.IsEnabled(EnableCap.ScissorTest);
        bool blendEnabled = GL.IsEnabled(EnableCap.Blend);
        bool depthTestEnabled = GL.IsEnabled(EnableCap.DepthTest);
        bool cullFaceEnabled = GL.IsEnabled(EnableCap.CullFace);

        try
        {
            InitializeResources(textureEncoder == null ? 1 : Math.Clamp(textureEncoder.TextureBufferCount, 2, 3));
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, oldFbo);
            int yuvTexture = yuvTextures[yuvIndex];
            int yuvFbo = yuvFbos[yuvIndex];
            textureEncoder?.PrepareOpenGLTexture(yuvTexture);

            // 1. Copy current framebuffer to sourceTexture
            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, sourceTexture);

            // Ensure no error before we start
            while (GL.GetError() != ErrorCode.NoError) {}

            GL.CopyTexSubImage2D((All)TextureTarget.Texture2D, 0, 0, 0, 0, 0, DesiredSize.Width, DesiredSize.Height);

            var err = GL.GetError();
            if (err != ErrorCode.NoError) Console.WriteLine($"GL Error after CopyTexSubImage2D: {err}");

            // 2. Render to YUV FBO
            GL.BindFramebuffer(FramebufferTarget.Framebuffer, yuvFbo);

            if (GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer) != FramebufferErrorCode.FramebufferComplete)
            {
                 Console.WriteLine($"Framebuffer incomplete: {GL.CheckFramebufferStatus(FramebufferTarget.Framebuffer)}");
            }

            GL.Viewport(0, 0, DesiredSize.Width, fboHeight);

            GL.Disable(EnableCap.ScissorTest);
            GL.Disable(EnableCap.Blend);
            GL.Disable(EnableCap.DepthTest);
            GL.Disable(EnableCap.CullFace);
            GL.ColorMask(true, true, true, true);

            GL.UseProgram(shaderProgram);

            GL.ActiveTexture(TextureUnit.Texture0);
            GL.BindTexture(TextureTarget.Texture2D, sourceTexture);
            GL.Uniform1(GL.GetUniformLocation(shaderProgram, "uTexture"), 0);
            GL.Uniform2(GL.GetUniformLocation(shaderProgram, "uResolution"), (float)DesiredSize.Width, (float)DesiredSize.Height);
            GL.Uniform1(GL.GetUniformLocation(shaderProgram, "uPixelFormat"), GetPixelFormatUniform());
            GL.Uniform1(GL.GetUniformLocation(shaderProgram, "uColorSpace"), GetColorSpaceUniform());

            GL.BindVertexArray(vao);
            GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

            if (textureEncoder != null)
            {
                GL.Flush();
                textureEncoder.WriteOpenGLTexture(yuvTexture, DesiredSize.Width, fboHeight, PixelFormat);
                yuvIndex = (yuvIndex + 1) % yuvTextures.Length;
                return;
            }

            ReadbackFrame(encoder, DesiredSize.Width, fboHeight, osuTK.Graphics.ES30.PixelFormat.Red, bufferSize);
        }
        finally
        {
            RestoreState(oldFbo, oldTexture, oldProgram, oldActiveTexture, oldViewport, scissorEnabled, blendEnabled, depthTestEnabled, cullFaceEnabled);
        }
    }

    private void RestoreState(int oldFbo, int oldTexture, int oldProgram, int oldActiveTexture, int[] oldViewport, bool scissorEnabled, bool blendEnabled, bool depthTestEnabled, bool cullFaceEnabled)
    {
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, oldFbo);
        GL.ActiveTexture((TextureUnit)oldActiveTexture);
        GL.BindTexture(TextureTarget.Texture2D, oldTexture);
        GL.UseProgram(oldProgram);
        if (oldViewport != null && oldViewport.Length == 4) GL.Viewport(oldViewport[0], oldViewport[1], oldViewport[2], oldViewport[3]);

        if (scissorEnabled) GL.Enable(EnableCap.ScissorTest);
        if (blendEnabled) GL.Enable(EnableCap.Blend);
        if (depthTestEnabled) GL.Enable(EnableCap.DepthTest);
        if (cullFaceEnabled) GL.Enable(EnableCap.CullFace);
    }

    private void WriteFrameRGB(EncoderBase encoder, Size size)
    {
        ReadbackFrame(encoder, size.Width, size.Height, osuTK.Graphics.ES30.PixelFormat.Rgb,
            checked(DesiredSize.Width * DesiredSize.Height * 3));
    }

    private void ReadbackFrame(EncoderBase encoder, int width, int height, osuTK.Graphics.ES30.PixelFormat format, int bufferSize)
    {
        GL.GetInteger(GetPName.PixelPackBufferBinding, out int oldBuffer);
        GL.GetInteger(GetPName.PackAlignment, out int oldAlignment);
        InitializePBOs(bufferSize);
        try
        {
            int slot = pboRing.AcquireReadSlot();
            GL.BindBuffer(BufferTarget.PixelPackBuffer, pboIds[slot]);
            GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
            GL.ReadPixels(0, 0, width, height, format, PixelType.UnsignedByte, IntPtr.Zero);
            GL.BindBuffer(BufferTarget.PixelPackBuffer, oldBuffer);
            pboRing.CaptureCompleted(encoder);
        }
        finally
        {
            GL.PixelStore(PixelStoreParameter.PackAlignment, oldAlignment);
            GL.BindBuffer(BufferTarget.PixelPackBuffer, oldBuffer);
        }
    }

    private IntPtr MapPbo(int slot)
    {
        GL.BindBuffer(BufferTarget.PixelPackBuffer, pboIds[slot]);
        try
        {
            return GL.MapBufferRange(BufferTarget.PixelPackBuffer, IntPtr.Zero, pboSize, BufferAccessMask.MapReadBit);
        }
        finally { GL.BindBuffer(BufferTarget.PixelPackBuffer, 0); }
    }

    private void UnmapPbo(int slot)
    {
        GL.BindBuffer(BufferTarget.PixelPackBuffer, pboIds[slot]);
        try
        {
            if (!GL.UnmapBuffer(BufferTarget.PixelPackBuffer))
                throw new InvalidOperationException("OpenGL readback buffer contents became invalid");
        }
        finally { GL.BindBuffer(BufferTarget.PixelPackBuffer, 0); }
    }

    private void InitializePBOs(int size)
    {
        if (pboInitialized)
        {
            if (pboSize != size) throw new InvalidOperationException("Readback dimensions changed during recording");
            return;
        }

        GL.GenBuffers(pboIds.Length, pboIds);
        for (int i = 0; i < pboIds.Length; i++)
        {
            GL.BindBuffer(BufferTarget.PixelPackBuffer, pboIds[i]);
            GL.BufferData(BufferTarget.PixelPackBuffer, size, IntPtr.Zero, BufferUsageHint.StreamRead);
        }
        GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
        pboSize = size;
        pboRing = new PboFrameRing(size, MapPbo, UnmapPbo);
        pboInitialized = true;
    }

    public override void Finish(EncoderBase encoder)
    {
        if (pboInitialized) WithGLContext(() => pboRing.Finish(encoder));
    }

    public override void ReleaseResources()
    {
        if (!pboInitialized && !resourcesInitialized) return;
        WithGLContext(() =>
        {
            try { pboRing?.ReleaseMappings(); }
            finally
            {
                if (pboInitialized)
                {
                    GL.DeleteBuffers(pboIds.Length, pboIds);
                    pboInitialized = false;
                    pboRing = null;
                }
                if (resourcesInitialized)
                {
                    GL.DeleteFramebuffers(yuvFbos.Length, yuvFbos);
                    GL.DeleteTextures(yuvTextures.Length, yuvTextures);
                    GL.DeleteTextures(1, new[] { sourceTexture });
                    GL.DeleteBuffers(1, new[] { vbo });
                    GL.DeleteVertexArrays(1, new[] { vao });
                    GL.DeleteProgram(shaderProgram);
                    resourcesInitialized = false;
                }
            }
        });
    }
}

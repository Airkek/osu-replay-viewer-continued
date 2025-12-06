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
    private int yuvFbo;
    private int yuvTexture;
    private int sourceTexture;
    private int vao, vbo;
    private bool resourcesInitialized = false;

    private int[] pboIds = new int[2];
    private int pboIndex = 0;
    private bool pboInitialized = false;
    private int pboSize = 0;

    private void InitializeResources()
    {
        if (resourcesInitialized) return;

        var basePath = AppDomain.CurrentDomain.BaseDirectory;
        string vertexShaderSource = File.ReadAllText(Path.Combine(basePath, "Record", "Shaders", "rgb_to_yuv.vert"));
        string fragmentShaderSource = File.ReadAllText(Path.Combine(basePath, "Record", "Shaders", "rgb_to_yuv.frag"));

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

        // Source Texture
        sourceTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, sourceTexture);
        GL.TexImage2D((All)TextureTarget.Texture2D, 0, (All)PixelInternalFormat.Rgb, DesiredSize.Width, DesiredSize.Height, 0, (All)osuTK.Graphics.ES30.PixelFormat.Rgb, (All)PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);

        // YUV Texture
        yuvTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, yuvTexture);
        // GL_R8 = 0x8229
        GL.TexImage2D((All)TextureTarget.Texture2D, 0, (All)0x8229, DesiredSize.Width, DesiredSize.Height * 3 / 2, 0, (All)osuTK.Graphics.ES30.PixelFormat.Red, (All)PixelType.UnsignedByte, IntPtr.Zero);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);

        // FBO
        GL.GenFramebuffers(1, out yuvFbo);
        GL.BindFramebuffer(FramebufferTarget.Framebuffer, yuvFbo);
        GL.FramebufferTexture2D((All)FramebufferTarget.Framebuffer, (All)FramebufferAttachment.ColorAttachment0, (All)TextureTarget.Texture2D, yuvTexture, 0);
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
    }

    public override void WriteFrame(EncoderBase encoder)
    {
        var size = surface.GetDrawableSize();
        if (size.Width != DesiredSize.Width || size.Height != DesiredSize.Height) return;

        if (PixelFormat == PixelFormatMode.RGB)
        {
            WriteFrameRGB(encoder, size);
        }
        else
        {
            WriteFrameYUV(encoder, size);
        }
    }

    private unsafe void WriteFrameYUV(EncoderBase encoder, Size size)
    {
        InitializeResources();

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
        
        GL.Viewport(0, 0, DesiredSize.Width, DesiredSize.Height * 3 / 2);
        
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

        GL.BindVertexArray(vao);
        GL.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);

        // 3. ReadPixels
        var bufferSize = DesiredSize.Width * (DesiredSize.Height * 3 / 2);
        InitializePBOs(bufferSize);

        int index = pboIndex % 2;
        int nextIndex = (pboIndex + 1) % 2;

        GL.BindBuffer(BufferTarget.PixelPackBuffer, pboIds[index]);
        GL.PixelStore(PixelStoreParameter.PackAlignment, 1);
        GL.ReadPixels(0, 0, DesiredSize.Width, DesiredSize.Height * 3 / 2, osuTK.Graphics.ES30.PixelFormat.Red, PixelType.UnsignedByte, IntPtr.Zero);
        GL.PixelStore(PixelStoreParameter.PackAlignment, 4); // Restore default
        GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);

        if (pboIndex > 0)
        {
            GL.BindBuffer(BufferTarget.PixelPackBuffer, pboIds[nextIndex]);
            var dataPtr = GL.MapBufferRange(BufferTarget.PixelPackBuffer, IntPtr.Zero, bufferSize, BufferAccessMask.MapReadBit);

            if (dataPtr != IntPtr.Zero)
            {
                try
                {
                    var span = new ReadOnlySpan<byte>(dataPtr.ToPointer(), bufferSize);
                    encoder.WriteFrame(span);
                }
                finally
                {
                    GL.UnmapBuffer(BufferTarget.PixelPackBuffer);
                }
            }
            GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
        }
        
        pboIndex++;

        // Restore state
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

    private unsafe void WriteFrameRGB(EncoderBase encoder, Size size)
    {
        var bufferSize = DesiredSize.Width * DesiredSize.Height * 3;
        InitializePBOs(bufferSize);

        int index = pboIndex % 2;
        int nextIndex = (pboIndex + 1) % 2;

        // Read pixels into current PBO
        GL.BindBuffer(BufferTarget.PixelPackBuffer, pboIds[index]);
        GL.ReadPixels(
            0, 0,
            size.Width, size.Height,
            osuTK.Graphics.ES30.PixelFormat.Rgb,
            PixelType.UnsignedByte,
            IntPtr.Zero);
        GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);

        // Process previous PBO
        if (pboIndex > 0)
        {
            GL.BindBuffer(BufferTarget.PixelPackBuffer, pboIds[nextIndex]);
            var dataPtr = GL.MapBufferRange(BufferTarget.PixelPackBuffer,
                IntPtr.Zero,
                bufferSize,
                BufferAccessMask.MapReadBit);

            if (dataPtr != IntPtr.Zero)
                try
                {
                    var span = new ReadOnlySpan<byte>(dataPtr.ToPointer(), bufferSize);
                    encoder.WriteFrame(span);
                }
                finally
                {
                    GL.UnmapBuffer(BufferTarget.PixelPackBuffer);
                }
            GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
        }

        pboIndex++;
    }

    private void InitializePBOs(int size)
    {
        if (pboInitialized && pboSize == size) return;
        
        if (pboInitialized)
        {
            GL.DeleteBuffers(2, pboIds);
        }

        GL.GenBuffers(2, pboIds);
        for (int i = 0; i < 2; i++)
        {
            GL.BindBuffer(BufferTarget.PixelPackBuffer, pboIds[i]);
            GL.BufferData(BufferTarget.PixelPackBuffer, size, IntPtr.Zero, BufferUsageHint.StreamRead);
        }
        GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
        pboInitialized = true;
        pboSize = size;
    }

    public override unsafe void Finish(EncoderBase encoder)
    {
        if (!pboInitialized) return;

        // Process the last frame if any
        if (pboIndex > 0)
        {
            int pendingIndex = (pboIndex - 1) % 2;
            GL.BindBuffer(BufferTarget.PixelPackBuffer, pboIds[pendingIndex]);
            var dataPtr = GL.MapBufferRange(BufferTarget.PixelPackBuffer, IntPtr.Zero, pboSize, BufferAccessMask.MapReadBit);
            if (dataPtr != IntPtr.Zero)
            {
                try
                {
                    var span = new ReadOnlySpan<byte>(dataPtr.ToPointer(), pboSize);
                    encoder.WriteFrame(span);
                }
                finally
                {
                    GL.UnmapBuffer(BufferTarget.PixelPackBuffer);
                }
            }
            GL.BindBuffer(BufferTarget.PixelPackBuffer, 0);
        }
        
        GL.DeleteBuffers(2, pboIds);
        pboInitialized = false;
    }
}
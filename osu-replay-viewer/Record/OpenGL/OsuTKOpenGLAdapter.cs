using osuTK.Graphics.ES30;

namespace osu_replay_renderer_netcore.Record.OpenGL;

public class OsuTKOpenGLAdapter : IOpenGLAdapter
{
    // Shader operations
    public int CreateShader(ShaderType type)
        => GL.CreateShader((osuTK.Graphics.ES30.ShaderType)type);

    public void ShaderSource(int shader, string source)
        => GL.ShaderSource(shader, 1, new[] { source }, new[] { source.Length });

    public void CompileShader(int shader)
        => GL.CompileShader(shader);

    public void GetShader(int shader, ShaderParameter parameter, out int success)
        => GL.GetShader(shader, (osuTK.Graphics.ES30.ShaderParameter)parameter, out success);

    public string GetShaderInfoLog(int shader) 
        => GL.GetShaderInfoLog(shader);

    // Program operations
    public int CreateProgram()
        => GL.CreateProgram();

    public void AttachShader(int program, int shader)
        => GL.AttachShader(program, shader);

    public void LinkProgram(int program)
        => GL.LinkProgram(program);

    public void DeleteShader(int shader)
        => GL.DeleteShader(shader);

    public void UseProgram(int program)
        => GL.UseProgram(program);

    public int GetUniformLocation(int program, string name)
        => GL.GetUniformLocation(program, name);

    // Texture operations
    public int GenTexture()
    {
        GL.GenTextures(1, out int texture);
        return texture;
    }

    public void BindTexture(TextureTarget target, int texture)
        => GL.BindTexture((osuTK.Graphics.ES30.TextureTarget)target, texture);

    public void TexImage2D(TextureTarget target, int level, PixelInternalFormat internalFormat, int width, int height, int border, PixelFormat format, PixelType type, System.IntPtr pixels)
        => GL.TexImage2D(
            (osuTK.Graphics.ES30.TextureTarget2d)target,
            level,
            ToTextureComponentCount(internalFormat),
            width,
            height,
            border,
            ToEs30PixelFormat(format),
            ToEs30PixelType(type),
            pixels);

    private static osuTK.Graphics.ES30.TextureComponentCount ToTextureComponentCount(PixelInternalFormat format)
        => format switch
        {
            PixelInternalFormat.Rgb => osuTK.Graphics.ES30.TextureComponentCount.Rgb,
            PixelInternalFormat.Rgba => osuTK.Graphics.ES30.TextureComponentCount.Rgba,
            _ => osuTK.Graphics.ES30.TextureComponentCount.Rgb
        };

    private static osuTK.Graphics.ES30.PixelFormat ToEs30PixelFormat(PixelFormat format)
        => format switch
        {
            PixelFormat.Red => osuTK.Graphics.ES30.PixelFormat.Red,
            PixelFormat.Rgb => osuTK.Graphics.ES30.PixelFormat.Rgb,
            PixelFormat.Rgba => osuTK.Graphics.ES30.PixelFormat.Rgba,
            _ => osuTK.Graphics.ES30.PixelFormat.Rgb
        };

    private static osuTK.Graphics.ES30.PixelType ToEs30PixelType(PixelType type)
        => type switch
        {
            PixelType.Byte => osuTK.Graphics.ES30.PixelType.Byte,
            PixelType.UnsignedByte => osuTK.Graphics.ES30.PixelType.UnsignedByte,
            PixelType.Short => osuTK.Graphics.ES30.PixelType.Short,
            PixelType.UnsignedShort => osuTK.Graphics.ES30.PixelType.UnsignedShort,
            PixelType.Int => osuTK.Graphics.ES30.PixelType.Int,
            PixelType.UnsignedInt => osuTK.Graphics.ES30.PixelType.UnsignedInt,
            PixelType.Float => osuTK.Graphics.ES30.PixelType.Float,
            _ => osuTK.Graphics.ES30.PixelType.UnsignedByte
        };

    public void TexParameter(TextureTarget target, TextureParameterName pname, int param)
        => GL.TexParameter(
            (osuTK.Graphics.ES30.TextureTarget)target,
            (osuTK.Graphics.ES30.TextureParameterName)pname,
            param);

    public void ActiveTexture(TextureUnit textureUnit)
        => GL.ActiveTexture((osuTK.Graphics.ES30.TextureUnit)textureUnit);

    // Framebuffer operations
    public void GenFramebuffers(int n, out int framebuffer)
    {
        GL.GenFramebuffers(n, out framebuffer);
    }

    public void BindFramebuffer(FramebufferTarget target, int framebuffer)
        => GL.BindFramebuffer((osuTK.Graphics.ES30.FramebufferTarget)target, framebuffer);

    public void FramebufferTexture2D(FramebufferTarget target, FramebufferAttachment attachment, TextureTarget textarget, int texture, int level)
        => GL.FramebufferTexture2D(
            osuTK.Graphics.ES30.FramebufferTarget.Framebuffer,
            (osuTK.Graphics.ES30.FramebufferAttachment)attachment,
            osuTK.Graphics.ES30.TextureTarget2d.Texture2D,
            texture,
            level);

    public FramebufferErrorCode CheckFramebufferStatus(FramebufferTarget target)
        => (FramebufferErrorCode)GL.CheckFramebufferStatus((osuTK.Graphics.ES30.FramebufferTarget)target);

    // Vertex array operations
    public void GenVertexArrays(int n, out int array)
        => GL.GenVertexArrays(n, out array);

    public void BindVertexArray(int array)
        => GL.BindVertexArray(array);

    public int GetAttribLocation(int program, string name)
        => GL.GetAttribLocation(program, name);

    public void EnableVertexAttribArray(int index)
        => GL.EnableVertexAttribArray(index);

    public void VertexAttribPointer(int index, int size, VertexAttribPointerType type, bool normalized, int stride, int offset)
        => GL.VertexAttribPointer(index, size, (osuTK.Graphics.ES30.VertexAttribPointerType)type, normalized, stride, offset);

    // Buffer operations
    public void GenBuffers(int n, int[] buffers)
        => GL.GenBuffers(n, buffers);
    
     public void GenBuffers(int n, out int buffers)
            => GL.GenBuffers(n, out buffers);

    public void BindBuffer(BufferTarget target, int buffer)
        => GL.BindBuffer((osuTK.Graphics.ES30.BufferTarget)target, buffer);

    public void BufferData(BufferTarget target, int size, System.IntPtr data, BufferUsageHint usage)
        => GL.BufferData((osuTK.Graphics.ES30.BufferTarget)target, size, data, (osuTK.Graphics.ES30.BufferUsageHint)usage);

    public void BufferData(BufferTarget target, int size, float[] data, BufferUsageHint usage)
    => GL.BufferData((osuTK.Graphics.ES30.BufferTarget)target, size, data, (osuTK.Graphics.ES30.BufferUsageHint)usage);

    public void DeleteBuffers(int n, int[] buffers)
        => GL.DeleteBuffers(n, buffers);

    // Get operations
    public void GetInteger(GetPName pname, out int data)
        => GL.GetInteger((osuTK.Graphics.ES30.GetPName)pname, out data);

    public void GetInteger(GetPName pname, int[] data)
        => GL.GetInteger((osuTK.Graphics.ES30.GetPName)pname, data);

    // State operations
    public bool IsEnabled(EnableCap cap)
        => GL.IsEnabled((osuTK.Graphics.ES30.EnableCap)cap);

    public void Disable(EnableCap cap)
        => GL.Disable((osuTK.Graphics.ES30.EnableCap)cap);

    public void ColorMask(bool red, bool green, bool blue, bool alpha)
        => GL.ColorMask(red, green, blue, alpha);

    public ErrorCode GetError()
        => (ErrorCode)GL.GetError();

    // Draw operations
    public void DrawArrays(PrimitiveType mode, int first, int count)
        => GL.DrawArrays((osuTK.Graphics.ES30.PrimitiveType)mode, first, count);

    public void Viewport(int x, int y, int width, int height)
        => GL.Viewport(x, y, width, height);

    // Copy operations
    public void CopyTexSubImage2D(TextureTarget target, int level, int xoffset, int yoffset, int x, int y, int width, int height)
        => GL.CopyTexSubImage2D(
            osuTK.Graphics.ES30.TextureTarget2d.Texture2D,
            level,
            xoffset,
            yoffset,
            x,
            y,
            width,
            height);

    // Pixel operations
    public void PixelStore(PixelStoreParameter pname, int param)
        => GL.PixelStore((osuTK.Graphics.ES30.PixelStoreParameter)pname, param);

    public void ReadPixels(int x, int y, int width, int height, PixelFormat format, PixelType type, System.IntPtr pixels)
        => GL.ReadPixels(x, y, width, height, (osuTK.Graphics.ES30.PixelFormat)format, (osuTK.Graphics.ES30.PixelType)type, pixels);

    public System.IntPtr MapBufferRange(BufferTarget target, System.IntPtr offset, int length, BufferAccessMask access)
        => GL.MapBufferRange((osuTK.Graphics.ES30.BufferTarget)target, offset, length, (osuTK.Graphics.ES30.BufferAccessMask)access);

    public bool UnmapBuffer(BufferTarget target)
        => GL.UnmapBuffer((osuTK.Graphics.ES30.BufferTarget)target);

    // Uniform operations
    public void Uniform1(int location, int v0)
        => GL.Uniform1(location, v0);

    public void Enable(EnableCap cap) => GL.Enable((osuTK.Graphics.ES30.EnableCap)cap);
}
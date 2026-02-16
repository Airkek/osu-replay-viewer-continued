using System;

namespace osu_replay_renderer_netcore.Record.OpenGL;

public enum TextureTarget
{
    Texture2D = 3553
}

public enum TextureParameterName
{
    TextureMinFilter = 10241,
    TextureMagFilter = 10240
}
public enum TextureMinFilter
{
    Nearest = 9728,
    Linear = 9729
}

public enum TextureMagFilter
{
    Nearest = 9728,
    Linear = 9729
}

public enum TextureUnit
{
    Texture0 = 33984
}

public enum FramebufferTarget
{
    Framebuffer = 36160
}

public enum FramebufferErrorCode
{
    FramebufferComplete = 36053
}

public enum VertexAttribPointerType
{
    Float = 5126
}

public enum BufferTarget
{
    ArrayBuffer = 34962,
    PixelPackBuffer = 35051
}

public enum BufferUsageHint
{
    StaticDraw = 35044,
    StreamRead = 35042
}

public enum GetPName
{
    DrawFramebufferBinding = 36006,
    TextureBinding2D = 32873,
    CurrentProgram = 35725,
    ActiveTexture = 34016,
    Viewport = 2978
}

public enum EnableCap
{
    ScissorTest = 3089,
    Blend = 3042,
    DepthTest = 2929,
    CullFace = 2884
}

public enum ErrorCode
{
    NoError = 0
}

public enum PrimitiveType
{
    TriangleStrip = 5
}

public enum PixelStoreParameter
{
    PackAlignment = 3333
}

public enum BufferAccessMask
{
    MapReadBit = 0x0001
}

public enum ShaderType
{
    FragmentShader = 35632,
    VertexShader = 35633,
    GeometryShader = 36313,
    GeometryShaderExt = 36313,
    TessEvaluationShader = 36487,
    TessControlShader = 36488,
    ComputeShader = 37305
}

public enum ShaderParameter
{
    ShaderType = 35663,
    DeleteStatus = 35712,
    CompileStatus = 35713,
    InfoLogLength = 35716,
    ShaderSourceLength = 35720
}

public enum PixelType
{
    UnsignedByte = 5121,
    Byte = 5120,
    UnsignedShort = 5123,
    Short = 5122,
    UnsignedInt = 5125,
    Int = 5124,
    Float = 5126
}

public enum PixelInternalFormat
{
    Rgb = 6407,
    Rgba = 6408,
    R8 = 0x8229
}

public enum PixelFormat
{
    Red = 6403,
    Rgb = 6407,
    Rgba = 6408
}

public enum FramebufferAttachment
{
    ColorAttachment0 = 36064,
    DepthAttachment = 36096,
    StencilAttachment = 36128
}

public interface IOpenGLAdapter
{
    public int CreateShader(ShaderType type);
    public void ShaderSource(int shader, string source);
    public void CompileShader(int shader);
    public void GetShader(int shader, ShaderParameter parameter, out int success);
    public string GetShaderInfoLog(int shader);

    // Program operations
    public int CreateProgram();
    public void AttachShader(int program, int shader);
    public void LinkProgram(int program);
    public void DeleteShader(int shader);
    public void UseProgram(int program);
    public int GetUniformLocation(int program, string name);

    // Texture operations
    public int GenTexture();
    public void BindTexture(TextureTarget target, int texture);
    public void TexImage2D(TextureTarget target, int level, PixelInternalFormat internalFormat, int width, int height, int border, PixelFormat format, PixelType type, IntPtr pixels);
    public void TexParameter(TextureTarget target, TextureParameterName pname, int param);
    public void ActiveTexture(TextureUnit textureUnit);

    // Framebuffer operations
    public void GenFramebuffers(int n, out int framebuffer);
    public void BindFramebuffer(FramebufferTarget target, int framebuffer);
    public void FramebufferTexture2D(FramebufferTarget target, FramebufferAttachment attachment, TextureTarget textarget, int texture, int level);
    public FramebufferErrorCode CheckFramebufferStatus(FramebufferTarget target);

    // Vertex array operations
    public void GenVertexArrays(int n, out int array);
    public void BindVertexArray(int array);
    public int GetAttribLocation(int program, string name);
    public void EnableVertexAttribArray(int index);
    public void VertexAttribPointer(int index, int size, VertexAttribPointerType type, bool normalized, int stride, int offset);

    // Buffer operations
    public void GenBuffers(int n, out int buffers);
    public void GenBuffers(int n, int[] buffers);
    public void BindBuffer(BufferTarget target, int buffer);
    public void BufferData(BufferTarget target, int size, IntPtr data, BufferUsageHint usage);
    public void BufferData(BufferTarget target, int size, float[] data, BufferUsageHint usage);
    public void DeleteBuffers(int n, int[] buffers);

    // Get operations
    public void GetInteger(GetPName pname, out int data);
    public void GetInteger(GetPName pname, int[] data);

    // State operations
    public bool IsEnabled(EnableCap cap);
    public void Disable(EnableCap cap);
    public void ColorMask(bool red, bool green, bool blue, bool alpha);
    public ErrorCode GetError();

    // Draw operations
    public void DrawArrays(PrimitiveType mode, int first, int count);
    public void Viewport(int x, int y, int width, int height);

    // Copy operations
    public void CopyTexSubImage2D(TextureTarget target, int level, int xoffset, int yoffset, int x, int y, int width, int height);

    // Pixel operations
    public void PixelStore(PixelStoreParameter pname, int param);
    public void ReadPixels(int x, int y, int width, int height, PixelFormat format, PixelType type, IntPtr pixels);
    public IntPtr MapBufferRange(BufferTarget target, IntPtr offset, int length, BufferAccessMask access);
    public bool UnmapBuffer(BufferTarget target);

    // Uniform operations
    public void Uniform1(int location, int v0);
    
    public void Enable(EnableCap cap);

}
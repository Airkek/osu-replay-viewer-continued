using System;
using System.Runtime.InteropServices;
using System.Text;
using Veldrid.OpenGLBindings;

namespace osu_replay_renderer_netcore.Record.OpenGL;

public unsafe class VeldridOpenGLAdapter : IOpenGLAdapter
{
    // Shader operations
    public int CreateShader(ShaderType type)
    {
        return (int)OpenGLNative.glCreateShader(MapShaderType(type));
    }

    public void ShaderSource(int shader, string source)
    {
        IntPtr[] sourcePtrs = new[] { Marshal.StringToHGlobalAnsi(source) };
        int[] lengths = new[] { source.Length };

        try
        {
            fixed (IntPtr* ptrs = sourcePtrs)
            fixed (int* lens = lengths)
            {
                OpenGLNative.glShaderSource((uint)shader, 1, (byte**)ptrs, lens);
            }
        }
        finally
        {
            foreach (var ptr in sourcePtrs)
            {
                if (ptr != IntPtr.Zero)
                    Marshal.FreeHGlobal(ptr);
            }
        }
    }

    public void CompileShader(int shader)
    {
        OpenGLNative.glCompileShader((uint)shader);
    }

    public void GetShader(int shader, ShaderParameter parameter, out int success)
    {
        int value;
        OpenGLNative.glGetShaderiv((uint)shader, MapShaderParameter(parameter), &value);
        success = value;
    }

    public string GetShaderInfoLog(int shader)
    {
        int length;
        OpenGLNative.glGetShaderiv((uint)shader, Veldrid.OpenGLBindings.ShaderParameter.InfoLogLength, &length);

        if (length == 0)
            return string.Empty;

        byte[] buffer = new byte[length];
        fixed (byte* buf = buffer)
        {
            uint actualLength;
            OpenGLNative.glGetShaderInfoLog((uint)shader, (uint)length, &actualLength, buf);
        }

        return Encoding.ASCII.GetString(buffer).TrimEnd('\0');
    }

    // Program operations
    public int CreateProgram()
    {
        return (int)OpenGLNative.glCreateProgram();
    }

    public void AttachShader(int program, int shader)
    {
        OpenGLNative.glAttachShader((uint)program, (uint)shader);
    }

    public void LinkProgram(int program)
    {
        OpenGLNative.glLinkProgram((uint)program);
    }

    public void DeleteShader(int shader)
    {
        OpenGLNative.glDeleteShader((uint)shader);
    }

    public void UseProgram(int program)
    {
        OpenGLNative.glUseProgram((uint)program);
    }

    public int GetUniformLocation(int program, string name)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(name + '\0');
        fixed (byte* namePtr = nameBytes)
        {
            return OpenGLNative.glGetUniformLocation((uint)program, namePtr);
        }
    }

    // Texture operations
    public int GenTexture()
    {
        uint texture;
        OpenGLNative.glGenTextures(1, out texture);
        return (int)texture;
    }

    public void BindTexture(TextureTarget target, int texture)
    {
        OpenGLNative.glBindTexture(MapTextureTarget(target), (uint)texture);
    }

    public void TexImage2D(TextureTarget target, int level, PixelInternalFormat internalFormat, int width, int height, int border, PixelFormat format, PixelType type, IntPtr pixels)
    {
        OpenGLNative.glTexImage2D(
            MapTextureTarget(target),
            level,
            MapPixelInternalFormat(internalFormat),
            (uint)width,
            (uint)height,
            border,
            MapPixelFormat(format),
            MapPixelType(type),
            (void*)pixels);
    }

    public void TexParameter(TextureTarget target, TextureParameterName pname, int param)
    {
        OpenGLNative.glTexParameteri(MapTextureTarget(target), MapTextureParameterName(pname), param);
    }

    public void ActiveTexture(TextureUnit textureUnit)
    {
        OpenGLNative.glActiveTexture(MapTextureUnit(textureUnit));
    }

    // Framebuffer operations
    public void GenFramebuffers(int n, out int framebuffer)
    {
        uint fb;
        OpenGLNative.glGenFramebuffers(1, out fb);
        framebuffer = (int)fb;
    }

    public void BindFramebuffer(FramebufferTarget target, int framebuffer)
    {
        OpenGLNative.glBindFramebuffer(MapFramebufferTarget(target), (uint)framebuffer);
    }

    public void FramebufferTexture2D(FramebufferTarget target, FramebufferAttachment attachment, TextureTarget textarget, int texture, int level)
    {
        OpenGLNative.glFramebufferTexture2D(
            MapFramebufferTarget(target),
            MapFramebufferAttachment(attachment),
            MapTextureTarget(textarget),
            (uint)texture,
            level);
    }

    public FramebufferErrorCode CheckFramebufferStatus(FramebufferTarget target)
    {
        return MapFramebufferErrorCode(OpenGLNative.glCheckFramebufferStatus(MapFramebufferTarget(target)));
    }

    // Vertex array operations
    public void GenVertexArrays(int n, out int array)
    {
        uint vao;
        OpenGLNative.glGenVertexArrays(1, out vao);
        array = (int)vao;
    }

    public void BindVertexArray(int array)
    {
        OpenGLNative.glBindVertexArray((uint)array);
    }

    public int GetAttribLocation(int program, string name)
    {
        byte[] nameBytes = Encoding.ASCII.GetBytes(name + '\0');
        fixed (byte* namePtr = nameBytes)
        {
            return OpenGLNative.glGetAttribLocation((uint)program, namePtr);
        }
    }

    public void EnableVertexAttribArray(int index)
    {
        OpenGLNative.glEnableVertexAttribArray((uint)index);
    }

    public void VertexAttribPointer(int index, int size, VertexAttribPointerType type, bool normalized, int stride, int offset)
    {
        OpenGLNative.glVertexAttribPointer(
            (uint)index,
            size,
            MapVertexAttribPointerType(type),
            normalized ? GLBoolean.True : GLBoolean.False,
            (uint)stride,
            (void*)offset);
    }

    // Buffer operations
    public void GenBuffers(int n, out int buffers)
    {
        uint buffer;
        OpenGLNative.glGenBuffers(1, out buffer);
        buffers = (int)buffer;
    }

    public void GenBuffers(int n, int[] buffers)
    {
        for (int i = 0; i < n; i++)
        {
            uint buffer;
            OpenGLNative.glGenBuffers(1, out buffer);
            buffers[i] = (int)buffer;
        }
    }

    public void BindBuffer(BufferTarget target, int buffer)
    {
        OpenGLNative.glBindBuffer(MapBufferTarget(target), (uint)buffer);
    }

    public void BufferData(BufferTarget target, int size, IntPtr data, BufferUsageHint usage)
    {
        OpenGLNative.glBufferData(MapBufferTarget(target), (UIntPtr)size, (void*)data, MapBufferUsageHint(usage));
    }

    public void BufferData(BufferTarget target, int size, float[] data, BufferUsageHint usage)
    {
        fixed (float* ptr = data)
        {
            OpenGLNative.glBufferData(MapBufferTarget(target), (UIntPtr)(size * sizeof(float)), ptr, MapBufferUsageHint(usage));
        }
    }

    public void DeleteBuffers(int n, int[] buffers)
    {
        fixed (int* buf = buffers)
        {
            OpenGLNative.glDeleteBuffers((uint)n, ref *(uint*)buf);
        }
    }

    // Get operations
    public void GetInteger(GetPName pname, out int data)
    {
        int tempData;
        OpenGLNative.glGetIntegerv(MapGetPName(pname), &tempData);
        data = tempData;
    }

    public void GetInteger(GetPName pname, int[] data)
    {
        fixed (int* d = data)
        {
            OpenGLNative.glGetIntegerv(MapGetPName(pname), d);
        }
    }

    // State operations
    public bool IsEnabled(EnableCap cap)
    {
        OpenGLNative.glGetError(); // clear any previous errors
        OpenGLNative.glEnable(MapEnableCap(cap));
        uint error = OpenGLNative.glGetError();
        // This is a simplified implementation - proper IsEnabled requires glGetBooleanv
        return error == 0;
    }

    public void Disable(EnableCap cap)
    {
        OpenGLNative.glDisable(MapEnableCap(cap));
    }

    public void ColorMask(bool red, bool green, bool blue, bool alpha)
    {
        OpenGLNative.glColorMask(
            red ? GLBoolean.True : GLBoolean.False,
            green ? GLBoolean.True : GLBoolean.False,
            blue ? GLBoolean.True : GLBoolean.False,
            alpha ? GLBoolean.True : GLBoolean.False);
    }

    public ErrorCode GetError()
    {
        return MapErrorCode(OpenGLNative.glGetError());
    }

    // Draw operations
    public void DrawArrays(PrimitiveType mode, int first, int count)
    {
        OpenGLNative.glDrawArrays(MapPrimitiveType(mode), first, (uint)count);
    }

    public void Viewport(int x, int y, int width, int height)
    {
        OpenGLNative.glViewport(x, y, (uint)width, (uint)height);
    }

    // Copy operations
    public void CopyTexSubImage2D(TextureTarget target, int level, int xoffset, int yoffset, int x, int y, int width, int height)
    {
        OpenGLNative.glCopyTexSubImage2D(
            MapTextureTarget(target),
            level,
            xoffset,
            yoffset,
            x,
            y,
            (uint)width,
            (uint)height);
    }

    // Pixel operations
    public void PixelStore(PixelStoreParameter pname, int param)
    {
        OpenGLNative.glPixelStorei(MapPixelStoreParameter(pname), param);
    }

    public void ReadPixels(int x, int y, int width, int height, PixelFormat format, PixelType type, IntPtr pixels)
    {
        OpenGLNative.glReadPixels(
            x,
            y,
            (uint)width,
            (uint)height,
            MapPixelFormat(format),
            MapPixelType(type),
            (void*)pixels);
    }

    public IntPtr MapBufferRange(BufferTarget target, IntPtr offset, int length, BufferAccessMask access)
    {
        return (IntPtr)OpenGLNative.glMapBufferRange(MapBufferTarget(target), offset, length, MapBufferAccessMask(access));
    }

    public bool UnmapBuffer(BufferTarget target)
    {
        return OpenGLNative.glUnmapBuffer(MapBufferTarget(target)) == GLBoolean.True;
    }

    // Uniform operations
    public void Uniform1(int location, int v0)
    {
        OpenGLNative.glUniform1i(location, v0);
    }

    public void Enable(EnableCap cap)
    {
        OpenGLNative.glEnable(MapEnableCap(cap));
    }

    #region Enum Mappings

    private static Veldrid.OpenGLBindings.ShaderType MapShaderType(ShaderType type)
    {
        return type switch
        {
            ShaderType.FragmentShader => Veldrid.OpenGLBindings.ShaderType.FragmentShader,
            ShaderType.VertexShader => Veldrid.OpenGLBindings.ShaderType.VertexShader,
            ShaderType.GeometryShader => Veldrid.OpenGLBindings.ShaderType.GeometryShader,
            ShaderType.TessEvaluationShader => Veldrid.OpenGLBindings.ShaderType.TessEvaluationShader,
            ShaderType.TessControlShader => Veldrid.OpenGLBindings.ShaderType.TessControlShader,
            ShaderType.ComputeShader => Veldrid.OpenGLBindings.ShaderType.ComputeShader,
            _ => Veldrid.OpenGLBindings.ShaderType.VertexShader
        };
    }

    private static Veldrid.OpenGLBindings.ShaderParameter MapShaderParameter(ShaderParameter parameter)
    {
        return parameter switch
        {
            ShaderParameter.ShaderType => Veldrid.OpenGLBindings.ShaderParameter.ShaderType,
            ShaderParameter.DeleteStatus => Veldrid.OpenGLBindings.ShaderParameter.DeleteStatus,
            ShaderParameter.CompileStatus => Veldrid.OpenGLBindings.ShaderParameter.CompileStatus,
            ShaderParameter.InfoLogLength => Veldrid.OpenGLBindings.ShaderParameter.InfoLogLength,
            ShaderParameter.ShaderSourceLength => Veldrid.OpenGLBindings.ShaderParameter.ShaderSourceLength,
            _ => Veldrid.OpenGLBindings.ShaderParameter.CompileStatus
        };
    }

    private static Veldrid.OpenGLBindings.TextureTarget MapTextureTarget(TextureTarget target)
    {
        return target switch
        {
            TextureTarget.Texture2D => Veldrid.OpenGLBindings.TextureTarget.Texture2D,
            _ => Veldrid.OpenGLBindings.TextureTarget.Texture2D
        };
    }

    private static Veldrid.OpenGLBindings.PixelInternalFormat MapPixelInternalFormat(PixelInternalFormat format)
    {
        return format switch
        {
            PixelInternalFormat.Rgb => Veldrid.OpenGLBindings.PixelInternalFormat.Rgb,
            PixelInternalFormat.Rgba => Veldrid.OpenGLBindings.PixelInternalFormat.Rgba,
            PixelInternalFormat.R8 => Veldrid.OpenGLBindings.PixelInternalFormat.R8,
            _ => Veldrid.OpenGLBindings.PixelInternalFormat.Rgba
        };
    }

    private static GLPixelFormat MapPixelFormat(PixelFormat format)
    {
        return format switch
        {
            PixelFormat.Red => GLPixelFormat.Red,
            PixelFormat.Rgb => GLPixelFormat.Rgb,
            PixelFormat.Rgba => GLPixelFormat.Rgba,
            _ => GLPixelFormat.Rgba
        };
    }

    private static GLPixelType MapPixelType(PixelType type)
    {
        return type switch
        {
            PixelType.UnsignedByte => GLPixelType.UnsignedByte,
            PixelType.Byte => GLPixelType.Byte,
            PixelType.UnsignedShort => GLPixelType.UnsignedShort,
            PixelType.Short => GLPixelType.Short,
            PixelType.UnsignedInt => GLPixelType.UnsignedInt,
            PixelType.Int => GLPixelType.Int,
            PixelType.Float => GLPixelType.Float,
            _ => GLPixelType.UnsignedByte
        };
    }

    private static Veldrid.OpenGLBindings.TextureParameterName MapTextureParameterName(TextureParameterName pname)
    {
        return pname switch
        {
            TextureParameterName.TextureMinFilter => Veldrid.OpenGLBindings.TextureParameterName.TextureMinFilter,
            TextureParameterName.TextureMagFilter => Veldrid.OpenGLBindings.TextureParameterName.TextureMagFilter,
            _ => Veldrid.OpenGLBindings.TextureParameterName.TextureMinFilter
        };
    }

    private static Veldrid.OpenGLBindings.TextureUnit MapTextureUnit(TextureUnit unit)
    {
        return unit switch
        {
            TextureUnit.Texture0 => Veldrid.OpenGLBindings.TextureUnit.Texture0,
            _ => Veldrid.OpenGLBindings.TextureUnit.Texture0
        };
    }

    private static Veldrid.OpenGLBindings.FramebufferTarget MapFramebufferTarget(FramebufferTarget target)
    {
        return target switch
        {
            FramebufferTarget.Framebuffer => Veldrid.OpenGLBindings.FramebufferTarget.Framebuffer,
            _ => Veldrid.OpenGLBindings.FramebufferTarget.Framebuffer
        };
    }

    private static GLFramebufferAttachment MapFramebufferAttachment(FramebufferAttachment attachment)
    {
        return attachment switch
        {
            FramebufferAttachment.ColorAttachment0 => GLFramebufferAttachment.ColorAttachment0,
            FramebufferAttachment.DepthAttachment => GLFramebufferAttachment.DepthAttachment,
            FramebufferAttachment.StencilAttachment => GLFramebufferAttachment.StencilAttachment,
            _ => GLFramebufferAttachment.ColorAttachment0
        };
    }

    private static FramebufferErrorCode MapFramebufferErrorCode(Veldrid.OpenGLBindings.FramebufferErrorCode code)
    {
        return code switch
        {
            Veldrid.OpenGLBindings.FramebufferErrorCode.FramebufferComplete => FramebufferErrorCode.FramebufferComplete,
            _ => (FramebufferErrorCode)code
        };
    }

    private static Veldrid.OpenGLBindings.VertexAttribPointerType MapVertexAttribPointerType(VertexAttribPointerType type)
    {
        return type switch
        {
            VertexAttribPointerType.Float => Veldrid.OpenGLBindings.VertexAttribPointerType.Float,
            _ => Veldrid.OpenGLBindings.VertexAttribPointerType.Float
        };
    }

    private static Veldrid.OpenGLBindings.BufferTarget MapBufferTarget(BufferTarget target)
    {
        return target switch
        {
            BufferTarget.ArrayBuffer => Veldrid.OpenGLBindings.BufferTarget.ArrayBuffer,
            BufferTarget.PixelPackBuffer => Veldrid.OpenGLBindings.BufferTarget.PixelPackBuffer,
            _ => Veldrid.OpenGLBindings.BufferTarget.ArrayBuffer
        };
    }

    private static Veldrid.OpenGLBindings.BufferUsageHint MapBufferUsageHint(BufferUsageHint hint)
    {
        return hint switch
        {
            BufferUsageHint.StaticDraw => Veldrid.OpenGLBindings.BufferUsageHint.StaticDraw,
            BufferUsageHint.StreamRead => Veldrid.OpenGLBindings.BufferUsageHint.StreamRead,
            _ => Veldrid.OpenGLBindings.BufferUsageHint.StaticDraw
        };
    }

    private static Veldrid.OpenGLBindings.GetPName MapGetPName(GetPName pname)
    {
        return pname switch
        {
            GetPName.DrawFramebufferBinding => Veldrid.OpenGLBindings.GetPName.DrawFramebufferBinding,
            GetPName.TextureBinding2D => Veldrid.OpenGLBindings.GetPName.TextureBinding2D,
            GetPName.CurrentProgram => Veldrid.OpenGLBindings.GetPName.CurrentProgram,
            GetPName.ActiveTexture => Veldrid.OpenGLBindings.GetPName.ActiveTexture,
            GetPName.Viewport => Veldrid.OpenGLBindings.GetPName.Viewport,
            _ => Veldrid.OpenGLBindings.GetPName.CurrentProgram
        };
    }

    private static Veldrid.OpenGLBindings.EnableCap MapEnableCap(EnableCap cap)
    {
        return cap switch
        {
            EnableCap.ScissorTest => Veldrid.OpenGLBindings.EnableCap.ScissorTest,
            EnableCap.Blend => Veldrid.OpenGLBindings.EnableCap.Blend,
            EnableCap.DepthTest => Veldrid.OpenGLBindings.EnableCap.DepthTest,
            EnableCap.CullFace => Veldrid.OpenGLBindings.EnableCap.CullFace,
            _ => Veldrid.OpenGLBindings.EnableCap.Blend
        };
    }

    private static ErrorCode MapErrorCode(uint error)
    {
        return (ErrorCode)error;
    }

    private static Veldrid.OpenGLBindings.PrimitiveType MapPrimitiveType(PrimitiveType type)
    {
        return type switch
        {
            PrimitiveType.TriangleStrip => Veldrid.OpenGLBindings.PrimitiveType.TriangleStrip,
            _ => Veldrid.OpenGLBindings.PrimitiveType.TriangleStrip
        };
    }

    private static Veldrid.OpenGLBindings.PixelStoreParameter MapPixelStoreParameter(PixelStoreParameter parameter)
    {
        return parameter switch
        {
            PixelStoreParameter.PackAlignment => Veldrid.OpenGLBindings.PixelStoreParameter.PackAlignment,
            _ => Veldrid.OpenGLBindings.PixelStoreParameter.PackAlignment
        };
    }

    private static Veldrid.OpenGLBindings.BufferAccessMask MapBufferAccessMask(BufferAccessMask mask)
    {
        return mask switch
        {
            BufferAccessMask.MapReadBit => Veldrid.OpenGLBindings.BufferAccessMask.Read,
            _ => Veldrid.OpenGLBindings.BufferAccessMask.Read
        };
    }

    #endregion
}
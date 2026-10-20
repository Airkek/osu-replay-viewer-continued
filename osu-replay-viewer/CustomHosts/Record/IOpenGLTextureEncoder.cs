namespace osu_replay_renderer_netcore.CustomHosts.Record;

public interface IOpenGLTextureEncoder
{
    bool AcceptsOpenGLTexture(PixelFormatMode pixelFormat);

    int TextureBufferCount { get; }

    void PrepareOpenGLTexture(int textureId);

    void WriteOpenGLTexture(int textureId, int width, int height, PixelFormatMode pixelFormat);
}

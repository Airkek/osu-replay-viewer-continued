namespace osu_replay_renderer_netcore.CustomHosts.Record;

public interface IOpenGLTextureEncoder
{
    bool AcceptsOpenGLTexture(PixelFormatMode pixelFormat);

    void WriteOpenGLTexture(int textureId, int width, int height, PixelFormatMode pixelFormat);
}

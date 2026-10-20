namespace osu_replay_renderer_netcore.CustomHosts.Record;

public interface IOwnedFrameEncoder
{
    // Ownership transfers on entry, including when enqueue fails
    void WriteFrame(IFrameLease frame);
}

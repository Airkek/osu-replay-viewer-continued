using System;

namespace osu_replay_renderer_netcore.CustomHosts.Record;

internal sealed class EncoderPacketPump
{
    private readonly Func<IntPtr, int> send;
    private readonly Func<(int Count, int State)> receive;
    private readonly Action<int> check;
    private readonly int again;
    private readonly int eof;

    public EncoderPacketPump(Func<IntPtr, int> send, Func<(int Count, int State)> receive, Action<int> check, int again, int eof)
        => (this.send, this.receive, this.check, this.again, this.eof) = (send, receive, check, again, eof);

    public void Send(IntPtr frame)
    {
        while (true)
        {
            int result = send(frame);
            if (result == again)
            {
                if (receive().Count == 0)
                    throw new InvalidOperationException("Encoder returned EAGAIN without producing output");
                continue;
            }
            if (frame == IntPtr.Zero && result == eof)
                return;
            check(result);
            break;
        }
        var output = receive();
        if (frame == IntPtr.Zero && output.State != eof)
            throw new InvalidOperationException("Encoder did not reach EOF while draining");
    }
}

using System;

namespace osu_replay_renderer_netcore.CustomHosts.Record;

public interface IFrameLease : IDisposable
{
    ReadOnlySpan<byte> Data { get; }
}

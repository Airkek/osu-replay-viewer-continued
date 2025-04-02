using osu.Framework.Timing;

namespace osu_replay_renderer_netcore.CustomHosts.CustomClocks
{
    public class RecordClock : IFrameBasedClock
    {
        public double FrameTime { get; private set; }
        public ulong CurrentFrame { get; set; } = 0;
        private int FPS;

        public RecordClock(int frameRate)
        {
            FPS = frameRate;
            FrameTime = 1000.0 / FPS;
        }

        public double ClockOffset = 0;

        public double ElapsedFrameTime => FrameTime;
        public double FramesPerSecond => FPS;
        FrameTimeInfo IFrameBasedClock.TimeInfo => new() { Elapsed = FrameTime, Current = CurrentTime };
        public double CurrentTime => 1000.0 * (CurrentFrame / FramesPerSecond);
        public double Rate => 1.00;
        public bool IsRunning => true;

        public void ProcessFrame() {}
    }
}

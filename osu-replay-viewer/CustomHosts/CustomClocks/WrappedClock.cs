using osu.Framework.Timing;
using osu.Game.Rulesets.Mods;

namespace osu_replay_renderer_netcore.CustomHosts.CustomClocks
{
    /// <summary>
    /// Dirty wrapped clock which allow me to manipulate gameplay clock
    /// </summary>
    public class WrappedClock : IFrameBasedClock, IAdjustableClock
    {
        private IFrameBasedClock wrap;
        public double TimeOffset { get; set; } = 0;
        public IApplicableToRate RateMod { get; set; } = null;

        private IAdjustableClock original;

        public WrappedClock(IFrameBasedClock wrap, IAdjustableClock originalSource)
        {
            this.wrap = wrap;
            original = originalSource;
        }

        public double ElapsedFrameTime => wrap.ElapsedFrameTime;
        public double FramesPerSecond => wrap.FramesPerSecond;
        public FrameTimeInfo TimeInfo => new FrameTimeInfo { Current = CurrentTime, Elapsed = ElapsedFrameTime };
        public double UnderlyingTime => wrap.CurrentTime + TimeOffset;

        public double CurrentTime
        {
            get
            {
                if (RateMod == null) return UnderlyingTime;
                return UnderlyingTime * RateMod.ApplyToRate(UnderlyingTime);
            }
        }

        public void Reset()
        {
            original.Reset();
        }

        public void Start()
        {
            TimeOffset = -wrap.CurrentTime;
            original.Start();
        }

        public void Stop()
        {
            original.Stop();
        }

        public bool Seek(double position)
        {
            return original.Seek(position);
        }

        public void ResetSpeedAdjustments()
        {
            original.ResetSpeedAdjustments();
        }

        public double Rate
        {
            get { return original.Rate; }
            set { original.Rate = value; }
        }

        public bool IsRunning => original.IsRunning;

        public void ProcessFrame()
        {
            wrap.ProcessFrame();
        }
    }
}
using System;
using osu.Framework.Timing;

namespace osu_replay_renderer_netcore.CustomHosts.CustomClocks
{
    /// <summary>
    /// Advances an adjustable source using deterministic output time
    /// </summary>
    public class WrappedClock : IFrameBasedClock, IAdjustableClock
    {
        private readonly IFrameBasedClock outputClock;
        private double lastOutputTime;
        private double fallbackRate = 1;

        public IAdjustableClock OriginalSource { get; }

        public event Action SourceStarted;
        public event Action SourceStopped;
        public event Action<double> SourceSeeked;
        public event Action<double, double, double, double> SourceAdvanced;

        public WrappedClock(IFrameBasedClock wrap, IAdjustableClock originalSource)
        {
            outputClock = wrap ?? throw new ArgumentNullException(nameof(wrap));
            OriginalSource = originalSource;
            lastOutputTime = outputClock.CurrentTime;
            CurrentTime = originalSource?.CurrentTime ?? lastOutputTime;
            IsRunning = originalSource?.IsRunning ?? true;
        }

        public double ElapsedFrameTime { get; private set; }
        public double FramesPerSecond => outputClock.FramesPerSecond;
        public FrameTimeInfo TimeInfo => new FrameTimeInfo { Current = CurrentTime, Elapsed = ElapsedFrameTime };
        public double CurrentTime { get; private set; }
        public bool IsRunning { get; private set; }

        public void Reset()
        {
            Stop();
            OriginalSource?.Reset();
            CurrentTime = 0;
            ElapsedFrameTime = 0;
            SourceSeeked?.Invoke(CurrentTime);
        }

        public void Start()
        {
            ProcessFrame();
            if (IsRunning)
                return;

            IsRunning = true;
            ElapsedFrameTime = 0;
            OriginalSource?.Start();
            SourceStarted?.Invoke();
        }

        public void Stop()
        {
            ProcessFrame();
            if (!IsRunning)
                return;

            IsRunning = false;
            ElapsedFrameTime = 0;
            OriginalSource?.Stop();
            SourceStopped?.Invoke();
        }

        public bool Seek(double position)
        {
            if (!double.IsFinite(position))
                return false;

            ProcessFrame();
            CurrentTime = position;
            ElapsedFrameTime = 0;
            OriginalSource?.Seek(position);
            SourceSeeked?.Invoke(position);
            return true;
        }

        public void ResetSpeedAdjustments()
        {
            ProcessFrame();
            OriginalSource?.ResetSpeedAdjustments();
            fallbackRate = 1;
        }

        public double Rate
        {
            get => OriginalSource?.Rate ?? fallbackRate;
            set
            {
                ProcessFrame();
                if (OriginalSource != null)
                    OriginalSource.Rate = value;
                else
                    fallbackRate = value;
            }
        }

        public void ProcessFrame()
        {
            outputClock.ProcessFrame();
            double outputTime = outputClock.CurrentTime;
            if (outputTime == lastOutputTime)
                return;

            double outputStart = lastOutputTime;
            double elapsed = outputTime - lastOutputTime;
            lastOutputTime = outputTime;
            ElapsedFrameTime = 0;

            if (!IsRunning || elapsed <= 0)
                return;

            double sourceStart = CurrentTime;
            double rate = Rate;
            ElapsedFrameTime = elapsed * rate;
            CurrentTime += ElapsedFrameTime;
            SourceAdvanced?.Invoke(outputStart, sourceStart, elapsed, rate);
        }
    }
}

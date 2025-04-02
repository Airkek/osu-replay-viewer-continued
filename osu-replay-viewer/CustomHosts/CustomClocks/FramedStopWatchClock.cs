using System;
using osu.Framework.Extensions.TypeExtensions;
using osu.Framework.Timing;

namespace osu_replay_renderer_netcore.CustomHosts.CustomClocks;

public class FramedStopWatchClock : IAdjustableClock
{
    private double rate = 1;
    private double rateChangeAccumulated;
    private double rateChangeUsed;

    private readonly RecordClock recordClock;
    private double seekOffset;
    
    private bool stopped = true;
    private double startTime;
    private double stopTime;

    public FramedStopWatchClock(RecordClock clock, StopwatchClock original)
    {
        recordClock = clock;
        Rate = original.Rate;

        if (original.IsRunning)
            Start();
    }

    private double stopwatchCurrentTime => (stopwatchMilliseconds - rateChangeUsed) * rate + rateChangeAccumulated;

    private double stopwatchMilliseconds =>
        (stopped ? stopTime - startTime : recordClock.CurrentTime - startTime) * 1000;

    public double ElapsedMilliseconds => stopwatchMilliseconds;

    public double CurrentTime => stopwatchCurrentTime + seekOffset;

    public double Rate
    {
        get => rate;
        set
        {
            if (rate == value) return;

            rateChangeAccumulated += (stopwatchMilliseconds - rateChangeUsed) * rate;
            rateChangeUsed = stopwatchMilliseconds;

            rate = value;
        }
    }

    public bool IsRunning { get; }

    public void Reset()
    {
        resetAccumulatedRate();
        stopped = true;
        stopTime = 0;
        startTime = 0;
    }

    public void Start()
    {
        stopped = false;
        startTime = recordClock.CurrentTime;
    }

    public void Stop()
    {
        stopped = true;
        stopTime = recordClock.CurrentTime;
    }

    public void ResetSpeedAdjustments()
    {
        Rate = 1;
    }

    public bool Seek(double position)
    {
        seekOffset = position - stopwatchCurrentTime;
        return true;
    }

    public void Restart()
    {
        resetAccumulatedRate();
        Start();
    }

    public override string ToString()
    {
        return $@"{GetType().ReadableName()} ({Math.Truncate(CurrentTime)}ms)";
    }

    private void resetAccumulatedRate()
    {
        rateChangeAccumulated = 0;
        rateChangeUsed = 0;
    }
}
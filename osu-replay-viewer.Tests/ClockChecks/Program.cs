using osu.Framework.Timing;
using osu.Framework.Audio.Track;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.UI;
using osu_replay_renderer_netcore.CustomHosts.CustomClocks;

int passed = 0;

Check("constant rate integrates across output frames", () =>
{
    var output = new FakeOutputClock();
    var source = new FakeSourceClock { Rate = 1.5 };
    var clock = new WrappedClock(output, source);
    clock.Start();
    for (int frame = 1; frame <= 60; frame++)
    {
        output.CurrentTime = frame * 1000.0 / 60;
        clock.ProcessFrame();
    }
    Equal(clock.CurrentTime, 1500);
    Equal(clock.ElapsedFrameTime, 25);
    Equal(clock.TimeInfo.Current, clock.CurrentTime);
    Equal(clock.TimeInfo.Elapsed, clock.ElapsedFrameTime);
});

Check("live source rate changes preserve previously elapsed time", () =>
{
    var output = new FakeOutputClock();
    var source = new FakeSourceClock();
    var clock = new WrappedClock(output, source);
    var segments = new List<(double Output, double Source, double Duration, double Rate)>();
    clock.SourceAdvanced += (o, s, d, r) => segments.Add((o, s, d, r));
    clock.Start();
    output.CurrentTime = 100;
    clock.ProcessFrame();
    source.Rate = 2;
    output.CurrentTime = 200;
    clock.ProcessFrame();
    source.Rate = .5;
    output.CurrentTime = 300;
    clock.ProcessFrame();
    Equal(clock.CurrentTime, 350);
    Equal(clock.ElapsedFrameTime, 50);
    Assert(segments.SequenceEqual(new[] { (0d, 0d, 100d, 1d), (100d, 100d, 100d, 2d), (200d, 300d, 100d, .5d) }), "integrated segment timing");
});

Check("reads are pure and repeated processing is idempotent", () =>
{
    var output = new FakeOutputClock();
    var clock = new WrappedClock(output, new FakeSourceClock());
    int segments = 0;
    clock.SourceAdvanced += (_, _, _, _) => segments++;
    clock.Start();
    output.CurrentTime = 100;
    Equal(clock.CurrentTime, 0);
    clock.ProcessFrame();
    for (int i = 0; i < 5; i++)
    {
        Equal(clock.CurrentTime, 100);
        Equal(clock.ElapsedFrameTime, 100);
        clock.ProcessFrame();
    }
    Assert(segments == 1, "duplicate advancement");
});

Check("start and stop settle elapsed time and resume without resetting position", () =>
{
    var output = new FakeOutputClock();
    var source = new FakeSourceClock { CurrentTime = -500 };
    var clock = new WrappedClock(output, source);
    int starts = 0;
    int stops = 0;
    clock.SourceStarted += () => { Assert(clock.IsRunning, "start callback state"); starts++; };
    clock.SourceStopped += () => { Assert(!clock.IsRunning, "stop callback state"); stops++; };
    output.CurrentTime = 100;
    clock.Start();
    Equal(clock.CurrentTime, -500);
    Assert(clock.IsRunning && !source.IsRunning, "independent running state for suppressed native playback");
    output.CurrentTime = 300;
    clock.Stop();
    Equal(clock.CurrentTime, -300);
    output.CurrentTime = 1000;
    clock.Start();
    Equal(clock.CurrentTime, -300);
    clock.Start();
    output.CurrentTime = 1100;
    clock.ProcessFrame();
    Equal(clock.CurrentTime, -200);
    clock.Stop();
    clock.Stop();
    Assert(starts == 2 && stops == 2, "duplicate lifecycle events");
});

Check("seek settles the old segment and accepts negative and beyond EOF positions", () =>
{
    var output = new FakeOutputClock();
    var source = new FakeSourceClock { RejectSeek = true, Rate = 2 };
    var clock = new WrappedClock(output, source);
    var advances = new List<double>();
    var seeks = new List<double>();
    clock.SourceAdvanced += (_, _, duration, _) => advances.Add(duration);
    clock.SourceSeeked += seeks.Add;
    clock.Start();
    output.CurrentTime = 100;
    Assert(clock.Seek(-1000), "negative seek");
    Equal(clock.CurrentTime, -1000);
    Equal(source.LastSeek, -1000);
    Equal(clock.ElapsedFrameTime, 0);
    output.CurrentTime = 200;
    clock.ProcessFrame();
    Equal(clock.CurrentTime, -800);
    Assert(clock.Seek(100000), "beyond EOF seek");
    Equal(clock.CurrentTime, 100000);
    clock.ProcessFrame();
    Equal(clock.CurrentTime, 100000);
    Assert(advances.SequenceEqual(new[] { 100d, 100d }), "seek segment duration");
    Assert(seeks.SequenceEqual(new[] { -1000d, 100000d }), "seek callbacks");
    Assert(!clock.Seek(double.NaN) && !clock.Seek(double.PositiveInfinity), "nonfinite seek");
});

Check("reset stops and resets raw position while retaining speed", () =>
{
    var output = new FakeOutputClock();
    var source = new FakeSourceClock { Rate = 1.5 };
    var clock = new WrappedClock(output, source);
    var events = new List<string>();
    clock.SourceStopped += () => events.Add("stop");
    clock.SourceSeeked += position => events.Add($"seek:{position}");
    clock.Start();
    output.CurrentTime = 100;
    clock.Reset();
    Equal(clock.CurrentTime, 0);
    Equal(clock.ElapsedFrameTime, 0);
    Equal(clock.Rate, 1.5);
    Assert(!clock.IsRunning && source.Resets == 1, "reset state");
    Assert(events.SequenceEqual(new[] { "stop", "seek:0" }), "reset events");
    output.CurrentTime = 200;
    clock.ProcessFrame();
    Equal(clock.CurrentTime, 0);
});

Check("rate setters settle the old rate before applying the new rate", () =>
{
    var output = new FakeOutputClock();
    var source = new FakeSourceClock { Rate = 2 };
    var clock = new WrappedClock(output, source);
    clock.Start();
    output.CurrentTime = 100;
    clock.Rate = .5;
    Equal(clock.CurrentTime, 200);
    output.CurrentTime = 200;
    clock.ResetSpeedAdjustments();
    Equal(clock.CurrentTime, 250);
    Equal(clock.Rate, 1);
    output.CurrentTime = 300;
    clock.ProcessFrame();
    Equal(clock.CurrentTime, 350);
});

Check("a missing native source uses output time and remains adjustable", () =>
{
    var output = new FakeOutputClock { CurrentTime = 100 };
    var clock = new WrappedClock(output, null);
    Assert(clock.IsRunning && clock.OriginalSource == null, "fallback state");
    Equal(clock.CurrentTime, 100);
    output.CurrentTime = 200;
    clock.ProcessFrame();
    Equal(clock.CurrentTime, 200);
    clock.Rate = 2;
    output.CurrentTime = 300;
    clock.ProcessFrame();
    Equal(clock.CurrentTime, 400);
    clock.Reset();
    Equal(clock.CurrentTime, 0);
});

Check("a framework framed clock processes its wrapped source", () =>
{
    var output = new FakeOutputClock();
    var clock = new WrappedClock(output, new FakeSourceClock());
    var framed = new FramedClock(clock);
    clock.Start();
    output.CurrentTime = 100;
    framed.ProcessFrame();
    Equal(framed.CurrentTime, 100);
});

Check("the gameplay interpolation and decoupling chain processes its wrapped source", () =>
{
    var output = new FakeOutputClock();
    var clock = new WrappedClock(output, new FakeSourceClock { Rate = 1.5 });
    var decoupled = new DecouplingFramedClock(clock) { AllowDecoupling = false };
    var interpolated = new InterpolatingFramedClock(decoupled);
    clock.Start();
    output.CurrentTime = 100;
    interpolated.ProcessFrame();
    Equal(clock.CurrentTime, 150);
    Equal(decoupled.CurrentTime, 150);
    Equal(interpolated.CurrentTime, 150);
});

foreach (bool adjustPitch in new[] { false, true })
foreach (ModTimeRamp mod in new ModTimeRamp[] { new ModWindUp(), new ModWindDown() })
{
    Check($"native {mod.Acronym} binds and integrates speed with AdjustPitch={adjustPitch}", () =>
    {
        var beatmap = new Beatmap
        {
            HitObjects = new List<HitObject> { new() { StartTime = 1000 }, new() { StartTime = 11000 } }
        };
        mod.AdjustPitch.Value = adjustPitch;
        mod.ApplyToBeatmap(beatmap);
        var output = new FakeOutputClock();
        using var track = new TrackVirtual(12000);
        mod.ApplyToTrack(track);
        var clock = new WrappedClock(output, track);
        clock.Start();
        foreach (double mapTime in new[] { 1000d, 4750d, 8500d, 11000d })
        {
            var framed = new FramedClock(new ManualClock { CurrentTime = mapTime });
            using var playfield = new TestPlayfield { Clock = framed };
            mod.Update(playfield);
            double expectedRate = mod.ApplyToRate(mapTime);
            Equal(track.Rate, expectedRate);
            Equal(track.AggregateFrequency.Value, adjustPitch ? expectedRate : 1);
            Equal(track.AggregateTempo.Value, adjustPitch ? 1 : expectedRate);
            double sourceStart = clock.CurrentTime;
            output.CurrentTime += 100;
            clock.ProcessFrame();
            Equal(clock.CurrentTime - sourceStart, 100 * expectedRate);
        }
    });
}

foreach (bool adjustPitch in new[] { false, true })
{
    Check($"native AS follows live SpeedChange with AdjustPitch={adjustPitch}", () =>
    {
        var mod = new ModAdaptiveSpeed();
        mod.AdjustPitch.Value = adjustPitch;
        var output = new FakeOutputClock();
        using var track = new TrackVirtual(12000);
        mod.ApplyToTrack(track);
        var clock = new WrappedClock(output, track);
        clock.Start();
        mod.SpeedChange.Value = 1.375;
        Equal(mod.ApplyToRate(5000), 1);
        Equal(track.Rate, 1.375);
        Equal(track.AggregateFrequency.Value, adjustPitch ? 1.375 : 1);
        Equal(track.AggregateTempo.Value, adjustPitch ? 1 : 1.375);
        output.CurrentTime = 100;
        clock.ProcessFrame();
        Equal(clock.CurrentTime, 137.5);
    });
}

foreach (ModRateAdjust mod in new ModRateAdjust[] { new TestDoubleTime(), new TestHalfTime(), new TestNightcore() })
{
    Check($"native {mod.Acronym} retains its frequency and tempo behavior", () =>
    {
        using var track = new TrackVirtual(12000);
        mod.ApplyToTrack(track);
        Equal(track.Rate, mod.SpeedChange.Value);
        if (mod is TestNightcore nightcore)
        {
            nightcore.SpeedChange.Value = 1.8;
            Equal(track.Rate, 1.8);
            Equal(track.AggregateFrequency.Value, 1.5);
            Equal(track.AggregateTempo.Value, 1.2);
        }
        else
        {
            Equal(track.AggregateFrequency.Value, 1);
            Equal(track.AggregateTempo.Value, mod.SpeedChange.Value);
        }
    });
}

Console.WriteLine($"Passed {passed} clock checks");

void Check(string name, Action test)
{
    test();
    passed++;
    Console.WriteLine($"PASS {name}");
}

static void Equal(double actual, double expected)
    => Assert(Math.Abs(actual - expected) < .000001, $"expected {expected}, got {actual}");

static void Assert(bool condition, string message)
{
    if (!condition)
        throw new Exception(message);
}

sealed class FakeOutputClock : IFrameBasedClock
{
    public double CurrentTime { get; set; }
    public double Rate => 1;
    public bool IsRunning => true;
    public double ElapsedFrameTime => 1000.0 / FramesPerSecond;
    public double FramesPerSecond => 60;
    public FrameTimeInfo TimeInfo => new() { Current = CurrentTime, Elapsed = ElapsedFrameTime };
    public void ProcessFrame() { }
}

sealed class FakeSourceClock : IAdjustableClock
{
    public double CurrentTime { get; set; }
    public double Rate { get; set; } = 1;
    public bool IsRunning => false;
    public bool RejectSeek { get; set; }
    public double LastSeek { get; private set; }
    public int Resets { get; private set; }
    public void Start() { }
    public void Stop() { }
    public void Reset() { CurrentTime = 0; Resets++; }
    public void ResetSpeedAdjustments() => Rate = 1;
    public bool Seek(double position)
    {
        LastSeek = position;
        if (RejectSeek)
            return false;
        CurrentTime = position;
        return true;
    }
}

sealed partial class TestPlayfield : Playfield { }
sealed class TestDoubleTime : ModDoubleTime { }
sealed class TestHalfTime : ModHalfTime { }
sealed class TestNightcore : ModNightcore { }

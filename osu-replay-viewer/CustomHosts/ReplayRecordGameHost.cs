using osu.Framework;
using osu.Framework.Bindables;
using osu.Framework.Configuration;
using osu.Framework.Input.Handlers;
using osu.Framework.Logging;
using osu.Framework.Platform;
using osu.Framework.Timing;
using osu_replay_renderer_netcore.Audio;
using osu_replay_renderer_netcore.CustomHosts.Record;
using osu_replay_renderer_netcore.Patching;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Reflection;
using osu_replay_renderer_netcore.Audio.Conversion;
using osu_replay_renderer_netcore.CustomHosts.CustomClocks;
using osu_replay_renderer_netcore.Record;
using osu.Framework.Audio.Sample;
using osu.Framework.Audio;
using osu.Framework.Graphics.Audio;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Rendering;
using osu.Game.Skinning;

namespace osu_replay_renderer_netcore.CustomHosts
{
    public enum GlRenderer
    {
        Auto,
        Veldrid,
        Deferred,
        Legacy
    }

    public class ReplayRecordGameHost : DesktopGameHost
    {
        public override IEnumerable<string> UserStoragePaths => CrossPlatform.GetUserStoragePaths();

        public override bool OpenFileExternally(string filename)
        {
            Logger.Log($"Application has requested file \"{filename}\" to be opened.");
            return true;
        }
        public override void OpenUrlExternally(string url) => Logger.Log($"Application has requested URL \"{url}\" to be opened.");
        protected override IFrameBasedClock SceneGraphClock => recordClock;
        protected override IWindow CreateWindow(GraphicsSurfaceType preferredSurface) => CrossPlatform.GetWindow(preferredSurface, Name);
        protected override IEnumerable<InputHandler> CreateAvailableInputHandlers() => [];

        private readonly Bindable<ExecutionMode> executionMode = new(ExecutionMode.SingleThread);

        protected override void SetupConfig(IDictionary<FrameworkSetting, object> defaultOverrides)
        {
            defaultOverrides[FrameworkSetting.ExecutionMode] = ExecutionMode.SingleThread;
            base.SetupConfig(defaultOverrides);

            Config.BindWith(FrameworkSetting.ExecutionMode, executionMode);
            executionMode.BindValueChanged(mode =>
            {
                if (mode.NewValue != ExecutionMode.SingleThread)
                    executionMode.Value = ExecutionMode.SingleThread;
            }, true);
        }

        private readonly RecordClock recordClock;
        private readonly Stopwatch timer = new();

        private readonly EncoderBase encoder;

        private readonly bool isFinishFramePatched;
        private readonly bool isAudioPatched;

        private AudioFormat audioFormat = new() { Channels = 2, PCMSize = 2 };
        private readonly SamplePcmCache sampleCache = new();
        private RecordedAudioMixer recordedAudio;
        private readonly List<MusicPlaybackSegment> musicSegments = new();
        private readonly Dictionary<int, RecordedSampleVoice> sampleVoices = new();
        private WrappedClock gameplayClock;
        private IAdjustableAudioComponent gameplayTrack;
        private Func<double> gameplayOffset;
        private bool recordingStarted;
        private double sampleGain;

        private FFmpegMusicSource musicSource;
        private bool isAudioPlayed = false;
        public bool NeedAudio => isAudioPatched && musicSource is null;

        private readonly GlRenderer rendererType;
        private RenderWrapper wrapper;

        public ReplayRecordGameHost(string gameName, EncoderBase encoder, RecordClock recordClock, GlRenderer rendererType, bool patchesApplied, GameSettings settings) : base(gameName)
        {
            this.encoder = encoder;
            isFinishFramePatched = patchesApplied;
            isAudioPatched = patchesApplied;

            this.recordClock = recordClock;
            this.rendererType = rendererType;

            if (isFinishFramePatched)
            {
                RenderPatcher.OnDraw += OnDraw;
            }

            PrepareAudioRendering(settings);
        }

        internal void SetMusicSource(FFmpegMusicSource source)
        {
            musicSource?.Dispose();
            musicSource = source;
            audioFormat = source.Format.CreateCopy();
            audioFormat.PCMSize = 2;
            recordedAudio = new RecordedAudioMixer(audioFormat);
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing) musicSource?.Dispose();
            }
            finally
            {
                base.Dispose(disposing);
            }
        }

        internal void SetGameplayClock(WrappedClock clock, IAdjustableAudioComponent track, Func<double> offset)
        {
            gameplayClock = clock;
            gameplayTrack = track;
            gameplayOffset = offset;
            clock.SourceStarted += () => isAudioPlayed = true;
            clock.SourceStopped += () => isAudioPlayed = false;
            clock.SourceAdvanced += RecordMusicInterval;
        }

        private long OutputSampleAt(double milliseconds) =>
            Math.Max(0, checked((long)Math.Round(milliseconds * audioFormat.SampleRate / 1000.0)));

        private long CurrentOutputSample => OutputSampleAt(recordClock.CurrentTime);

        private void RecordMusicInterval(double outputStart, double sourceStart, double duration, double rate)
        {
            if (!recordingStarted || !isAudioPlayed || !isAudioPatched || rate <= 0)
                return;
            double frequency = gameplayTrack.AggregateFrequency.Value;
            double tempo = rate / frequency;
            var segment = MusicPlaybackSegment.FromClockInterval(outputStart, sourceStart, duration,
                frequency, tempo, audioFormat.SampleRate);
            if (segment.OutputEndSample > segment.OutputStartSample)
                musicSegments.Add(segment);
        }

        public void StartRecording()
        {
            timer.Reset();
            encoder.Start();
            recordingStarted = true;
        }

        public void FinishRecording()
        {
            try
            {
                wrapper?.Finish(encoder);
                gameplayClock?.ProcessFrame();
                UpdateSampleVoices();
            }
            finally
            {
                try
                {
                    encoder.Finish();
                }
                finally
                {
                    wrapper?.ReleaseResources();
                }
            }
            timer.Stop();

            recordingStarted = false;
            if (isAudioPatched && musicSource != null)
            {
                Console.WriteLine("Rendering audio timeline...");
                var sw = new Stopwatch();
                sw.Start();
                string directory = Path.Combine(Path.GetTempPath(), $"orv-audio-{Guid.NewGuid():N}");
                Directory.CreateDirectory(directory);
                string extension = Path.GetExtension(encoder.Config.OutputPath);
                string tempOutput = encoder.Config.OutputPath + $".{Guid.NewGuid():N}.muxed{extension}";
                try
                {
                    long samples = CurrentOutputSample;
                    string musicPath = Path.Combine(directory, "music.f32");
                    using (musicSource)
                        DynamicMusicRenderer.Render(musicSource, musicSegments, samples, musicPath, encoder.Config.FFmpegExec);
                    var audioEncoder = new ExternalAudioEncoder(Path.Combine(directory, "audio.m4a"),
                        audioFormat.SampleRate, audioFormat.Channels, encoder.Config.FFmpegExec);
                    audioEncoder.Start();
                    try
                    {
                        recordedAudio.Render(samples, musicPath, 1, audioEncoder.Write);
                    }
                    finally
                    {
                        audioEncoder.Finish();
                    }
                    FFmpegAudioTools.MuxAudioVideo(encoder.Config.OutputPath, audioEncoder.OutputPath, tempOutput);
                    File.Move(tempOutput, encoder.Config.OutputPath, true);
                }
                finally
                {
                    if (File.Exists(tempOutput)) File.Delete(tempOutput);
                    Directory.Delete(directory, true);
                }
                sw.Stop();
                Console.WriteLine($"Audio rendering and muxing done in {sw.ElapsedMilliseconds}ms");
            }

            if (_fpsContainer.Count == 0)
            {
                Console.WriteLine($"Render finished in {timer.Elapsed:g}. Frames: {recordClock.CurrentFrame}");
                return;
            }
            _fpsContainer.Sort();
            var medianFps = _fpsContainer[_fpsContainer.Count / 2];
            var minFps = _fpsContainer[0];
            var maxFps = _fpsContainer.Last();
            var averageFps = _fpsContainer.Average();
            Console.WriteLine(FormattableString.Invariant($"Render finished in {timer.Elapsed:g}. FPS - Min: {minFps:F2}, Median: {medianFps:F2}, Max: {maxFps:F2} (Average: {averageFps:F2})"));
        }

        private void PrepareAudioRendering(GameSettings settings)
        {
            if (!isAudioPatched)
                return;
            sampleGain = settings.VolumeEffects * settings.VolumeMaster;
            AudioPatcher.OnSamplePlay += sample =>
            {
                RegisterSample(sample, false, (sample as DrawableSample)?.Clock.CurrentTime);
            };
            var skinSampleVoices = new Dictionary<PoolableSkinnableSample, int>();
            AudioPatcher.OnSkinSamplePlay += skinableSample =>
            {
                long at = SampleEventTime(skinableSample.Clock.CurrentTime);
                if (skinSampleVoices.Remove(skinableSample, out int previous))
                    StopSampleVoice(previous, at);
                int voice = RegisterSample(skinableSample.Sample, skinableSample.Looping, skinableSample.Clock.CurrentTime);
                if (voice != 0) skinSampleVoices[skinableSample] = voice;
            };
            AudioPatcher.OnSkinSampleStop += skinableSample =>
            {
                if (skinSampleVoices.Remove(skinableSample, out int voice))
                    StopSampleVoice(voice, SampleEventTime(skinableSample.Clock.CurrentTime));
            };
        }

        private sealed class RecordedSampleVoice
        {
            public ISample Sample;
            public long LastSample;
            public double SourcePosition;
            public double Rate;
            public double Gain;
            public int SourceLength;
            public int SampleRate;
            public bool Loop;
        }

        private readonly List<int> finishedSampleVoices = new();

        private long SampleEventTime(double? gameplayTime)
        {
            long now = CurrentOutputSample;
            if (gameplayTime == null || !isAudioPlayed || gameplayOffset == null)
                return now;

            double sourceTime = (gameplayTime.Value - gameplayOffset()) / 1000.0;
            for (int i = musicSegments.Count - 1; i >= 0; i--)
            {
                var segment = musicSegments[i];
                double rate = segment.Frequency * segment.Tempo;
                double delta = sourceTime - segment.SourceStartSeconds;
                double duration = (segment.OutputEndSample - segment.OutputStartSample) / (double)audioFormat.SampleRate;
                if (delta >= -0.000001 && delta <= duration * rate + 0.000001)
                    return Math.Clamp(segment.OutputStartSample + (long)Math.Round(delta / rate * audioFormat.SampleRate), 0, now);
                if (i < musicSegments.Count - 2)
                    break;
            }
            return now;
        }

        private int RegisterSample(ISample sample, bool loop, double? gameplayTime)
        {
            if (!recordingStarted || recordedAudio == null || sample == null)
                return 0;
            long at = SampleEventTime(gameplayTime);
            UpdateSampleVoices(at);
            int recursionAllowed = 50;
            while (sample is DrawableSample drawable && recursionAllowed-- > 0)
                sample = drawable.GetUnderlaying();
            if (sample is SampleVirtual || !sample.IsSampleBass())
                return 0;
            var bass = sample.AsSampleBass();
            if (bass.SampleId == 0)
                return 0;
            var buffer = sampleCache.Get(bass.Factory, bass.SampleId, 1, bass.AsAudioBuffer);
            if (buffer == null || buffer.Samples == 0)
                return 0;

            double rate = sample.AggregateFrequency.Value * sample.AggregateTempo.Value;
            double gain = sample.AggregateVolume.Value * sampleGain;
            int id = recordedAudio.StartVoice(at, buffer, gain, rate, loop);
            sampleVoices.Add(id, new RecordedSampleVoice
            {
                Sample = sample, LastSample = at, Rate = rate, Gain = gain,
                SourceLength = buffer.Samples, SampleRate = buffer.Format.SampleRate, Loop = loop
            });
            return id;
        }

        private void StopSampleVoice(int id, long at)
        {
            if (sampleVoices.Remove(id, out var voice))
                recordedAudio.StopVoice(Math.Max(at, voice.LastSample), id);
        }

        private void UpdateSampleVoices() => UpdateSampleVoices(CurrentOutputSample);

        private void UpdateSampleVoices(long at)
        {
            foreach (var entry in sampleVoices)
            {
                var voice = entry.Value;
                long position = Math.Max(at, voice.LastSample);
                voice.SourcePosition += (position - voice.LastSample) * voice.Rate * voice.SampleRate / audioFormat.SampleRate;
                voice.LastSample = position;
                if (!voice.Loop && voice.SourcePosition >= voice.SourceLength)
                {
                    finishedSampleVoices.Add(entry.Key);
                    continue;
                }
                double rate = voice.Sample.AggregateFrequency.Value * voice.Sample.AggregateTempo.Value;
                double gain = voice.Sample.AggregateVolume.Value * sampleGain;
                if (rate != voice.Rate || gain != voice.Gain)
                {
                    recordedAudio.UpdateVoice(position, entry.Key, gain, rate);
                    voice.Rate = rate;
                    voice.Gain = gain;
                }
            }
            foreach (int id in finishedSampleVoices)
                sampleVoices.Remove(id);
            finishedSampleVoices.Clear();
        }

        public void AudioEnded()
        {
            if (!isAudioPlayed) return;
            gameplayClock?.ProcessFrame();
            isAudioPlayed = false;

            Console.WriteLine($"Audio Rendering: Track stopped at frame #{recordClock.CurrentFrame}");
        }

        protected override void ChooseAndSetupRenderer()
        {
            var type = rendererType;

            if (encoder is IOpenGLTextureEncoder && type != GlRenderer.Legacy)
            {
                Console.WriteLine("[Renderer] NVIDIA GPU encoder requires legacy OpenGL renderer; switching renderer to Legacy.");
                type = GlRenderer.Legacy;
            }

            if (type == GlRenderer.Auto)
            {
                if (encoder.PixelFormat != PixelFormatMode.RGB)
                {
                    type = GlRenderer.Legacy;
                }
                else
                {
                    // Veldrid works faster on my Windows pc and Legacy is the best on my linux server and macbook
                    switch (RuntimeInfo.OS)
                    {
                        case RuntimeInfo.Platform.Windows:
                            type = GlRenderer.Veldrid;
                            break;
                        case RuntimeInfo.Platform.Linux:
                        case RuntimeInfo.Platform.macOS:
                            type = GlRenderer.Legacy;
                            break;
                        default:
                            throw new ArgumentOutOfRangeException();
                    }
                }
            }

            string rendererStr;

            switch (type)
            {
                case GlRenderer.Veldrid:
                    rendererStr = "veldrid";
                    break;
                case GlRenderer.Deferred:
                    rendererStr = "deferred";
                    break;
                case GlRenderer.Legacy:
                    rendererStr = "gl";
                    break;
                case GlRenderer.Auto:
                default:
                    throw new ArgumentOutOfRangeException();
            }

            SetupRendererAndWindow(rendererStr, GraphicsSurfaceType.OpenGL);
            wrapper = CreateWrapper(Renderer, encoder.Config.Resolution, encoder.Config.PixelFormat, encoder.Config.ColorSpace);
            if (wrapper is null)
            {
                Console.Error.WriteLine($"Cannot create wrapper for renderer: {Renderer.GetType()}");
                Exit();
            }

            Console.WriteLine($"Created '{type}' renderer. Type: {Renderer.GetType()}, wrapper: {wrapper.GetType()}");
        }

        private static RenderWrapper CreateWrapper(IRenderer renderer, Size size, PixelFormatMode pixelFormat, ColorSpaceMode colorSpace)
        {
            if (VeldridDeviceWrapper.IsSupported(renderer))
            {
                return new VeldridDeviceWrapper(renderer, size, pixelFormat, colorSpace);
            }

            if (GLRendererWrapper.IsSupported(renderer))
            {
                return new GLRendererWrapper(renderer, size, pixelFormat, colorSpace);
            }

            Console.WriteLine($"Unknown renderer: {renderer.GetType()}");
            throw new NotImplementedException($"Unknown renderer: {renderer.GetType()}");
        }


        protected override void SetupForRun()
        {
            base.SetupForRun();
            // The record procedure is basically like this:
            // 1. Create new OpenGL context
            // 2. Draw to that context
            // 3. Take screenshot (a.k.a read the context buffer)
            // 4. Store that screenshot to file, or feed it to FFmpeg
            // 5. Advance the clock to next frame
            // 6. Jump to step 2 until the game decided to end

            MaximumDrawHz = recordClock.FramesPerSecond;
            MaximumUpdateHz = MaximumInactiveHz = 0;
        }
        private bool setupHostInRender = false;

        protected virtual void SetupHostInRender()
        {
            Config.SetValue(FrameworkSetting.FrameSync, FrameSync.Unlimited);
        }

        private Container getRoot()
        {
            PropertyInfo rootProperty = typeof(DesktopGameHost).GetProperty("Root", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            MethodInfo getter = rootProperty.GetGetMethod(nonPublic: true);

            return getter.Invoke(this, null) as Container;
        }

        protected override void DrawFrame()
        {
            float scale = Window.Scale;
            var windowSize = new Size(Math.Max(1, (int)Math.Round(encoder.Config.Resolution.Width / scale)),
                Math.Max(1, (int)Math.Round(encoder.Config.Resolution.Height / scale)));
            Window.MinSize = new Size(Math.Min(Window.MinSize.Width, windowSize.Width),
                Math.Min(Window.MinSize.Height, windowSize.Height));
            Config.SetValue(FrameworkSetting.WindowedSize, windowSize);
            Config.SetValue(FrameworkSetting.WindowMode, WindowMode.Windowed);

            if (!setupHostInRender)
            {
                setupHostInRender = true;
                SetupHostInRender();
            }

            var root = getRoot();
            if (root is null || !root.IsLoaded) return;

            // Draw
            base.DrawFrame();

            if (!isFinishFramePatched)
            {
                OnDraw();
            }
        }

        private List<double> _fpsContainer = new();
        private long _lastFpsPrintTime;
        private ulong _lastFrameCount;

        private void PrintFps()
        {
            if (_lastFpsPrintTime + 1000 > timer.ElapsedMilliseconds)
            {
                return;
            }

            var diffTime = timer.ElapsedMilliseconds - _lastFpsPrintTime;
            var diffFrames = recordClock.CurrentFrame - _lastFrameCount;

            _lastFpsPrintTime = timer.ElapsedMilliseconds;
            _lastFrameCount = recordClock.CurrentFrame;

            var fps = (double)diffFrames / (double)diffTime * 1000d;
            _fpsContainer.Add(fps);
            Console.WriteLine(FormattableString.Invariant($"Current fps: {fps:F2} (speed: {fps / encoder.Config.FPS:F2}x)"));
        }

        private void OnDraw()
        {
            if (encoder is null || !encoder.CanWrite)
            {
                return;
            }

            if (!timer.IsRunning)
            {
                timer.Start();
                Console.WriteLine("Render started");
            }

            gameplayClock?.ProcessFrame();
            if (!wrapper.WriteFrame(encoder))
                return;

            UpdateSampleVoices();

            recordClock.CurrentFrame++;

            PrintFps();
        }
    }
}

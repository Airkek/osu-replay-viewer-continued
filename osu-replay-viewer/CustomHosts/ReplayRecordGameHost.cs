using osu.Framework;
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
using System.Linq;
using System.Reflection;
using osu_replay_renderer_netcore.Audio.Conversion;
using osu_replay_renderer_netcore.CustomHosts.CustomClocks;
using osu_replay_renderer_netcore.Record;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Rendering;

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
        
        private readonly RecordClock recordClock;
        private readonly Stopwatch timer = new();

        private readonly EncoderBase encoder;
        
        private readonly bool isFinishFramePatched;
        private readonly bool isAudioPatched;

        private readonly AudioJournal audioJournal = new();
        private AudioBuffer audioTrack = null;
        public bool NeedAudio => isAudioPatched && audioTrack is null;
        
        private readonly GlRenderer rendererType;
        private RenderWrapper wrapper;

        public ReplayRecordGameHost(string gameName, EncoderBase encoder, RecordClock recordClock, GlRenderer rendererType, bool patchesApplied) : base(gameName)
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

            PrepareAudioRendering();
        }

        public void SetAudioTrack(AudioBuffer track)
        {
            audioTrack = track;
        }

        public void StartRecording()
        {
            timer.Reset();
            encoder.Start();
        }

        public void FinishRecording()
        {
            encoder.Finish();
            timer.Stop();

            if (isAudioPatched)
            {
                var buff = FinishAudio();
                FFmpegAudioTools.WriteAudioToVideo(encoder.OutputPath, buff);
            }

            _fpsContainer.Sort();
            var medianFps = _fpsContainer[_fpsContainer.Count / 2];
            var minFps = _fpsContainer[0];
            var maxFps = _fpsContainer.Last();
            var averageFps = _fpsContainer.Average();
            Console.WriteLine($"Render finished in {timer.Elapsed:g}. FPS - Min: {minFps:F2}, Median: {medianFps:F2}, Max: {maxFps:F2} (Average: {averageFps:F2})");
        }

        private void PrepareAudioRendering()
        {
            if (!isAudioPatched)
            {
                return;
            }
            AudioPatcher.OnTrackPlay += track =>
            {
                Console.WriteLine($"Audio Rendering: Track played at frame #{recordClock.CurrentFrame}");
                if (audioTrack is not null && audioJournal is not null)
                {
                    audioJournal.BufferAt(recordClock.CurrentTime / 1000.0, audioTrack);
                };
            };

            AudioPatcher.OnSamplePlay += sample =>
            {
                if (sample is not null && audioJournal is not null)
                {
                    audioJournal.SampleAt(recordClock.CurrentTime / 1000.0, sample, buff =>
                    {
                        buff = buff.CreateCopy();
                        if (Math.Abs(sample.AggregateFrequency.Value - 1) > double.Epsilon)
                        {
                            buff.SoundTouchAll(p => p.Pitch = sample.Frequency.Value * sample.AggregateFrequency.Value);
                        }
                        buff.Process(x => x * sample.Volume.Value * sample.AggregateVolume.Value);
                        return buff;
                    });
                }
            };
        }

        protected override void ChooseAndSetupRenderer()
        {
            var type = rendererType;

            if (type == GlRenderer.Auto)
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
            wrapper = CreateWrapper(Renderer, encoder.Resolution);
            if (wrapper is null)
            {
                Console.Error.WriteLine($"Cannot create wrapper for renderer: {Renderer.GetType()}");
                Exit();
            }
            
            Console.WriteLine($"Created '{type}' renderer. Type: {Renderer.GetType()}, wrapper: {wrapper.GetType()}");
        }

        private static RenderWrapper CreateWrapper(IRenderer renderer, Size size)
        {
            if (VeldridDeviceWrapper.IsSupported(renderer))
            {
                return new VeldridDeviceWrapper(renderer, size);
            }

            if (GLRendererWrapper.IsSupported(renderer))
            {
                return new GLRendererWrapper(renderer, size);
            }
            
            Console.WriteLine($"Unknown renderer: {renderer.GetType()}");
            throw new NotImplementedException($"Unknown renderer: {renderer.GetType()}");
        }

        private AudioBuffer FinishAudio()
        {
            if (!isAudioPatched)
            {
                return null;
            }
            var buff = AudioBuffer.FromSeconds(new AudioFormat
            {
                Channels = 2,
                SampleRate = 48000,
                PCMSize = 2
            }, audioJournal.LongestDuration + 3.0);
            audioJournal.MixSamples(buff);
            buff.Process(x => Math.Tanh(x));
            return buff;
        }

        protected override void SetupConfig(IDictionary<FrameworkSetting, object> defaultOverrides)
        {
            //defaultOverrides[FrameworkSetting.AudioDevice] = "No sound";
            base.SetupConfig(defaultOverrides);
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
            // Make sure we're using correct framework config
            if (RuntimeInfo.IsApple)
            {
                // Retina display
                Config.SetValue(FrameworkSetting.WindowedSize, encoder.Resolution / 2);
            }
            else
            {
                Config.SetValue(FrameworkSetting.WindowedSize, encoder.Resolution);
            }
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
            Console.WriteLine($"Current fps: {fps:F2} (speed: {fps / encoder.FPS:F2}x)");
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

            wrapper.WriteFrame(encoder);
            recordClock.CurrentFrame++;

            PrintFps();
        }
    }
}

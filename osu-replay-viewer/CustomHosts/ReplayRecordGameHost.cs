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
using System.Reflection;
using System.Threading.Tasks;
using AutoMapper.Internal;
using osu_replay_renderer_netcore.CustomHosts.CustomClocks;
using osu_replay_renderer_netcore.Record;
using osu.Framework.Graphics.Containers;
using osu.Framework.Graphics.Rendering;
using osu.Game.Configuration;

namespace osu_replay_renderer_netcore.CustomHosts
{
    /// <summary>
    /// Game host that's designed to record the game. This will spawn an OpenGL window, but this
    /// will be changed in the future (maybe we'll hide it, or maybe we'll implement entire
    /// fake window from scratch to make it render offscreen)
    /// </summary>
    public class ReplayRecordGameHost : DesktopGameHost
    {
        // public override IEnumerable<string> UserStoragePaths => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData).Yield();
        public override IEnumerable<string> UserStoragePaths => CrossPlatform.GetUserStoragePaths();

        public override bool OpenFileExternally(string filename)
        {
            Logger.Log($"Application has requested file \"{filename}\" to be opened.");
            return true;
        }
        public override void OpenUrlExternally(string url) => Logger.Log($"Application has requested URL \"{url}\" to be opened.");

        private RecordClock recordClock;
        protected override IFrameBasedClock SceneGraphClock => recordClock;
        protected override IWindow CreateWindow(GraphicsSurfaceType preferredSurface) => CrossPlatform.GetWindow(preferredSurface);
        protected override IEnumerable<InputHandler> CreateAvailableInputHandlers() => new InputHandler[] { };

        public System.Drawing.Size Resolution { get; set; } = new System.Drawing.Size { Width = 1280, Height = 720 };
        public ExternalFFmpegEncoder Encoder { get; set; }
        public bool UsingEncoder { get; set; } = true;

        private RenderWrapper wrapper;

        public double FPS => recordClock.FramesPerSecond;

        public ulong Frames => recordClock.CurrentFrame;

        public ReplayRecordGameHost(string gameName, RecordClock clock) : base(gameName, new HostOptions
        {
            //BindIPC = false
        })
        {
            recordClock = clock;
            RenderPatcher.OnDraw += OnDraw;
            PrepareAudioRendering();
        }

        public void StartRecording()
        {
            Encoder.StartFFmpeg();
        }

        public AudioJournal AudioJournal { get; set; } = new();
        public AudioBuffer AudioTrack { get; set; } = null;
        public string AudioOutput { get; set; } = null;

        private void PrepareAudioRendering()
        {
            AudioPatcher.OnTrackPlay += track =>
            {
                //Console.WriteLine($"Audio Rendering: Track played at frame #{recordClock.CurrentFrame}");
                Console.WriteLine(track.CurrentTime);
                if (AudioTrack == null) return;
                AudioJournal.BufferAt(recordClock.CurrentTime / 1000.0, AudioTrack);
            };

            AudioPatcher.OnSamplePlay += sample =>
            {
                if (sample is null) return;
                //Console.WriteLine($"Audio Rendering: Sample played at frame #{recordClock.CurrentFrame}: Freq = {sample.Frequency.Value}:{sample.AggregateFrequency.Value} | Volume = {sample.Volume}:{sample.AggregateVolume} | {recordClock.CurrentTime / 1000}s");
                AudioJournal.SampleAt(recordClock.CurrentTime / 1000.0, sample, buff =>
                {
                    buff = buff.CreateCopy();
                    if (sample.AggregateFrequency.Value != 1) buff.SoundTouchAll(p => p.Pitch = sample.Frequency.Value * sample.AggregateFrequency.Value);
                    buff.Process(x => x * sample.Volume.Value * sample.AggregateVolume.Value);
                    return buff;
                });
            };
        }

        protected override void ChooseAndSetupRenderer()
        {
            SetupRendererAndWindow("veldrid", GraphicsSurfaceType.OpenGL);
            wrapper = CreateWrapper(Renderer, Encoder.Resolution);
        }

        private static RenderWrapper CreateWrapper(IRenderer renderer, Size size)
        {
            RenderWrapper wrapper = null;
            try
            {
                wrapper = new VeldridDeviceWrapper(renderer, size);
                return wrapper;
            } catch {}
            
            try
            {
                wrapper = new GLRendererWrapper(renderer, size);
                return wrapper;
            } catch {}

            return wrapper;
        }

        public AudioBuffer FinishAudio()
        {
            AudioBuffer buff = AudioBuffer.FromSeconds(new AudioFormat
            {
                Channels = 2,
                SampleRate = 44100,
                PCMSize = 2
            }, AudioJournal.LongestDuration + 3.0);
            AudioJournal.MixSamples(buff);
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

        public readonly Stopwatch Timer = new();

        protected override void DrawFrame()
        {            
            // Make sure we're using correct framework config
            Config.SetValue(FrameworkSetting.WindowedSize, Resolution);
            Config.SetValue(FrameworkSetting.WindowMode, WindowMode.Windowed);
            
            if (!setupHostInRender)
            {
                setupHostInRender = true;
                SetupHostInRender();
            }

            if (getRoot() == null) return;

            // Draw
            base.DrawFrame();
        }

        private void OnDraw()
        {
            lock (Encoder.WriteLocker)
            {
                if (Encoder?.InputStream is null || !Encoder.InputStream.CanWrite)
                {
                    return;
                }
                
                if (!Timer.IsRunning)
                {
                    Timer.Start();
                    Logger.Log("Render started", LoggingTarget.Runtime, LogLevel.Important);
                }
                wrapper.WriteScreenshotToStream(Encoder.InputStream);
                recordClock.CurrentFrame++;
            }
           
            // if (previousScreenshotTask != null)
            // {
            //     Task.WaitAll(previousScreenshotTask);
            //     Image<Rgba32> ss = previousScreenshotTask.Result;
            //     if (UsingEncoder && Encoder != null)
            //     {
            //         if (ss.Width == Encoder.Resolution.Width && ss.Height == Encoder.Resolution.Height) Encoder.WriteFrame(ss);
            //     }
            // }
            // previousScreenshotTask = TakeScreenshotAsync();
        }
    }
}

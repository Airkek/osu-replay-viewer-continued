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
using System.IO;
using System.Linq;
using System.Reflection;
using osu_replay_renderer_netcore.Audio.Conversion;
using osu_replay_renderer_netcore.CustomHosts.CustomClocks;
using osu_replay_renderer_netcore.Record;
using osu.Framework.Audio.Sample;
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
        
        private readonly RecordClock recordClock;
        private readonly Stopwatch timer = new();

        private readonly EncoderBase encoder;
        
        private readonly bool isFinishFramePatched;
        private readonly bool isAudioPatched;
        
        private readonly StreamingAudioMixer audioMixer = new(new AudioFormat { Channels = 2, SampleRate = 44100, PCMSize = 2 });
        private ExternalAudioEncoder audioEncoder;
        private double lastAudioTime = 0;

        private AudioBuffer audioTrack = null;
        private StreamingAudioMixer.ActiveVoice audioTrackVoice = null;
        private bool isAudioPlayed = false;
        public bool NeedAudio => isAudioPatched && audioTrack is null;
        
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

        public void SetAudioTrack(AudioBuffer track)
        {
            audioTrack = track;
        }

        public void StartRecording()
        {
            timer.Reset();
            encoder.Start();
            
            if (isAudioPatched)
            {
                var audioPath = encoder.Config.OutputPath + ".audio.aac";
                audioEncoder = new ExternalAudioEncoder(audioPath, 44100, 2, encoder.Config.FFmpegExec);
                audioEncoder.Start();
                lastAudioTime = 0;
            }
        }

        public void FinishRecording()
        {
            wrapper?.Finish(encoder);
            encoder.Finish();
            timer.Stop();

            if (isAudioPatched && audioEncoder != null)
            {
                audioEncoder.Finish();
                Console.WriteLine("Muxing audio and video...");
                var sw = new Stopwatch();
                sw.Start();
                
                // Mux
                var videoPath = encoder.Config.OutputPath;
                var audioPath = audioEncoder.OutputPath;
                var tempOutput = videoPath + ".muxed.mp4";
                
                FFmpegAudioTools.MuxAudioVideo(videoPath, audioPath, tempOutput);
                
                if (File.Exists(tempOutput))
                {
                    File.Delete(videoPath);
                    File.Delete(audioPath);
                    File.Move(tempOutput, videoPath);
                }
                
                sw.Stop();
                Console.WriteLine($"Muxing done in {sw.ElapsedMilliseconds}ms");
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
            {
                return;
            }
            AudioPatcher.OnTrackPlay += track =>
            {
                if (isAudioPlayed)
                {
                    return;
                }
                isAudioPlayed = true;

                var startOffset = (track.CurrentTime / 1000f) / track.Rate;
                Console.WriteLine($"Audio Rendering: Track played at frame #{recordClock.CurrentFrame}");
                
                if (audioTrackVoice != null) audioTrackVoice.Stopped = true;
                
                if (audioTrack is not null)
                {
                    audioTrackVoice = audioMixer.AddVoice(audioTrack);
                    audioTrackVoice.Position = startOffset * audioTrack.Format.SampleRate;
                };
            };

            AudioPatcher.OnTrackStop += track =>
            {
                AudioEnded();
            };

            AudioPatcher.OnTrackSeek += track =>
            {
                if (!isAudioPlayed)
                {
                    return;
                }

                var startOffset = (track.CurrentTime / 1000f) / track.Rate;
                Console.WriteLine($"Audio Rendering: Track seek to {startOffset} at frame #{recordClock.CurrentFrame}");
                if (audioTrackVoice != null) audioTrackVoice.Stopped = true;

                if (audioTrack is not null)
                {
                    audioTrackVoice = audioMixer.AddVoice(audioTrack);
                    audioTrackVoice.Position = startOffset * audioTrack.Format.SampleRate;
                }
            };

            var registerSample = (ISample sample) =>
            {
                if (sample is null)
                {
                    return null;
                }

                // We need to get the buffer from the sample.
                int recursionAllowed = 50;
                while (sample is DrawableSample sample2 && recursionAllowed > 0)
                {
                    sample = sample2.GetUnderlaying();
                    recursionAllowed--;
                }
                if (sample is SampleVirtual) return null;
                if (!sample.IsSampleBass()) return null;

                var bass = sample.AsSampleBass();
                if (bass.SampleId == 0) return null;

                var buff = bass.AsAudioBuffer();
                if (buff == null) return null;

                // Process buffer
                buff = buff.CreateCopy();
                if (Math.Abs(sample.AggregateFrequency.Value - 1) > double.Epsilon)
                {
                    buff.SoundTouchAll(p => p.Pitch = sample.AggregateFrequency.Value);
                }
                buff.Process(x => x * sample.AggregateVolume.Value * settings.VolumeEffects * settings.VolumeMaster);

                var voice = audioMixer.AddVoice(buff);
                return voice;
            };
            
            AudioPatcher.OnSamplePlay += sample =>
            {
                registerSample(sample);
            };
            
            var skinSampleVoices = new Dictionary<PoolableSkinnableSample, StreamingAudioMixer.ActiveVoice>();
            AudioPatcher.OnSkinSamplePlay += skinableSample =>
            {
                var voice = registerSample(skinableSample.Sample);
                if (voice is not null)
                {
                    skinSampleVoices[skinableSample] = voice;
                }
            };

            AudioPatcher.OnSkinSampleStop += skinableSample =>
            {
                if (skinSampleVoices.Remove(skinableSample, out var voice))
                {
                    voice.Stopped = true;
                }
            };
        }

        public void AudioEnded()
        {
            if (!isAudioPlayed) return;
            isAudioPlayed = false;
                
            Console.WriteLine($"Audio Rendering: Track stopped at frame #{recordClock.CurrentFrame}");
            if (audioTrackVoice != null)
            {
                audioTrackVoice.Stopped = true;
                audioTrackVoice = null;
            }
        }

        protected override void ChooseAndSetupRenderer()
        {
            var type = rendererType;

            if (type == GlRenderer.Auto)
            {
                if (encoder.PixelFormat == PixelFormatMode.YUV420)
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
            wrapper = CreateWrapper(Renderer, encoder.Config.Resolution, encoder.Config.PixelFormat);
            if (wrapper is null)
            {
                Console.Error.WriteLine($"Cannot create wrapper for renderer: {Renderer.GetType()}");
                Exit();
            }
            
            Console.WriteLine($"Created '{type}' renderer. Type: {Renderer.GetType()}, wrapper: {wrapper.GetType()}");
        }

        private static RenderWrapper CreateWrapper(IRenderer renderer, Size size, PixelFormatMode pixelFormat)
        {
            if (VeldridDeviceWrapper.IsSupported(renderer))
            {
                return new VeldridDeviceWrapper(renderer, size, pixelFormat);
            }

            if (GLRendererWrapper.IsSupported(renderer))
            {
                return new GLRendererWrapper(renderer, size, pixelFormat);
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
            // Make sure we're using correct framework config
            if (RuntimeInfo.IsApple)
            {
                // Retina display
                Config.SetValue(FrameworkSetting.WindowedSize, encoder.Config.Resolution / 2);
            }
            else
            {
                Config.SetValue(FrameworkSetting.WindowedSize, encoder.Config.Resolution);
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

            wrapper.WriteFrame(encoder);
            
            // Audio mixing
            if (isAudioPatched && audioEncoder != null)
            {
                double currentTime = recordClock.CurrentTime / 1000.0;
                double deltaTime = currentTime - lastAudioTime;
                
                if (deltaTime > 0)
                {
                    int samplesToMix = (int)(deltaTime * audioMixer.Format.SampleRate);
                    if (samplesToMix > 0)
                    {
                        var mixedData = audioMixer.MixChunk(samplesToMix);
                        audioEncoder.Write(mixedData);
                        lastAudioTime += (double)samplesToMix / audioMixer.Format.SampleRate;
                    }
                }
            }

            recordClock.CurrentFrame++;

            PrintFps();
        }
    }
}

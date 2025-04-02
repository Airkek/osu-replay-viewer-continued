using ManagedBass;
using osu.Framework;
using osu.Framework.Configuration;
using osu.Framework.Extensions.IEnumerableExtensions;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Framework.Platform;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using AutoMapper.Internal;

namespace osu_replay_renderer_netcore.CustomHosts
{
    public class ReplayHeadlessGameHost : HeadlessGameHost
    {
        public override IEnumerable<string> UserStoragePaths => CrossPlatform.GetUserStoragePaths();

        public string OutputAudioToFile { get; set; } = null;
        public int AudioInputDevice { get; set; } = -1;
        public int AudioOutputDevice { get; set; } = -1;
        public bool UsingAudioRecorder { get; set; } = true;

        //public int TrackMixerHandle { get; set; } = 0;
        //public int SampleMixerHandle { get; set; } = 0;

        public ReplayHeadlessGameHost(
            string gameName,
            HostOptions options,
            bool realtime = true
        ) : base(gameName, options, realtime)
        {}

        //int frames = 0;

        private Stream fileStream;
        private WaveFileWriter waveFileWriter;
        public void PrepareAudioDevices()
        {
            if (OutputAudioToFile == null) return;
            fileStream = new FileStream(OutputAudioToFile, FileMode.CreateNew);
            waveFileWriter = new WaveFileWriter(fileStream, new WaveFormat(44100, 2));

            // Approach 1: Not using loopback
            /*Console.WriteLine("Rate = " + Bass.ChannelGetInfo(TrackMixerHandle).Frequency);
            int limit = 1200;

            ScheduledDelegate scheduledDelegate = null;
            scheduledDelegate = AudioThread.Scheduler.AddDelayed(() =>
            {
                NextAudio();
                limit--;
                Console.WriteLine(limit);
                if (limit == 0)
                {
                    scheduledDelegate.Cancel();
                    waveFileWriter.Dispose();
                    fileStream.Close();
                }

            }, 20, true);*/

            // Approach 2: Using loopback device
            var prevMasterVolume = Config.Get<double>(FrameworkSetting.VolumeUniversal);
            Config.SetValue(FrameworkSetting.VolumeUniversal, 1.0);

            var prevDevice = Config.Get<string>(FrameworkSetting.AudioDevice);
            Config.SetValue(FrameworkSetting.AudioDevice, Bass.GetDeviceInfo(AudioOutputDevice).Name);

            Bass.RecordInit(AudioInputDevice);
            Bass.RecordStart(44100, 2, 0, (handle, bufferPointer, length, user) =>
            {
                short[] buffer = new short[length / 2];
                Marshal.Copy(bufferPointer, buffer, 0, length / 2);
                waveFileWriter.Write(buffer, length);
                if (!UsingAudioRecorder)
                {
                    waveFileWriter.Dispose();
                    fileStream.Close();
                    Console.WriteLine("Restoring previous framework settings...");
                    Config.SetValue(FrameworkSetting.AudioDevice, prevDevice);
                    Config.SetValue(FrameworkSetting.VolumeUniversal, prevMasterVolume);
                }
                return UsingAudioRecorder;
            });
        }

        // Audio quality is quite horrible if we use the first approach
        // I decided to comment out this entire code
        /*private short[] trackBuffer, sampleBuffer;
        private short trackLastElement = 0, sampleLastElement = 0; // Smoothing output
        private void NextAudio()
        {
            var size = (int)Bass.ChannelSeconds2Bytes(TrackMixerHandle, 0.02);
            if (trackBuffer == null)
            {
                trackBuffer = new short[size / 2];
                sampleBuffer = new short[size / 2];
            }

            Bass.ChannelGetData(TrackMixerHandle, trackBuffer, size);
            Bass.ChannelGetData(SampleMixerHandle, sampleBuffer, size);

            if (trackLastElement != 0) trackBuffer[0] = (short)(trackLastElement / 2 + trackBuffer[0] / 2);
            if (sampleLastElement != 0) sampleBuffer[0] = (short)(sampleLastElement / 2 + sampleBuffer[0] / 2);
            trackLastElement = trackBuffer[trackBuffer.Length - 1];
            sampleLastElement = sampleBuffer[trackBuffer.Length - 1];

            for (int i = 0; i < size / 2; i++)
            {
                trackBuffer[i] = (short)((trackBuffer[i] + sampleBuffer[i]) / 2);
            }
            waveFileWriter.Write(trackBuffer, size);
        }*/

        private Container getRoot()
        {
            PropertyInfo rootProperty = typeof(DesktopGameHost).GetProperty("Root", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            MethodInfo getter = rootProperty.GetGetMethod(nonPublic: true);
            
            return getter.Invoke(this, null) as Container;
        } 
        
        protected override void DrawFrame()
        {
            if (getRoot == null) return;
            //var container = Root.Child as PlatformActionContainer;

            // Here we'll do something to the container
            // Let's just print it out as a tree!
            /*frames++;
            if ((frames) % 240 == 0)
            {
                Console.Clear();
                Console.WriteLine("Screen Report:");
                PrintAsTree(container, 0);
                Console.WriteLine();
            }*/
        }

        private void PrintAsTree(Drawable drawable, int depth)
        {
            string spaces = "".PadLeft(depth * 2, ' ');
            Console.WriteLine(spaces + drawable.GetType().Name + " (" + drawable.X + ", " + drawable.Y + ")");
            if (depth > 6) return;

            if (drawable is Container container1)
            {
                foreach (var child in container1.Children) PrintAsTree(child, depth + 1);
            }
            else if (drawable is Container<Drawable> container2)
            {
                foreach (var child in container2.Children) PrintAsTree(child, depth + 1);
            }
            else if (drawable is CompositeDrawable composite)
            {
                //Console.WriteLine(spaces + "(Composite Drawable)");
                foreach (var child in DrawablesUtils.GetInternalChildren(composite)) PrintAsTree(child, depth + 1);
            }
        }
    }
}

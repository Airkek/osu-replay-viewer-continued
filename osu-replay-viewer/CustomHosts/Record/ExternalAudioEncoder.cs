using System;
using System.Diagnostics;
using System.IO;

namespace osu_replay_renderer_netcore.CustomHosts.Record
{
    public class ExternalAudioEncoder
    {
        public string OutputPath { get; private set; }
        public int SampleRate { get; private set; }
        public int Channels { get; private set; }

        private Process FFmpeg { get; set; }
        private Stream InputStream { get; set; }
        
        private string FFmpegExec = "ffmpeg";

        public ExternalAudioEncoder(string outputPath, int sampleRate, int channels, string ffmpegExec = null)
        {
            OutputPath = outputPath;
            SampleRate = sampleRate;
            Channels = channels;
            if (!string.IsNullOrWhiteSpace(ffmpegExec))
            {
                FFmpegExec = ffmpegExec;
            }
        }

        public void Start()
        {
            // We use aac for speed and compatibility.
            // Input: raw PCM, s16le (signed 16-bit little endian), stereo (or whatever channels), sample rate
            string args = $"-y -f s16le -ar {SampleRate} -ac {Channels} -i pipe: -c:a aac -b:a 192k \"{OutputPath}\"";
            
            Console.WriteLine("Starting Audio FFmpeg process with arguments: " + args);
            FFmpeg = new Process
            {
                StartInfo =
                {
                    UseShellExecute = false,
                    FileName = FFmpegExec,
                    Arguments = args,
                    RedirectStandardInput = true,
                    CreateNoWindow = true
                }
            };
            FFmpeg.Start();
            InputStream = FFmpeg.StandardInput.BaseStream;
        }

        public void Write(float[] data)
        {
            if (InputStream == null) return;

            // Convert float to short (s16le)
            byte[] buffer = new byte[data.Length * 2];
            for (int i = 0; i < data.Length; i++)
            {
                short sample = (short)(Math.Clamp(data[i], -1f, 1f) * 32767f);
                buffer[i * 2] = (byte)(sample & 0xff);
                buffer[i * 2 + 1] = (byte)((sample >> 8) & 0xff);
            }
            InputStream.Write(buffer);
        }

        public void Write(byte[] data)
        {
            if (InputStream == null) return;
            InputStream.Write(data);
        }

        public void Finish()
        {
            if (InputStream != null)
            {
                InputStream.Close();
                InputStream = null;
            }
            if (FFmpeg != null)
            {
                FFmpeg.WaitForExit();
                FFmpeg = null;
            }
        }
    }
}

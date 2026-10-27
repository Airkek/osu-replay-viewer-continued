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
        private readonly int bitrate;

        public ExternalAudioEncoder(string outputPath, int sampleRate, int channels, string ffmpegExec = null, int bitrate = 192000)
        {
            if (bitrate <= 0) throw new ArgumentOutOfRangeException(nameof(bitrate));
            this.bitrate = bitrate;
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
            FFmpeg = new Process
            {
                StartInfo =
                {
                    UseShellExecute = false,
                    FileName = FFmpegExec,
                    RedirectStandardInput = true,
                    CreateNoWindow = true
                }
            };
            foreach (string argument in new[] { "-y", "-f", "s16le", "-ar", SampleRate.ToString(System.Globalization.CultureInfo.InvariantCulture),
                "-ac", Channels.ToString(System.Globalization.CultureInfo.InvariantCulture), "-i", "pipe:", "-c:a", "aac", "-b:a",
                bitrate.ToString(System.Globalization.CultureInfo.InvariantCulture), OutputPath })
                FFmpeg.StartInfo.ArgumentList.Add(argument);
            Console.WriteLine($"Starting audio encoder: {OutputPath}");
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

        public void Write(ReadOnlySpan<byte> data)
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
                using var process = FFmpeg;
                FFmpeg = null;
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidDataException($"FFmpeg audio encode failed with exit code {process.ExitCode}");
            }
        }
    }
}

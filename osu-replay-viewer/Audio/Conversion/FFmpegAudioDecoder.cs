using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text;

namespace osu_replay_renderer_netcore.Audio.Conversion
{
    public static class FFmpegAudioDecoder
    {
        public static string FFmpegExec = "ffmpeg";

        public static AudioBuffer Decode(string path, double tempoFactor = 1.0, double pitchFactor = 1.0, double rateFactor = 1.0, int outChannels = 2, int outRate = 44100)
        {
            var filters = new List<string>();

            tempoFactor *= rateFactor;
            pitchFactor *= rateFactor;

            if (Math.Abs(tempoFactor - 1.0f) > double.Epsilon)
            {
                filters.Add($"rubberband=tempo={tempoFactor}");
            }

            if (Math.Abs(pitchFactor - 1.0f) > double.Epsilon)
            {
                filters.Add($"rubberband=pitch={pitchFactor.ToString(CultureInfo.InvariantCulture)}");
            }

            var args = new StringBuilder();
            args.Append($"-i \"{path}\" ");

            if (filters.Count > 0)
            {
                args.Append($"-af \"{string.Join(",", filters)}\" ");
            }

            args.Append($"-f s16le -acodec pcm_s16le -ac {outChannels} -ar {outRate} -");

            Console.WriteLine($"Starting FFmpeg with arguments: {args}");

            using var ffmpeg = new Process
            {
                StartInfo = 
                {
                    FileName = FFmpegExec,
                    Arguments = args.ToString(),
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                }
            };

            ffmpeg.Start();
            
            var outputStream = new MemoryStream();
            ffmpeg.StandardOutput.BaseStream.CopyTo(outputStream);
            outputStream.Position = 0;
            
            ffmpeg.WaitForExit();

            if (ffmpeg.ExitCode != 0)
            {
                throw new Exception($"FFmpeg error: {ffmpeg.StandardError.ReadToEnd()}");
            }

            int sampleCount = (int)outputStream.Length / 2;
            var buffer = new AudioBuffer(
                new AudioFormat { Channels = outChannels, SampleRate = outRate, PCMSize = 2 },
                sampleCount / outChannels
            );

            using var reader = new BinaryReader(outputStream);
            for (int i = 0; i < sampleCount; i++)
            {
                buffer.Data[i] = reader.ReadInt16() / 32768f;
            }

            return buffer;
        }
    }
}
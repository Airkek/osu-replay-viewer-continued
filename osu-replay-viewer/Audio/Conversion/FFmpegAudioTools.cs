using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;
using System.Text;

namespace osu_replay_renderer_netcore.Audio.Conversion
{
    public static class FFmpegAudioTools
    {
        public static string FFmpegExec = "ffmpeg";
        
        public static void WriteAudioToVideo(string video, AudioBuffer buff)
        {
            var tempFile = video + ".audio.mp4";

            var args = $"-y -i \"{video}\" -i - -c:v copy -c:a aac -b:a 256k -ar {buff.Format.SampleRate} -map 0:v -map 1:a \"{tempFile}\"";
            Console.WriteLine($"Starting FFmpeg with arguments: {args}");

            var ffmpeg = new Process
            {
                StartInfo =
                {
                    UseShellExecute = false,
                    FileName = FFmpegExec,
                    Arguments = args,
                    RedirectStandardInput = true
                }
            };

            try
            {
                ffmpeg.Start();
                buff.WriteWave(ffmpeg.StandardInput.BaseStream);
                ffmpeg.StandardInput.Close();
                ffmpeg.WaitForExit();
            }
            finally
            {
                if (ffmpeg.ExitCode == 0)
                {
                    File.Delete(video);
                    File.Move(tempFile, video);
                }
                else
                {
                    Console.Error.WriteLine("Failed to add audio to video");
                    if (File.Exists(tempFile))
                    {
                        File.Delete(tempFile);
                    }
                }
            }
        }

        public static AudioBuffer Decode(string path, double tempoFactor = 1.0, double pitchFactor = 1.0, double rateFactor = 1.0, double volume = 1.0, int outChannels = 2, int outRate = 48000)
        {
            var filters = new List<string>();

            tempoFactor *= rateFactor;
            pitchFactor *= rateFactor;

            if (Math.Abs(tempoFactor - 1.0f) > double.Epsilon)
            {
                filters.Add($"rubberband=tempo={tempoFactor.ToString(CultureInfo.InvariantCulture)}");
            }

            if (Math.Abs(pitchFactor - 1.0f) > double.Epsilon)
            {
                filters.Add($"rubberband=pitch={pitchFactor.ToString(CultureInfo.InvariantCulture)}");
            }
            
            if (Math.Abs(volume - 1.0) > double.Epsilon)
            {
                filters.Add($"volume={volume.ToString(CultureInfo.InvariantCulture)}");
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
                    RedirectStandardOutput = true
                }
            };

            ffmpeg.Start();
            
            var outputStream = new MemoryStream();
            ffmpeg.StandardOutput.BaseStream.CopyTo(outputStream);
            outputStream.Position = 0;
            
            ffmpeg.WaitForExit();

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
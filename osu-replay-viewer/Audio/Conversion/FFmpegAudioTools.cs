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

            var args =
                $"-y -i \"{video}\" -i - -c:v copy -c:a aac -b:a 256k -ar {buff.Format.SampleRate} -map 0:v -map 1:a \"{tempFile}\"";
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

        public static void WriteAudioToMp3(string outputPath, AudioBuffer buff)
        {
            var tempFile = outputPath + ".tmp.mp3";

            var args = $"-y -f wav -i - -c:a libmp3lame -b:a 256k -ar {buff.Format.SampleRate} \"{tempFile}\"";
            Console.WriteLine($"Starting FFmpeg with arguments: {args}");

            var ffmpeg = new Process
            {
                StartInfo =
                {
                    UseShellExecute = false,
                    FileName = FFmpegExec,
                    Arguments = args,
                    RedirectStandardInput = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            try
            {
                ffmpeg.Start();
                
                buff.WriteWave(ffmpeg.StandardInput.BaseStream);
                ffmpeg.StandardInput.Close();

                ffmpeg.WaitForExit();

                if (ffmpeg.ExitCode == 0)
                {
                    if (File.Exists(outputPath))
                    {
                        File.Delete(outputPath);
                    }

                    File.Move(tempFile, outputPath);
                }
                else
                {
                    Console.Error.WriteLine($"FFmpeg exited with code {ffmpeg.ExitCode}. Audio not written.");
                    if (File.Exists(tempFile))
                    {
                        File.Delete(tempFile);
                    }
                }
            }
            finally
            {
                if (File.Exists(tempFile))
                {
                    try
                    {
                        File.Delete(tempFile);
                    }
                    catch
                    {
                    }
                }
            }
        }


        public static AudioBuffer Decode(string path, double tempoFactor = 1.0, double pitchFactor = 1.0,
            double rateFactor = 1.0, double volume = 1.0, int outChannels = 2, int outRate = 48000)
        {
            var filterParts = new List<string>();

            var rubberbandFilters = new List<string>();
            
            var effectiveTempoFactor = tempoFactor * rateFactor;
            var effectivePitchFactor = pitchFactor * rateFactor;

            if (Math.Abs(effectiveTempoFactor - 1.0) > double.Epsilon)
            {
                rubberbandFilters.Add($"tempo={effectiveTempoFactor.ToString(CultureInfo.InvariantCulture)}");
            }

            if (Math.Abs(effectivePitchFactor - 1.0) > double.Epsilon)
            {
                rubberbandFilters.Add($"pitch={effectivePitchFactor.ToString(CultureInfo.InvariantCulture)}");
            }

            if (rubberbandFilters.Count != 0)
            {
                filterParts.Add($"rubberband={string.Join(":", rubberbandFilters)}");
            }

            if (Math.Abs(volume - 1.0) > double.Epsilon)
            {
                filterParts.Add($"volume={volume.ToString(CultureInfo.InvariantCulture)}");
            }

            var args = new StringBuilder();
            args.Append($"-i \"{path}\" ");

            if (filterParts.Count > 0)
            {
                args.Append($"-af \"{string.Join(",", filterParts)}\" ");
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
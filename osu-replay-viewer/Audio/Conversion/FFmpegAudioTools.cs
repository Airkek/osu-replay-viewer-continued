using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Globalization;

namespace osu_replay_renderer_netcore.Audio.Conversion
{
    public static class FFmpegAudioTools
    {
        public static string FFmpegExec = "ffmpeg";

        public static AudioBuffer Decode(string path, double tempoFactor = 1.0, double pitchFactor = 1.0,
            double rateFactor = 1.0, double volume = 1.0, int outChannels = 2, int? outRate = null)
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

            if (outChannels <= 0 || outRate is <= 0)
                throw new ArgumentOutOfRangeException(nameof(outChannels));

            using var ffmpeg = new Process
            {
                StartInfo =
                {
                    FileName = FFmpegExec,
                    UseShellExecute = false,
                    RedirectStandardOutput = true
                }
            };
            var args = ffmpeg.StartInfo.ArgumentList;
            args.Add("-i");
            args.Add(path);
            args.Add("-vn");
            if (filterParts.Count > 0)
            {
                args.Add("-af");
                args.Add(string.Join(",", filterParts));
            }
            args.Add("-f");
            args.Add("wav");
            args.Add("-acodec");
            args.Add("pcm_f32le");
            args.Add("-ac");
            args.Add(outChannels.ToString(CultureInfo.InvariantCulture));
            if (outRate.HasValue)
            {
                args.Add("-ar");
                args.Add(outRate.Value.ToString(CultureInfo.InvariantCulture));
            }
            args.Add("-");

            Console.WriteLine($"Decoding float PCM with FFmpeg: {path}");
            ffmpeg.Start();
            AudioBuffer buffer;
            try
            {
                buffer = FloatWaveReader.Read(ffmpeg.StandardOutput.BaseStream);
                ffmpeg.WaitForExit();
            }
            catch
            {
                if (!ffmpeg.HasExited) ffmpeg.Kill();
                ffmpeg.WaitForExit();
                throw;
            }
            if (ffmpeg.ExitCode != 0)
                throw new InvalidDataException($"FFmpeg audio decode failed with exit code {ffmpeg.ExitCode}");

            return buffer;
        }

        public static void MuxAudioVideo(string videoPath, string audioPath, string outputPath)
        {
            using var ffmpeg = new Process
            {
                StartInfo = new ProcessStartInfo(FFmpegExec)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            foreach (string argument in new[] { "-y", "-i", videoPath, "-i", audioPath, "-c:v", "copy", "-c:a", "copy", "-map", "0:v", "-map", "1:a", "-shortest", outputPath })
                ffmpeg.StartInfo.ArgumentList.Add(argument);
            ffmpeg.Start();
            ffmpeg.WaitForExit();
            if (ffmpeg.ExitCode != 0)
                throw new InvalidDataException($"FFmpeg mux failed with exit code {ffmpeg.ExitCode}");
        }
    }
}

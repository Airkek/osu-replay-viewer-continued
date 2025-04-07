using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace osu_replay_renderer_netcore.Audio.Conversion
{
    /// <summary>
    /// FFmpeg wrapper to decode audio. IO operations are made via child
    /// process stdio. While osu.Framework already have FFmpeg Autogen, I
    /// can't find an example of reading packets from container and decode
    /// it to PCM data (same goes for video encoding)
    /// </summary>
    public static class FFmpegAudioDecoder
    {
        /// <summary>
        /// Decode given input to PCM signed 16-bit (which is stored in the
        /// buffer).
        /// <param name="input"></param>
        /// <returns></returns>
        public static AudioBuffer Decode(string path, int outChannels = 2, int outRate = 44100)
        {
            var args = $"-i \"{path}\" -f s16le -acodec pcm_s16le -ac {outChannels} -ar {outRate} -";
            Console.WriteLine("Starting FFmpeg process with arguments: " + args);
            var FFmpeg = new Process()
            {
                StartInfo =
                {
                    UseShellExecute = false,
                    CreateNoWindow = false,
                    FileName = "C:\\ffmpeg\\ffmpeg.exe",
                    Arguments = args,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true
                }
            };
            FFmpeg.Start();

            var sOut = FFmpeg.StandardOutput.BaseStream;
            var memoryStream = new MemoryStream();
            sOut.CopyTo(memoryStream);
            memoryStream.Position = 0;

            int integers = (int)memoryStream.Length / 2;
            var buff = new AudioBuffer(new AudioFormat
            {
                Channels = outChannels,
                SampleRate = outRate,
                PCMSize = 2
            }, integers / outChannels);
            BinaryReader reader = new(memoryStream);
            for (int i = 0; i < integers; i++) buff.Data[i] = reader.ReadInt16() / 32768f;
            return buff;
        }
    }
}

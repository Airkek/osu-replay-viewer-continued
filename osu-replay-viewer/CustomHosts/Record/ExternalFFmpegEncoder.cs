using SixLabors.ImageSharp;
using SixLabors.ImageSharp.Formats.Jpeg;
using SixLabors.ImageSharp.PixelFormats;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using osu_replay_renderer_netcore.Audio;
using SixLabors.ImageSharp.Advanced;

namespace osu_replay_renderer_netcore.CustomHosts.Record
{
    /// <summary>
    /// FFmpeg video encoder with actual FFmpeg executable instead of FFmpeg.AutoGen
    /// </summary>
    public class ExternalFFmpegEncoder
    {
        public readonly object WriteLocker = new();
        public Process FFmpeg { get; private set; }
        public Stream InputStream { get; private set; }
        public int FPS { get; set; } = 60;
        public System.Drawing.Size Resolution { get; set; }
        public string OutputPath { get; set; } = "output.mp4";
        public string Preset { get; set; } = "slow";
        public string Encoder { get; set; } = "libx264";
        public string Bitrate { get; set; } = "100M";
        public bool MotionInterpolation { get; set; } = false;

        /// <summary>
        /// Blend multiple frames. Values that's lower than or equals to 1 will disable frames
        /// blending. Frames blending makes encoding process way slower
        /// </summary>
        public int FramesBlending { get; set; } = 1;

        public string FFmpegArguments
        {
            get
            {
                int actualFramesBlending = Math.Max(FramesBlending, 1);

                string inputParameters = $"-y -f rawvideo -pix_fmt rgb24 -s {Resolution.Width}x{Resolution.Height} -r {FPS * actualFramesBlending} -i pipe:";

                string inputEffect;
                if (actualFramesBlending > 1) inputEffect = $"-vf tblend=all_mode=average -r {FPS}";
                else if (MotionInterpolation) inputEffect = $"-vf minterpolate=fps={FPS * 4}";
                else inputEffect = null;

                var encoderSpecific = "";

                switch (Encoder)
                {
                    case "h264_nvenc":
                        encoderSpecific = "-rc constqp -qp 21";
                        break;
                    case "libx264":
                    case "h264_amf":
                    case "h264_qsv":
                    case "h264_videotoolbox":
                        encoderSpecific = "-crf 21";
                        break;
                }
                
                string outputParameters = $"-c:v {Encoder} -vf \"vflip\" {encoderSpecific} -pix_fmt yuv420p -preset {Preset} {OutputPath}";

                return inputParameters + (inputEffect != null? (" " + inputEffect) : "") + " " + outputParameters;
            }
        }

        public void StartFFmpeg()
        {
            Console.WriteLine("Starting FFmpeg process with arguments: " + FFmpegArguments);
            FFmpeg = new Process()
            {
                StartInfo =
                {
                    UseShellExecute = false,
                    CreateNoWindow = false,
                    FileName = "C:\\ffmpeg\\ffmpeg.exe",
                    Arguments = FFmpegArguments,
                    RedirectStandardInput = true
                }
            };
            FFmpeg.Start();
            InputStream = FFmpeg.StandardInput.BaseStream;
        }

        public void Finish()
        {
            InputStream.Close();
            InputStream = null;
        }

        public void WriteAudio(string file)
        {
            var ffmpeg = new Process()
            {
                StartInfo =
                {
                    UseShellExecute = false,
                    CreateNoWindow = false,
                    FileName = "C:\\ffmpeg\\ffmpeg.exe",
                    Arguments = $"-y -i {OutputPath} -i {file} -c:v copy -c:a aac {OutputPath}.audio.mp4",
                    RedirectStandardInput = false
                }
            };
            ffmpeg.Start();
            ffmpeg.WaitForExit();
        }
    }
}

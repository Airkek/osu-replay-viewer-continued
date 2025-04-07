using System;
using System.Diagnostics;
using System.IO;

namespace osu_replay_renderer_netcore.CustomHosts.Record
{
    /// <summary>
    /// FFmpeg video encoder with actual FFmpeg executable instead of FFmpeg.AutoGen
    /// </summary>
    public class ExternalFFmpegEncoder : EncoderBase
    {
        
        private Process FFmpeg { get; set; }
        private Stream InputStream { get; set; }
        private string FFmpegArguments
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

        public override bool CanWrite => InputStream is not null && InputStream.CanWrite;

        protected override void _writeFrameInternal(ReadOnlySpan<byte> frame)
        {
            InputStream.Write(frame);
        }

        protected override void _finishInternal()
        {
            InputStream.Close();
            InputStream = null;
            FFmpeg.WaitForExit();
            FFmpeg = null;
        }

        protected override void _startInternal()
        {
            Console.WriteLine("Starting FFmpeg process with arguments: " + FFmpegArguments);
            FFmpeg = new Process()
            {
                StartInfo =
                {
                    UseShellExecute = false,
                    CreateNoWindow = false,
                    FileName = "ffmpeg",
                    Arguments = FFmpegArguments,
                    RedirectStandardInput = true
                }
            };
            FFmpeg.Start();
            InputStream = FFmpeg.StandardInput.BaseStream;
        }

        public void WriteAudio(string file)
        {
            var ffmpeg = new Process()
            {
                StartInfo =
                {
                    UseShellExecute = false,
                    CreateNoWindow = false,
                    FileName = "ffmpeg",
                    Arguments = $"-y -i {OutputPath} -i {file} -c:v copy -c:a aac {OutputPath}.audio.mp4",
                    RedirectStandardInput = false
                }
            };
            ffmpeg.Start();
            ffmpeg.WaitForExit();
        }
    }
}

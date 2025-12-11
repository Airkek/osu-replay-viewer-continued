using System;
using System.Diagnostics;
using System.IO;

namespace osu_replay_renderer_netcore.CustomHosts.Record
{
    public class ExternalFFmpegEncoder : EncoderBase
    {
        private Process FFmpeg { get; set; }
        private Stream InputStream { get; set; }
        private string FFmpegArguments
        {
            get
            {
                string pixFmt = "rgb24";
                string filters = "-vf \"vflip\"";
                
                string colorFlags = "";
                if (Config.PixelFormat == PixelFormatMode.YUV420)
                {
                    pixFmt = "yuv420p";
                    filters = ""; // Shader handles flip
                    colorFlags = "-colorspace bt709 -color_primaries bt709 -color_trc bt709 -color_range tv";
                }

                var inputParameters = $"-y -f rawvideo -pix_fmt {pixFmt} -s {Config.Resolution.Width}x{Config.Resolution.Height} -r {Config.FPS} -i pipe:";

                var inputEffect = string.Empty;
                var encoderSpecific = string.Empty;

                switch (Config.Encoder)
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
                
                var outputParameters = $"-c:v {Config.Encoder} {filters} {encoderSpecific} {colorFlags} -pix_fmt yuv420p -preset {Config.Preset} {Config.OutputPath}";
                return inputParameters + (string.IsNullOrWhiteSpace(inputEffect)? (" " + inputEffect) : "") + " " + outputParameters;
            }
        }

        public override bool CanWrite => InputStream is not null && InputStream.CanWrite;
        
        public ExternalFFmpegEncoder(EncoderConfig config) : base(config) { }

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
            FFmpeg = new Process
            {
                StartInfo =
                {
                    UseShellExecute = false,
                    FileName = Config.FFmpegExec,
                    Arguments = FFmpegArguments,
                    RedirectStandardInput = true
                }
            };
            FFmpeg.Start();
            InputStream = FFmpeg.StandardInput.BaseStream;
        }
    }
}

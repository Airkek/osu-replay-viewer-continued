using System;
using System.Buffers;
using FFmpeg.AutoGen;
using System.Collections.Generic;

namespace osu_replay_renderer_netcore.CustomHosts.Record
{
    public unsafe class FFmpegAutoGenEncoder : EncoderBase, IEncoderAudioInput
    {
        private AVFormatContext* _formatContext;
        private AVCodecContext* _codecContext;
        private SwsContext* _swsContext;
        private AVStream* _videoStream;
        private AVFrame* _frame;
        private AVPacket* videoPacket;
        private EncoderPacketPump packetPump;
        private int _pts;
        private long _bitrate;
        private bool _headerWritten;
        private NativeAudioMuxer audioMuxer;
        private int audioSampleRate;
        private int audioChannels;

        public override bool CanWrite => _formatContext != null && _formatContext->pb != null;

        public FFmpegAutoGenEncoder(EncoderConfig config) : base(config)
        {
            if (!string.IsNullOrWhiteSpace(Config.FFmpegPath))
            {
                ffmpeg.RootPath = Config.FFmpegPath;
            }
        }


        public void ConfigureAudio(int sampleRate, int channels)
        {
            lock (WriteLocker)
            {
                if (_formatContext != null) throw new InvalidOperationException("Configure audio before starting the encoder");
                if (sampleRate <= 0 || channels < 1 || channels > 8) throw new ArgumentOutOfRangeException(nameof(sampleRate));
                audioSampleRate = sampleRate;
                audioChannels = channels;
            }
        }

        public void WriteAudio(ReadOnlySpan<byte> pcm)
        {
            lock (WriteLocker)
            {
                if (audioMuxer == null) throw new InvalidOperationException("The encoder has no audio stream");
                audioMuxer.Write(pcm);
            }
        }

        private byte[] _pixelBuffer;
        protected override void _startInternal()
        {
            if (Config.PixelFormat == PixelFormatMode.RGB)
            {
                int bufferSize = Config.Resolution.Width * Config.Resolution.Height * 3;
                _pixelBuffer = ArrayPool<byte>.Shared.Rent(bufferSize);
            }

            ffmpeg.avformat_network_init();

            // Allocate output format context
            fixed (AVFormatContext** ctx = &_formatContext) ffmpeg.avformat_alloc_output_context2(ctx, null, null, Config.OutputPath);
            if (_formatContext == null)
                throw new InvalidOperationException("Could not create output context");

            // Find encoder
            AVCodec* codec = ffmpeg.avcodec_find_encoder_by_name(Config.Encoder);
            if (codec == null)
                throw new InvalidOperationException($"Codec {Config.Encoder} not found");

            // Create video stream
            _videoStream = ffmpeg.avformat_new_stream(_formatContext, codec);
            _videoStream->id = (int)_formatContext->nb_streams - 1;

            // Allocate and configure codec context
            _codecContext = ffmpeg.avcodec_alloc_context3(codec);
            if (_codecContext == null)
                throw new InvalidOperationException("Failed to allocate codec context");

            _bitrate = ParseBitrate(Config.Bitrate);
            _codecContext->bit_rate = _bitrate;
            _codecContext->width = Config.Resolution.Width;
            _codecContext->height = Config.Resolution.Height;
            _codecContext->time_base = new AVRational { num = 1, den = Config.FPS };
            _codecContext->framerate = new AVRational { num = Config.FPS, den = 1 };
            _codecContext->pix_fmt = GetAVPixelFormat(Config.PixelFormat);
            _codecContext->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
            _videoStream->time_base = _codecContext->time_base;
            _videoStream->avg_frame_rate = _codecContext->framerate;
            if ((_formatContext->oformat->flags & ffmpeg.AVFMT_GLOBALHEADER) != 0)
                _codecContext->flags |= ffmpeg.AV_CODEC_FLAG_GLOBAL_HEADER;

            // Set color metadata for YUV formats
            if (Config.PixelFormat != PixelFormatMode.RGB)
            {
                if (Config.ColorSpace == ColorSpaceMode.BT601)
                {
                    _codecContext->colorspace = AVColorSpace.AVCOL_SPC_BT470BG;
                    _codecContext->color_primaries = AVColorPrimaries.AVCOL_PRI_BT470BG;
                    _codecContext->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_GAMMA22;
                }
                else // BT709
                {
                    _codecContext->colorspace = AVColorSpace.AVCOL_SPC_BT709;
                    _codecContext->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
                    _codecContext->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
                }
                _codecContext->color_range = AVColorRange.AVCOL_RANGE_JPEG;
            }

            Console.WriteLine($"[FFmpegAutoGenEncoder] Pixel format: {Config.PixelFormat} -> {_codecContext->pix_fmt}, Color space: {Config.ColorSpace}");

            NativeEncoderSettings.Apply(_codecContext, Config);

            // Open codec
            if (ffmpeg.avcodec_open2(_codecContext, codec, null) < 0)
                throw new InvalidOperationException("Failed to open codec");

            // Copy params to stream
            ffmpeg.avcodec_parameters_from_context(_videoStream->codecpar, _codecContext);

            // Allocate frame
            _frame = ffmpeg.av_frame_alloc();
            _frame->format = (int)_codecContext->pix_fmt;
            _frame->width = _codecContext->width;
            _frame->height = _codecContext->height;
            ffmpeg.av_frame_get_buffer(_frame, 32);

            if (Config.PixelFormat == PixelFormatMode.RGB)
            {
                // Setup SWS for RGB24->YUV420P
                _swsContext = ffmpeg.sws_getContext(
                    Config.Resolution.Width, Config.Resolution.Height, AVPixelFormat.AV_PIX_FMT_RGB24,
                    Config.Resolution.Width, Config.Resolution.Height, AVPixelFormat.AV_PIX_FMT_YUV420P,
                    ffmpeg.SWS_POINT, null, null, null);
            }

            videoPacket = ffmpeg.av_packet_alloc();
            if (videoPacket == null) throw new OutOfMemoryException("Could not allocate the video packet");
            packetPump = new EncoderPacketPump(
                pointer => ffmpeg.avcodec_send_frame(_codecContext, (AVFrame*)pointer),
                ReceiveVideoPackets, CheckVideo, ffmpeg.AVERROR(ffmpeg.EAGAIN), ffmpeg.AVERROR_EOF);

            // Open output file
            if (ffmpeg.avio_open(&_formatContext->pb, Config.OutputPath, ffmpeg.AVIO_FLAG_WRITE) < 0)
                throw new InvalidOperationException("Could not open output file");

            if (audioSampleRate > 0)
                audioMuxer = new NativeAudioMuxer(_formatContext, audioSampleRate, audioChannels, Config.AudioBitrate ?? 192000);

            // Write header
            if (ffmpeg.avformat_write_header(_formatContext, null) < 0)
                throw new InvalidOperationException("Could not write output header");
            _headerWritten = true;
        }

        private AVPixelFormat GetAVPixelFormat(PixelFormatMode mode) => mode switch
        {
            PixelFormatMode.YUV420 => AVPixelFormat.AV_PIX_FMT_YUV420P,
            PixelFormatMode.YUV444 => AVPixelFormat.AV_PIX_FMT_YUV444P,
            PixelFormatMode.NV12 => AVPixelFormat.AV_PIX_FMT_NV12,
            _ => AVPixelFormat.AV_PIX_FMT_YUV420P
        };

        protected override void _writeFrameInternal(ReadOnlySpan<byte> frame)
        {
            CheckVideo(ffmpeg.av_frame_make_writable(_frame));
            fixed (byte* framePtr = frame)
            {
                if (Config.PixelFormat == PixelFormatMode.RGB)
                {
                    fixed (byte* srcPtr = _pixelBuffer)
                    {
                        // For some reason sws_scale crashes with ACCESS_VIOLATION when passing mapped PBO pointer :(
                        // TODO: find a way to avoid copying this shit
                        Buffer.MemoryCopy(framePtr, srcPtr, _pixelBuffer.Length, frame.Length);

                        int srcStride = Config.Resolution.Width * 3;
                        byte*[] srcData = { srcPtr + (Config.Resolution.Height - 1) * srcStride, null, null, null };
                        int[] srcStrideArray = { -srcStride, 0, 0, 0 };

                        // Convert to YUV420P with vertical flip
                        ffmpeg.sws_scale(_swsContext, srcData, srcStrideArray, 0, Config.Resolution.Height,
                            _frame->data, _frame->linesize);
                    }
                }
                else if (Config.PixelFormat == PixelFormatMode.YUV420)
                {
                    // YUV420P input (already flipped by shader)
                    CopyYUV420P(framePtr);
                }
                else if (Config.PixelFormat == PixelFormatMode.YUV444)
                {
                    // YUV444P input
                    CopyYUV444P(framePtr);
                }
                else if (Config.PixelFormat == PixelFormatMode.NV12)
                {
                    // NV12 input
                    CopyNV12(framePtr);
                }

                _frame->pts = _pts;
                packetPump.Send((IntPtr)_frame);
                _pts++;
            }
        }

        private (int Count, int State) ReceiveVideoPackets()
        {
            int count = 0;
            while (true)
            {
                int status = ffmpeg.avcodec_receive_packet(_codecContext, videoPacket);
                if (status == ffmpeg.AVERROR(ffmpeg.EAGAIN) || status == ffmpeg.AVERROR_EOF)
                    return (count, status);
                CheckVideo(status);
                try
                {
                    // Preserve the final CFR sample when the encoder omits its duration
                    if (videoPacket->duration <= 0)
                        videoPacket->duration = 1;
                    ffmpeg.av_packet_rescale_ts(videoPacket, _codecContext->time_base, _videoStream->time_base);
                    videoPacket->stream_index = _videoStream->index;
                    CheckVideo(ffmpeg.av_interleaved_write_frame(_formatContext, videoPacket));
                    count++;
                }
                finally
                {
                    ffmpeg.av_packet_unref(videoPacket);
                }
            }
        }

        private static void CheckVideo(int status)
        {
            if (status >= 0) return;
            byte* message = stackalloc byte[256];
            ffmpeg.av_strerror(status, message, 256);
            throw new System.IO.IOException($"Video encoder failed: {System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)message)}");
        }

        private void CopyYUV420P(byte* srcPtr)
        {
            int width = Config.Resolution.Width;
            int height = Config.Resolution.Height;
            int ySize = width * height;
            int uvSize = width * height / 4;

            // Y Plane
            byte* ySrc = srcPtr;
            byte* yDst = _frame->data[0];
            int yStride = _frame->linesize[0];
            for (int i = 0; i < height; i++)
            {
                Buffer.MemoryCopy(ySrc + i * width, yDst + i * yStride, yStride, width);
            }

            // U Plane
            byte* uSrc = srcPtr + ySize;
            byte* uDst = _frame->data[1];
            int uStride = _frame->linesize[1];
            for (int i = 0; i < height / 2; i++)
            {
                Buffer.MemoryCopy(uSrc + i * (width / 2), uDst + i * uStride, uStride, width / 2);
            }

            // V Plane
            byte* vSrc = srcPtr + ySize + uvSize;
            byte* vDst = _frame->data[2];
            int vStride = _frame->linesize[2];
            for (int i = 0; i < height / 2; i++)
            {
                Buffer.MemoryCopy(vSrc + i * (width / 2), vDst + i * vStride, vStride, width / 2);
            }
        }

        private void CopyYUV444P(byte* srcPtr)
        {
            int width = Config.Resolution.Width;
            int height = Config.Resolution.Height;
            int planeSize = width * height;

            // Y Plane
            byte* ySrc = srcPtr;
            byte* yDst = _frame->data[0];
            int yStride = _frame->linesize[0];
            for (int i = 0; i < height; i++)
            {
                Buffer.MemoryCopy(ySrc + i * width, yDst + i * yStride, yStride, width);
            }

            // U Plane
            byte* uSrc = srcPtr + planeSize;
            byte* uDst = _frame->data[1];
            int uStride = _frame->linesize[1];
            for (int i = 0; i < height; i++)
            {
                Buffer.MemoryCopy(uSrc + i * width, uDst + i * uStride, uStride, width);
            }

            // V Plane
            byte* vSrc = srcPtr + planeSize * 2;
            byte* vDst = _frame->data[2];
            int vStride = _frame->linesize[2];
            for (int i = 0; i < height; i++)
            {
                Buffer.MemoryCopy(vSrc + i * width, vDst + i * vStride, vStride, width);
            }
        }

        private void CopyNV12(byte* srcPtr)
        {
            int width = Config.Resolution.Width;
            int height = Config.Resolution.Height;
            int ySize = width * height;
            int uvSize = width * height / 2; // Interleaved UV

            // Y Plane
            byte* ySrc = srcPtr;
            byte* yDst = _frame->data[0];
            int yStride = _frame->linesize[0];
            for (int i = 0; i < height; i++)
            {
                Buffer.MemoryCopy(ySrc + i * width, yDst + i * yStride, yStride, width);
            }

            // UV Plane (interleaved)
            byte* uvSrc = srcPtr + ySize;
            byte* uvDst = _frame->data[1];
            int uvStride = _frame->linesize[1];
            for (int i = 0; i < height / 2; i++)
            {
                Buffer.MemoryCopy(uvSrc + i * width, uvDst + i * uvStride, uvStride, width);
            }
        }

        protected override void _finishInternal()
        {
            try
            {
                if (_headerWritten)
                    packetPump.Send(IntPtr.Zero);

                if (_headerWritten) audioMuxer?.Finish();
                if (_headerWritten && ffmpeg.av_write_trailer(_formatContext) < 0)
                    throw new InvalidOperationException("Could not finalize the output container");
            }
            finally
            {
                audioMuxer?.Dispose();
                audioMuxer = null;
                if (_pixelBuffer is not null)
                {
                    ArrayPool<byte>.Shared.Return(_pixelBuffer);
                    _pixelBuffer = null;
                }
                fixed (AVPacket** packet = &videoPacket) ffmpeg.av_packet_free(packet);
                packetPump = null;
                fixed (AVCodecContext** ctx = &_codecContext) ffmpeg.avcodec_free_context(ctx);
                fixed (AVFrame** frame = &_frame) ffmpeg.av_frame_free(frame);
                if (_swsContext != null) ffmpeg.sws_freeContext(_swsContext);
                if (_formatContext != null)
                {
                    if (_formatContext->pb != null)
                        ffmpeg.avio_closep(&_formatContext->pb);
                    ffmpeg.avformat_free_context(_formatContext);
                }

                _formatContext = null;
                _codecContext = null;
                _swsContext = null;
                _videoStream = null;
                _frame = null;
                _headerWritten = false;
                _pts = 0;
            }
        }

        private long ParseBitrate(string bitrateStr)
        {
            if (string.IsNullOrEmpty(bitrateStr))
                return 10_000_000; // Default

            bitrateStr = bitrateStr.ToUpperInvariant().TrimEnd('B');
            long multiplier = 1;
            if (bitrateStr.EndsWith("K"))
            {
                multiplier = 1_000;
                bitrateStr = bitrateStr.TrimEnd('K');
            }
            else if (bitrateStr.EndsWith("M"))
            {
                multiplier = 1_000_000;
                bitrateStr = bitrateStr.TrimEnd('M');
            }
            else if (bitrateStr.EndsWith("G"))
            {
                multiplier = 1_000_000_000;
                bitrateStr = bitrateStr.TrimEnd('G');
            }

            if (long.TryParse(bitrateStr, out long value))
                return value * multiplier;
            return 10_000_000; // Fallback
        }
    }
}

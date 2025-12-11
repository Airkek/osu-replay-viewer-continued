using System;
using System.Buffers;
using FFmpeg.AutoGen;
using System.Collections.Generic;

namespace osu_replay_renderer_netcore.CustomHosts.Record
{
    public unsafe class FFmpegAutoGenEncoder : EncoderBase
    {
        private AVFormatContext* _formatContext;
        private AVCodecContext* _codecContext;
        private SwsContext* _swsContext;
        private AVStream* _videoStream;
        private AVFrame* _frame;
        private int _pts;
        private long _bitrate;

        public override bool CanWrite => _formatContext != null && _formatContext->pb != null;

        public FFmpegAutoGenEncoder(EncoderConfig config) : base(config)
        {
            if (!string.IsNullOrWhiteSpace(Config.FFmpegPath))
            {
                ffmpeg.RootPath = Config.FFmpegPath;
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
            _codecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_YUV420P;
            _codecContext->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;

            if (Config.PixelFormat == PixelFormatMode.YUV420)
            {
                _codecContext->colorspace = AVColorSpace.AVCOL_SPC_BT709;
                _codecContext->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
                _codecContext->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
                _codecContext->color_range = AVColorRange.AVCOL_RANGE_JPEG;
            }

            // Set encoder options
            var dict = new Dictionary<string, string>();
            dict["preset"] = Config.Preset;
            switch (Config.Encoder)
            {
                case "h264_nvenc":
                    dict["rc"] = "constqp";
                    dict["qp"] = "21";
                    break;
                case "libx264":
                case "h264_amf":
                case "h264_qsv":
                case "h264_videotoolbox":
                    dict["crf"] = "21";
                    break;
            }

            // Convert options to AVDictionary
            AVDictionary* opts = null;
            foreach (var pair in dict)
            {
                ffmpeg.av_dict_set(&opts, pair.Key, pair.Value, 0);
            }

            // Open codec
            if (ffmpeg.avcodec_open2(_codecContext, codec, &opts) < 0)
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

            // Open output file
            if (ffmpeg.avio_open(&_formatContext->pb, Config.OutputPath, ffmpeg.AVIO_FLAG_WRITE) < 0)
                throw new InvalidOperationException("Could not open output file");

            // Write header
            ffmpeg.avformat_write_header(_formatContext, null);
        }

        protected override void _writeFrameInternal(ReadOnlySpan<byte> frame)
        {
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
                else
                {
                    // YUV420P input (already flipped by shader)
                    byte* srcPtr = framePtr;
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

                _frame->pts = _pts++;

                // Send frame to encoder
                int ret = ffmpeg.avcodec_send_frame(_codecContext, _frame);
                if (ret < 0)
                    throw new InvalidOperationException("Failed to send frame");

                // Receive packets
                AVPacket* packet = ffmpeg.av_packet_alloc();
                try
                {
                    while (true)
                    {
                        ret = ffmpeg.avcodec_receive_packet(_codecContext, packet);
                        if (ret == ffmpeg.AVERROR(ffmpeg.EAGAIN) || ret == ffmpeg.AVERROR_EOF)
                            break;
                        if (ret < 0)
                            throw new InvalidOperationException("Error during encoding");

                        ffmpeg.av_packet_rescale_ts(packet, _codecContext->time_base, _videoStream->time_base);
                        packet->stream_index = _videoStream->index;
                        ffmpeg.av_interleaved_write_frame(_formatContext, packet);
                        ffmpeg.av_packet_unref(packet);
                    }
                }
                finally
                {
                    ffmpeg.av_packet_free(&packet);
                }
            }
        }

        protected override void _finishInternal()
        {
            if (_pixelBuffer is not null)
            {
                ArrayPool<byte>.Shared.Return(_pixelBuffer);
            }
            // Flush encoder
            ffmpeg.avcodec_send_frame(_codecContext, null);

            AVPacket* packet = ffmpeg.av_packet_alloc();
            try
            {
                while (ffmpeg.avcodec_receive_packet(_codecContext, packet) >= 0)
                {
                    ffmpeg.av_packet_rescale_ts(packet, _codecContext->time_base, _videoStream->time_base);
                    packet->stream_index = _videoStream->index;
                    ffmpeg.av_interleaved_write_frame(_formatContext, packet);
                    ffmpeg.av_packet_unref(packet);
                }
            }
            finally
            {
                ffmpeg.av_packet_free(&packet);
            }

            // Write trailer
            ffmpeg.av_write_trailer(_formatContext);

            // Cleanup
            ffmpeg.avcodec_close(_codecContext);
            fixed (AVCodecContext** ctx = &_codecContext) ffmpeg.avcodec_free_context(ctx);
            fixed (AVFrame** frame = &_frame) ffmpeg.av_frame_free(frame);
            if (_swsContext != null) ffmpeg.sws_freeContext(_swsContext);
            ffmpeg.avio_closep(&_formatContext->pb);
            ffmpeg.avformat_free_context(_formatContext);

            _formatContext = null;
            _codecContext = null;
            _swsContext = null;
            _videoStream = null;
            _frame = null;
            _pts = 0;
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
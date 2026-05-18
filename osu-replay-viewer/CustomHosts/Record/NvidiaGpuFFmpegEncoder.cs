using System;
using System.Collections.Generic;
using System.Drawing;
using FFmpeg.AutoGen;
using ManagedCuda;
using ManagedCuda.BasicTypes;
using static FFmpeg.AutoGen.ffmpeg;

namespace osu_replay_renderer_netcore.CustomHosts.Record;

public unsafe class NvidiaGpuFFmpegEncoder : EncoderBase, IOpenGLTextureEncoder
{
    private const int gl_texture_2d = 0x0DE1;

    private AVFormatContext* formatContext;
    private AVCodecContext* codecContext;
    private AVStream* videoStream;
    private AVBufferRef* cudaDeviceRef;
    private AVBufferRef* cudaFramesRef;
    private int pts;
    private long bitrate;

    private CUcontext cudaContext;
    private CUstream cudaStream;
    private CudaOpenGLImageInteropResource registeredTexture;
    private int registeredTextureId;

    private bool acceptingFrames;

    public override bool CanWrite => formatContext != null && formatContext->pb != null && acceptingFrames;

    public NvidiaGpuFFmpegEncoder(EncoderConfig config) : base(config)
    {
        if (!string.IsNullOrWhiteSpace(Config.FFmpegPath))
            ffmpeg.RootPath = Config.FFmpegPath;
    }

    public static bool IsSupportedConfig(EncoderConfig config)
    {
        return config.Encoder is "h264_nvenc" or "hevc_nvenc";
    }

    public bool AcceptsOpenGLTexture(PixelFormatMode pixelFormat) => pixelFormat == PixelFormatMode.NV12;

    protected override void _startInternal()
    {
        if (acceptingFrames)
            throw new InvalidOperationException("Already accepting frames.");
        
        if (Config.PixelFormat != PixelFormatMode.NV12)
            throw new InvalidOperationException("NVIDIA GPU encoder requires NV12 pixel format.");

        avformat_network_init();

        fixed (AVFormatContext** ctx = &formatContext)
            ThrowIfError(avformat_alloc_output_context2(ctx, null, null, Config.OutputPath), "Could not create output context");

        if (formatContext == null)
            throw new InvalidOperationException("Could not create output context.");

        AVCodec* codec = avcodec_find_encoder_by_name(Config.Encoder);
        if (codec == null)
            throw new InvalidOperationException($"Codec {Config.Encoder} not found.");

        videoStream = avformat_new_stream(formatContext, codec);
        if (videoStream == null)
            throw new InvalidOperationException("Could not create video stream.");

        videoStream->id = (int)formatContext->nb_streams - 1;

        codecContext = avcodec_alloc_context3(codec);
        if (codecContext == null)
            throw new InvalidOperationException("Failed to allocate codec context.");

        bitrate = ParseBitrate(Config.Bitrate);
        codecContext->bit_rate = bitrate;
        codecContext->width = Config.Resolution.Width;
        codecContext->height = Config.Resolution.Height;
        codecContext->time_base = new AVRational { num = 1, den = Config.FPS };
        codecContext->framerate = new AVRational { num = Config.FPS, den = 1 };
        codecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_CUDA;
        codecContext->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
        codecContext->color_range = AVColorRange.AVCOL_RANGE_JPEG;

        if (Config.ColorSpace == ColorSpaceMode.BT601)
        {
            codecContext->colorspace = AVColorSpace.AVCOL_SPC_BT470BG;
            codecContext->color_primaries = AVColorPrimaries.AVCOL_PRI_BT470BG;
            codecContext->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_GAMMA22;
        }
        else
        {
            codecContext->colorspace = AVColorSpace.AVCOL_SPC_BT709;
            codecContext->color_primaries = AVColorPrimaries.AVCOL_PRI_BT709;
            codecContext->color_trc = AVColorTransferCharacteristic.AVCOL_TRC_BT709;
        }

        if ((formatContext->oformat->flags & AVFMT_GLOBALHEADER) != 0)
            codecContext->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;

        InitializeCudaFrames();

        AVDictionary* opts = null;
        foreach (var pair in GetEncoderOptions())
            av_dict_set(&opts, pair.Key, pair.Value, 0);

        try
        {
            ThrowIfError(avcodec_open2(codecContext, codec, &opts), "Failed to open codec");
        }
        finally
        {
            av_dict_free(&opts);
        }

        ThrowIfError(avcodec_parameters_from_context(videoStream->codecpar, codecContext), "Failed to copy codec parameters");

        if ((formatContext->oformat->flags & AVFMT_NOFILE) == 0)
            ThrowIfError(avio_open(&formatContext->pb, Config.OutputPath, AVIO_FLAG_WRITE), "Could not open output file");

        ThrowIfError(avformat_write_header(formatContext, null), "Failed to write output header");
        Console.WriteLine("[NvidiaGpuFFmpegEncoder] Using OpenGL texture -> CUDA frame -> NVENC path (no raw frame readback).");

        acceptingFrames = true;
    }

    protected override void _writeFrameInternal(ReadOnlySpan<byte> frame)
    {
        throw new NotSupportedException("NVIDIA GPU encoder accepts OpenGL textures only.");
    }

    public void WriteOpenGLTexture(int textureId, int width, int height, PixelFormatMode pixelFormat)
    {
        if (!CanWrite || !AcceptsOpenGLTexture(pixelFormat))
            return;

        if (width != Config.Resolution.Width || height != Config.Resolution.Height * 3 / 2)
            throw new InvalidOperationException($"Unexpected NV12 texture size: {width}x{height}.");

        AVFrame* frame = av_frame_alloc();
        if (frame == null)
            throw new InvalidOperationException("Could not allocate frame.");

        try
        {
            frame->format = (int)AVPixelFormat.AV_PIX_FMT_CUDA;
            frame->width = Config.Resolution.Width;
            frame->height = Config.Resolution.Height;
            frame->hw_frames_ctx = av_buffer_ref(cudaFramesRef);
            if (frame->hw_frames_ctx == null)
                throw new InvalidOperationException("Could not reference CUDA frames context.");

            ThrowIfError(av_hwframe_get_buffer(cudaFramesRef, frame, 0), "Could not allocate CUDA frame");
            CopyTextureToFrame(textureId, frame);

            frame->pts = pts++;
            EncodeFrame(frame);
        }
        finally
        {
            av_frame_free(&frame);
        }
    }

    private void InitializeCudaFrames()
    {
        AVBufferRef* deviceRef = null;
        ThrowIfError(av_hwdevice_ctx_create(&deviceRef, AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, null, null, 0), "Could not create FFmpeg CUDA device");
        cudaDeviceRef = deviceRef;

        cudaFramesRef = av_hwframe_ctx_alloc(cudaDeviceRef);
        if (cudaFramesRef == null)
            throw new InvalidOperationException("Could not allocate CUDA frames context.");

        var framesContext = (AVHWFramesContext*)cudaFramesRef->data;
        framesContext->format = AVPixelFormat.AV_PIX_FMT_CUDA;
        framesContext->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
        framesContext->width = Config.Resolution.Width;
        framesContext->height = Config.Resolution.Height;
        framesContext->initial_pool_size = 4;

        ThrowIfError(av_hwframe_ctx_init(cudaFramesRef), "Could not initialize CUDA frames context");

        codecContext->hw_frames_ctx = av_buffer_ref(cudaFramesRef);
        if (codecContext->hw_frames_ctx == null)
            throw new InvalidOperationException("Could not reference CUDA frames context.");

        cudaContext = NvidiaCudaInterop.GetCudaContext(cudaDeviceRef);
        cudaStream = NvidiaCudaInterop.GetCudaStream(cudaDeviceRef);
    }

    private Dictionary<string, string> GetEncoderOptions()
    {
        var dict = new Dictionary<string, string>
        {
            ["preset"] = Config.Preset,
            ["rc"] = "constqp",
            ["qp"] = "21",
        };

        return dict;
    }

    private void CopyTextureToFrame(int textureId, AVFrame* frame)
    {
        using var _ = NvidiaCudaInterop.PushContext(cudaContext);

        if (registeredTexture == null || registeredTextureId != textureId)
        {
            registeredTexture?.Dispose();
            registeredTexture = new CudaOpenGLImageInteropResource(
                (uint)textureId,
                CUGraphicsRegisterFlags.ReadOnly,
                (CudaOpenGLImageInteropResource.OpenGLImageTarget)gl_texture_2d);
            registeredTextureId = textureId;
        }

        registeredTexture.Map(cudaStream);
        try
        {
            var source = registeredTexture.GetMappedCUArray(0, 0);
            var height = Config.Resolution.Height;
            var width = Config.Resolution.Width;
            var yPlane = new IntPtr(frame->data[0]);
            var uvPlane = frame->data[1] != null
                ? new IntPtr(frame->data[1])
                : IntPtr.Add(yPlane, frame->linesize[0] * height);

            NvidiaCudaInterop.CopyArrayToDevice2D(source, 0, yPlane, frame->linesize[0], width, height);
            NvidiaCudaInterop.CopyArrayToDevice2D(source, height, uvPlane, frame->linesize[1], width, height / 2);
        }
        finally
        {
            registeredTexture.UnMap(cudaStream);
        }
    }

    private void EncodeFrame(AVFrame* frame)
    {
        ThrowIfError(avcodec_send_frame(codecContext, frame), "Failed to send frame");
        ReceivePackets();
    }

    private void ReceivePackets()
    {
        AVPacket* packet = av_packet_alloc();
        if (packet == null)
            throw new InvalidOperationException("Could not allocate packet.");

        try
        {
            while (true)
            {
                int ret = avcodec_receive_packet(codecContext, packet);
                if (ret == AVERROR(EAGAIN) || ret == AVERROR_EOF)
                    break;

                ThrowIfError(ret, "Error during encoding");
                av_packet_rescale_ts(packet, codecContext->time_base, videoStream->time_base);
                packet->stream_index = videoStream->index;
                ThrowIfError(av_interleaved_write_frame(formatContext, packet), "Failed to write packet");
                av_packet_unref(packet);
            }
        }
        finally
        {
            av_packet_free(&packet);
        }
    }

    protected override void _finishInternal()
    {
        acceptingFrames = false;
        
        if (codecContext == null)
            return;

        try
        {
            avcodec_send_frame(codecContext, null);
            ReceivePackets();
            av_write_trailer(formatContext);
        }
        finally
        {
            if (registeredTexture != null)
            {
                using (NvidiaCudaInterop.PushContext(cudaContext))
                    registeredTexture.Dispose();
            }

            registeredTexture = null;
            registeredTextureId = 0;

            if (codecContext != null)
            {
                avcodec_close(codecContext);
                fixed (AVCodecContext** ctx = &codecContext)
                    avcodec_free_context(ctx);
            }

            if (cudaFramesRef != null)
            {
                AVBufferRef* framesRef = cudaFramesRef;
                av_buffer_unref(&framesRef);
                cudaFramesRef = null;
            }

            if (cudaDeviceRef != null)
            {
                AVBufferRef* deviceRef = cudaDeviceRef;
                av_buffer_unref(&deviceRef);
                cudaDeviceRef = null;
            }

            if (formatContext != null)
            {
                if ((formatContext->oformat->flags & AVFMT_NOFILE) == 0 && formatContext->pb != null)
                    avio_closep(&formatContext->pb);

                avformat_free_context(formatContext);
            }

            formatContext = null;
            codecContext = null;
            videoStream = null;
            pts = 0;
        }
    }

    private static void ThrowIfError(int error, string message)
    {
        if (error >= 0)
            return;

        const int bufferSize = 1024;
        byte* buffer = stackalloc byte[bufferSize];
        av_strerror(error, buffer, (ulong)bufferSize);
        throw new InvalidOperationException($"{message}: {System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)buffer)}");
    }

    private static long ParseBitrate(string bitrateStr)
    {
        if (string.IsNullOrEmpty(bitrateStr))
            return 10_000_000;

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

        return long.TryParse(bitrateStr, out long value) ? value * multiplier : 10_000_000;
    }
}
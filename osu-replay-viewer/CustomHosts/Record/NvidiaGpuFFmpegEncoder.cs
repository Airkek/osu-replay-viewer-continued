using System;
using System.Buffers;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Runtime.ExceptionServices;
using FFmpeg.AutoGen;
using ManagedCuda;
using ManagedCuda.BasicTypes;
using static FFmpeg.AutoGen.ffmpeg;

namespace osu_replay_renderer_netcore.CustomHosts.Record;

public unsafe class NvidiaGpuFFmpegEncoder : EncoderBase, IOpenGLTextureEncoder, IEncoderAudioInput, IDisposable
{
    private const int gl_texture_2d = 0x0DE1;
    private const int frame_capacity = 3;
    private readonly object producerLock = new();
    private readonly BlockingCollection<FrameSlot> availableFrames = new(frame_capacity);
    private readonly List<FrameSlot> frameSlots = new();
    private readonly Dictionary<int, TextureRegistration> textures = new();
    private EncoderWorker<WorkItem> worker;
    private AVFormatContext* formatContext;
    private AVCodecContext* codecContext;
    private AVStream* videoStream;
    private AVPacket* packet;
    private AVBufferRef* cudaDeviceRef;
    private AVBufferRef* cudaFramesRef;
    private CUcontext cudaContext;
    private CUstream cudaStream;
    private CudaStream transferStream;
    private NativeAudioMuxer audioMuxer;
    private EncoderPacketPump packetPump;
    private int audioSampleRate;
    private int audioChannels;
    private long pts;
    private bool startRequested;
    private bool finished;
    private volatile bool acceptingFrames;

    private sealed class FrameSlot
    {
        public AVFrame* Frame;
        public CudaEvent Ready;
        public bool CopyCompleted;
    }

    private sealed class TextureRegistration
    {
        public CudaOpenGLImageInteropResource Resource;
        public CudaEvent Ready;
        public bool HasCopy;
    }

    private readonly struct WorkItem
    {
        public readonly FrameSlot Video;
        public readonly byte[] Audio;
        public readonly int AudioLength;

        public WorkItem(FrameSlot video) => (Video, Audio, AudioLength) = (video, null, 0);
        public WorkItem(byte[] audio, int length) => (Video, Audio, AudioLength) = (null, audio, length);
    }

    public override bool CanWrite
    {
        get
        {
            worker?.ThrowIfFaulted();
            return acceptingFrames;
        }
    }

    public int TextureBufferCount => frame_capacity;

    public NvidiaGpuFFmpegEncoder(EncoderConfig config) : base(config)
    {
        if (!string.IsNullOrWhiteSpace(Config.FFmpegPath))
            ffmpeg.RootPath = Config.FFmpegPath;
    }

    public static bool IsSupportedConfig(EncoderConfig config) => config.Encoder is "h264_nvenc" or "hevc_nvenc";
    public bool AcceptsOpenGLTexture(PixelFormatMode pixelFormat) => pixelFormat == PixelFormatMode.NV12;

    public void ConfigureAudio(int sampleRate, int channels)
    {
        lock (producerLock)
        {
            if (startRequested)
                throw new InvalidOperationException("Audio must be configured before starting the encoder");
            if (sampleRate < 1 || channels < 1)
                throw new ArgumentOutOfRangeException(nameof(sampleRate));
            audioSampleRate = sampleRate;
            audioChannels = channels;
        }
    }

    protected override void _startInternal()
    {
        lock (producerLock)
        {
            if (startRequested || finished)
                throw new InvalidOperationException("The encoder has already been started or disposed");
            if (Config.PixelFormat != PixelFormatMode.NV12)
                throw new InvalidOperationException("NVIDIA GPU encoder requires NV12 pixel format");
            startRequested = true;
            worker = new EncoderWorker<WorkItem>(frame_capacity * 2, InitializeEncoder, ProcessItem, ReleaseItem,
                FinalizeEncoder, ReleaseResources);
        }

        try
        {
            worker.Start();
            acceptingFrames = true;
        }
        catch
        {
            finished = true;
            try { worker.Dispose(); }
            catch { }
            availableFrames.Dispose();
            throw;
        }
    }

    private void InitializeEncoder()
    {
        fixed (AVFormatContext** context = &formatContext)
            ThrowIfError(avformat_alloc_output_context2(context, null, null, Config.OutputPath), "Could not create output context");
        if (formatContext == null)
            throw new InvalidOperationException("Could not create output context");

        AVCodec* codec = avcodec_find_encoder_by_name(Config.Encoder);
        if (codec == null)
            throw new InvalidOperationException($"Codec {Config.Encoder} not found");
        videoStream = avformat_new_stream(formatContext, codec);
        if (videoStream == null)
            throw new InvalidOperationException("Could not create video stream");
        videoStream->id = (int)formatContext->nb_streams - 1;
        codecContext = avcodec_alloc_context3(codec);
        if (codecContext == null)
            throw new InvalidOperationException("Could not allocate codec context");
        codecContext->bit_rate = ParseBitrate(Config.Bitrate);
        codecContext->width = Config.Resolution.Width;
        codecContext->height = Config.Resolution.Height;
        codecContext->time_base = new AVRational { num = 1, den = Config.FPS };
        codecContext->framerate = new AVRational { num = Config.FPS, den = 1 };
        videoStream->time_base = codecContext->time_base;
        videoStream->avg_frame_rate = codecContext->framerate;
        codecContext->pix_fmt = AVPixelFormat.AV_PIX_FMT_CUDA;
        codecContext->codec_type = AVMediaType.AVMEDIA_TYPE_VIDEO;
        codecContext->color_range = AVColorRange.AVCOL_RANGE_JPEG;
        codecContext->colorspace = Config.ColorSpace == ColorSpaceMode.BT601 ? AVColorSpace.AVCOL_SPC_BT470BG : AVColorSpace.AVCOL_SPC_BT709;
        codecContext->color_primaries = Config.ColorSpace == ColorSpaceMode.BT601 ? AVColorPrimaries.AVCOL_PRI_BT470BG : AVColorPrimaries.AVCOL_PRI_BT709;
        codecContext->color_trc = Config.ColorSpace == ColorSpaceMode.BT601 ? AVColorTransferCharacteristic.AVCOL_TRC_GAMMA22 : AVColorTransferCharacteristic.AVCOL_TRC_BT709;
        if ((formatContext->oformat->flags & AVFMT_GLOBALHEADER) != 0)
            codecContext->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;
        InitializeCudaFrames();
        NativeEncoderSettings.Apply(codecContext, Config);
        ThrowIfError(avcodec_open2(codecContext, codec, null), "Could not open codec");
        ThrowIfError(avcodec_parameters_from_context(videoStream->codecpar, codecContext), "Could not copy codec parameters");
        if (audioSampleRate > 0)
            audioMuxer = new NativeAudioMuxer(formatContext, audioSampleRate, audioChannels);
        if ((formatContext->oformat->flags & AVFMT_NOFILE) == 0)
            ThrowIfError(avio_open(&formatContext->pb, Config.OutputPath, AVIO_FLAG_WRITE), "Could not open output file");
        ThrowIfError(avformat_write_header(formatContext, null), "Could not write output header");
        packet = av_packet_alloc();
        if (packet == null)
            throw new InvalidOperationException("Could not allocate packet");
        packetPump = new EncoderPacketPump(frame => avcodec_send_frame(codecContext, (AVFrame*)frame),
            () => { int count = ReceivePackets(out int state); return (count, state); },
            error => ThrowIfError(error, "Could not send frame"), AVERROR(EAGAIN), AVERROR_EOF);
        using var contextScope = NvidiaCudaInterop.PushContext(cudaContext);
        for (int index = 0; index < frame_capacity; index++)
        {
            var slot = new FrameSlot();
            frameSlots.Add(slot);
            slot.Frame = av_frame_alloc();
            if (slot.Frame == null)
                throw new InvalidOperationException("Could not allocate frame");
            slot.Ready = new CudaEvent(CUEventFlags.DisableTiming | CUEventFlags.BlockingSync);
            availableFrames.Add(slot);
        }
        Console.WriteLine("[NvidiaGpuFFmpegEncoder] OpenGL texture -> asynchronous CUDA copies -> queued NVENC/mux");
    }

    private void InitializeCudaFrames()
    {
        fixed (AVBufferRef** device = &cudaDeviceRef)
            ThrowIfError(av_hwdevice_ctx_create(device, AVHWDeviceType.AV_HWDEVICE_TYPE_CUDA, null, null, 0), "Could not create CUDA device");
        cudaContext = NvidiaCudaInterop.GetCudaContext(cudaDeviceRef);
        using (NvidiaCudaInterop.PushContext(cudaContext))
        {
            transferStream = new CudaStream(CUStreamFlags.NonBlocking);
            cudaStream = transferStream.Stream;
        }
        cudaFramesRef = av_hwframe_ctx_alloc(cudaDeviceRef);
        if (cudaFramesRef == null)
            throw new InvalidOperationException("Could not allocate CUDA frames context");
        var framesContext = (AVHWFramesContext*)cudaFramesRef->data;
        framesContext->format = AVPixelFormat.AV_PIX_FMT_CUDA;
        framesContext->sw_format = AVPixelFormat.AV_PIX_FMT_NV12;
        framesContext->width = Config.Resolution.Width;
        framesContext->height = Config.Resolution.Height;
        framesContext->initial_pool_size = 4;
        ThrowIfError(av_hwframe_ctx_init(cudaFramesRef), "Could not initialize CUDA frames context");
        codecContext->hw_frames_ctx = av_buffer_ref(cudaFramesRef);
        if (codecContext->hw_frames_ctx == null)
            throw new InvalidOperationException("Could not reference CUDA frames context");
    }

    protected override void _writeFrameInternal(ReadOnlySpan<byte> frame) =>
        throw new NotSupportedException("NVIDIA GPU encoder accepts OpenGL textures only");

    public void PrepareOpenGLTexture(int textureId)
    {
        lock (producerLock)
        {
            worker?.ThrowIfFaulted();
            if (!acceptingFrames || !textures.TryGetValue(textureId, out var texture) || !texture.HasCopy)
                return;
            using var contextScope = NvidiaCudaInterop.PushContext(cudaContext);
            texture.Ready.Synchronize();
        }
    }

    public void WriteOpenGLTexture(int textureId, int width, int height, PixelFormatMode pixelFormat)
    {
        lock (producerLock)
        {
            worker?.ThrowIfFaulted();
            if (!acceptingFrames)
                return;
            if (!AcceptsOpenGLTexture(pixelFormat) || width != Config.Resolution.Width || height != Config.Resolution.Height * 3 / 2)
                throw new InvalidOperationException($"Unexpected NV12 texture size or format: {width}x{height}, {pixelFormat}");
            FrameSlot slot = null;
            try
            {
                slot = availableFrames.Take(worker.CancellationToken);
                slot.CopyCompleted = false;
                ThrowIfError(av_hwframe_get_buffer(cudaFramesRef, slot.Frame, 0), "Could not acquire CUDA frame");
                CopyTextureToFrame(textureId, slot);
                slot.Frame->pts = pts++;
                worker.Submit(new WorkItem(slot));
                slot = null;
            }
            catch (OperationCanceledException)
            {
                worker.ThrowIfFaulted();
                throw;
            }
            finally
            {
                if (slot != null)
                {
                    using var contextScope = NvidiaCudaInterop.PushContext(cudaContext);
                    NvidiaCudaInterop.StreamSynchronize(cudaStream);
                    av_frame_unref(slot.Frame);
                    availableFrames.Add(slot);
                }
            }
        }
    }

    private void CopyTextureToFrame(int textureId, FrameSlot slot)
    {
        using var contextScope = NvidiaCudaInterop.PushContext(cudaContext);
        if (!textures.TryGetValue(textureId, out var texture))
        {
            if (textures.Count == TextureBufferCount)
                throw new InvalidOperationException("The OpenGL texture ring exceeded its capacity");
            texture = new TextureRegistration();
            textures.Add(textureId, texture);
            texture.Ready = new CudaEvent(CUEventFlags.DisableTiming | CUEventFlags.BlockingSync);
            texture.Resource = new CudaOpenGLImageInteropResource((uint)textureId, CUGraphicsRegisterFlags.ReadOnly,
                (CudaOpenGLImageInteropResource.OpenGLImageTarget)gl_texture_2d);
        }
        texture.Resource.Map(cudaStream);
        try
        {
            var source = texture.Resource.GetMappedCUArray(0, 0);
            int height = Config.Resolution.Height;
            int width = Config.Resolution.Width;
            var yPlane = new IntPtr(slot.Frame->data[0]);
            var uvPlane = slot.Frame->data[1] != null ? new IntPtr(slot.Frame->data[1]) : IntPtr.Add(yPlane, slot.Frame->linesize[0] * height);
            int uvPitch = slot.Frame->linesize[1] > 0 ? slot.Frame->linesize[1] : slot.Frame->linesize[0];
            NvidiaCudaInterop.CopyArrayToDevice2DAsync(source, 0, yPlane, slot.Frame->linesize[0], width, height, cudaStream);
            NvidiaCudaInterop.CopyArrayToDevice2DAsync(source, height, uvPlane, uvPitch, width, height / 2, cudaStream);
        }
        finally
        {
            texture.Resource.UnMap(cudaStream);
        }
        texture.Ready.Record(cudaStream);
        texture.HasCopy = true;
        slot.Ready.Record(cudaStream);
    }

    public void WriteAudio(ReadOnlySpan<byte> pcm)
    {
        lock (producerLock)
        {
            worker?.ThrowIfFaulted();
            if (!acceptingFrames)
                return;
            if (audioSampleRate == 0)
                throw new InvalidOperationException("Audio was not configured");
            byte[] buffer = ArrayPool<byte>.Shared.Rent(pcm.Length);
            try
            {
                pcm.CopyTo(buffer);
                worker.Submit(new WorkItem(buffer, pcm.Length));
                buffer = null;
            }
            finally
            {
                if (buffer != null)
                    ArrayPool<byte>.Shared.Return(buffer);
            }
        }
    }

    private void ProcessItem(WorkItem item)
    {
        if (item.Video != null)
        {
            using var contextScope = NvidiaCudaInterop.PushContext(cudaContext);
            item.Video.Ready.Synchronize();
            item.Video.CopyCompleted = true;
            packetPump.Send((IntPtr)item.Video.Frame);
        }
        else
        {
            audioMuxer.Write(item.Audio.AsSpan(0, item.AudioLength));
        }
    }

    private void ReleaseItem(WorkItem item)
    {
        if (item.Video != null)
        {
            using var contextScope = NvidiaCudaInterop.PushContext(cudaContext);
            if (!item.Video.CopyCompleted)
                item.Video.Ready.Synchronize();
            av_frame_unref(item.Video.Frame);
            availableFrames.Add(item.Video);
        }
        else
        {
            ArrayPool<byte>.Shared.Return(item.Audio);
        }
    }

    private int ReceivePackets(out int state)
    {
        int count = 0;
        while (true)
        {
            state = avcodec_receive_packet(codecContext, packet);
            if (state == AVERROR(EAGAIN) || state == AVERROR_EOF)
                return count;
            ThrowIfError(state, "Could not receive encoded packet");
            try
            {
                // Preserve the final CFR sample when the encoder omits its duration
                if (packet->duration <= 0)
                    packet->duration = 1;
                av_packet_rescale_ts(packet, codecContext->time_base, videoStream->time_base);
                packet->stream_index = videoStream->index;
                ThrowIfError(av_interleaved_write_frame(formatContext, packet), "Could not mux video packet");
                count++;
            }
            finally
            {
                av_packet_unref(packet);
            }
        }
    }

    private void FinalizeEncoder()
    {
        packetPump.Send(IntPtr.Zero);
        audioMuxer?.Finish();
        ThrowIfError(av_write_trailer(formatContext), "Could not write output trailer");
    }

    protected override void _finishInternal()
    {
        lock (producerLock)
        {
            if (finished)
                return;
            finished = true;
            acceptingFrames = false;
        }
        try { worker?.Dispose(); }
        finally { availableFrames.Dispose(); }
    }

    private void ReleaseResources()
    {
        lock (producerLock)
        {
            acceptingFrames = false;
            ExceptionDispatchInfo cleanupFailure = null;
            void Release(Action action)
            {
                try { action(); }
                catch (Exception exception) { cleanupFailure ??= ExceptionDispatchInfo.Capture(exception); }
            }
            if (cudaContext.Pointer != IntPtr.Zero)
            {
                IDisposable contextScope = null;
                Release(() => contextScope = NvidiaCudaInterop.PushContext(cudaContext));
                if (transferStream != null)
                    Release(() => NvidiaCudaInterop.StreamSynchronize(cudaStream));
                foreach (var texture in textures.Values)
                {
                    Release(() => texture.Resource?.Dispose());
                    Release(() => texture.Ready?.Dispose());
                }
                textures.Clear();
                foreach (var slot in frameSlots)
                    Release(() => slot.Ready?.Dispose());
                Release(() => transferStream?.Dispose());
                transferStream = null;
                Release(() => contextScope?.Dispose());
            }
            foreach (var slot in frameSlots)
            {
                AVFrame* frame = slot.Frame;
                av_frame_free(&frame);
            }
            frameSlots.Clear();
            Release(() => audioMuxer?.Dispose());
            audioMuxer = null;
            fixed (AVPacket** packetPointer = &packet) av_packet_free(packetPointer);
            fixed (AVCodecContext** codec = &codecContext) avcodec_free_context(codec);
            fixed (AVBufferRef** frames = &cudaFramesRef) av_buffer_unref(frames);
            fixed (AVBufferRef** device = &cudaDeviceRef) av_buffer_unref(device);
            if (formatContext != null)
            {
                if (formatContext->pb != null)
                    Release(() => ThrowIfError(avio_closep(&formatContext->pb), "Could not close output file"));
                avformat_free_context(formatContext);
                formatContext = null;
            }
            videoStream = null;
            cleanupFailure?.Throw();
        }
    }

    public void Dispose() => Finish();

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

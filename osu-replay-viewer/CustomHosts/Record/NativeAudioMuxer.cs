using System;
using System.Buffers.Binary;
using System.IO;
using FFmpeg.AutoGen;
using static FFmpeg.AutoGen.ffmpeg;

namespace osu_replay_renderer_netcore.CustomHosts.Record;

internal unsafe sealed class NativeAudioMuxer : IDisposable
{
    private readonly AVFormatContext* format;
    private AVCodecContext* codec;
    private AVStream* stream;
    private AVFrame* frame;
    private AVPacket* packet;
    private byte[] pending;
    private int pendingBytes;
    private int channels;
    private long nextSample;
    private bool finished;

    public NativeAudioMuxer(AVFormatContext* format, int sampleRate, int channels, int bitrate = 192000)
    {
        if (format == null) throw new ArgumentNullException(nameof(format));
        if (bitrate <= 0) throw new ArgumentOutOfRangeException(nameof(bitrate));
        if (sampleRate <= 0 || channels < 1 || channels > 8)
            throw new ArgumentOutOfRangeException(nameof(sampleRate));
        this.format = format;
        this.channels = channels;
        try
        {
            AVCodec* encoder = avcodec_find_encoder(AVCodecID.AV_CODEC_ID_AAC);
            if (encoder == null) throw new InvalidOperationException("The FFmpeg libraries do not provide an AAC encoder");
            codec = avcodec_alloc_context3(encoder);
            if (codec == null) throw new OutOfMemoryException("Could not allocate the AAC codec context");
            codec->sample_rate = sampleRate;
            codec->channels = channels;
            codec->channel_layout = (ulong)av_get_default_channel_layout(channels);
            codec->sample_fmt = AVSampleFormat.AV_SAMPLE_FMT_FLTP;
            codec->bit_rate = bitrate;
            codec->time_base = new AVRational { num = 1, den = sampleRate };
            if ((format->oformat->flags & AVFMT_GLOBALHEADER) != 0)
                codec->flags |= AV_CODEC_FLAG_GLOBAL_HEADER;
            Check(avcodec_open2(codec, encoder, null), "Could not open the AAC encoder at the song sample rate");
            stream = avformat_new_stream(format, null);
            if (stream == null) throw new OutOfMemoryException("Could not allocate the audio stream");
            stream->time_base = codec->time_base;
            Check(avcodec_parameters_from_context(stream->codecpar, codec), "Could not copy AAC stream parameters");
            frame = av_frame_alloc();
            packet = av_packet_alloc();
            if (frame == null || packet == null) throw new OutOfMemoryException("Could not allocate AAC buffers");
            frame->format = (int)codec->sample_fmt;
            frame->channel_layout = codec->channel_layout;
            frame->sample_rate = sampleRate;
            frame->nb_samples = codec->frame_size;
            if (frame->nb_samples <= 0) throw new InvalidOperationException("AAC encoder returned an invalid frame size");
            Check(av_frame_get_buffer(frame, 0), "Could not allocate the AAC frame buffer");
            pending = new byte[checked(frame->nb_samples * channels * sizeof(short))];
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Write(ReadOnlySpan<byte> pcm)
    {
        if (finished || codec == null) throw new InvalidOperationException("The audio stream is closed");
        if (pcm.Length % (channels * sizeof(short)) != 0)
            throw new ArgumentException("PCM data must contain complete interleaved s16 frames", nameof(pcm));
        while (!pcm.IsEmpty)
        {
            int count = Math.Min(pending.Length - pendingBytes, pcm.Length);
            pcm[..count].CopyTo(pending.AsSpan(pendingBytes));
            pendingBytes += count;
            pcm = pcm[count..];
            if (pendingBytes == pending.Length)
            {
                Encode(pendingBytes / (channels * sizeof(short)));
                pendingBytes = 0;
            }
        }
    }

    private void Encode(int samples)
    {
        Check(av_frame_make_writable(frame), "Could not acquire an AAC frame buffer");
        frame->nb_samples = samples;
        for (int channel = 0; channel < channels; channel++)
        {
            float* destination = (float*)frame->extended_data[channel];
            for (int sample = 0; sample < samples; sample++)
                destination[sample] = BinaryPrimitives.ReadInt16LittleEndian(pending.AsSpan((sample * channels + channel) * sizeof(short))) / 32768f;
        }
        frame->pts = nextSample;
        Send(frame);
        nextSample += samples;
    }

    private void Send(AVFrame* input)
    {
        int result = avcodec_send_frame(codec, input);
        if (result == AVERROR(EAGAIN))
        {
            Receive();
            result = avcodec_send_frame(codec, input);
        }
        Check(result, "Could not submit AAC samples");
        Receive();
    }

    private void Receive()
    {
        while (true)
        {
            int result = avcodec_receive_packet(codec, packet);
            if (result == AVERROR(EAGAIN) || result == AVERROR_EOF) return;
            Check(result, "Could not receive an AAC packet");
            try
            {
                av_packet_rescale_ts(packet, codec->time_base, stream->time_base);
                packet->stream_index = stream->index;
                Check(av_interleaved_write_frame(format, packet), "Could not write the audio packet");
            }
            finally
            {
                av_packet_unref(packet);
            }
        }
    }

    public void Finish()
    {
        if (finished || codec == null) return;
        finished = true;
        if (pendingBytes > 0)
        {
            Encode(pendingBytes / (channels * sizeof(short)));
            pendingBytes = 0;
        }
        Send(null);
    }

    public void Dispose()
    {
        fixed (AVPacket** address = &packet) av_packet_free(address);
        fixed (AVFrame** address = &frame) av_frame_free(address);
        fixed (AVCodecContext** address = &codec) avcodec_free_context(address);
        pending = null;
        stream = null;
        pendingBytes = 0;
        finished = true;
    }

    private static void Check(int result, string operation)
    {
        if (result >= 0) return;
        const int size = 256;
        byte* error = stackalloc byte[size];
        av_strerror(result, error, size);
        throw new IOException($"{operation}: {System.Runtime.InteropServices.Marshal.PtrToStringAnsi((IntPtr)error)}");
    }
}

using System;
using System.Globalization;
using FFmpeg.AutoGen;
using static FFmpeg.AutoGen.ffmpeg;

namespace osu_replay_renderer_netcore.CustomHosts.Record;

internal static unsafe class NativeEncoderSettings
{
    public static void Apply(AVCodecContext* codec, EncoderConfig config)
    {
        bool Supports(string name) => codec->priv_data != null && av_opt_find(codec->priv_data, name, null, 0, 0) != null;
        foreach (var option in FFmpegEncoderOptions.BuildNative(config, Supports))
        {
            if (av_opt_set(codec->priv_data, option.Key, option.Value, 0) < 0)
                throw new ArgumentException($"The FFmpeg encoder rejected {option.Key}={option.Value}");
        }

        if (config.GopSize.HasValue)
            codec->gop_size = config.GopSize.Value;

        if (config.RateControl is VideoRateControl.ConstantBitrate or VideoRateControl.VariableBitrate)
        {
            codec->bit_rate = ParseBitrate(config.Bitrate);
            if (config.RateControl == VideoRateControl.ConstantBitrate)
            {
                codec->rc_min_rate = codec->bit_rate;
                codec->rc_max_rate = codec->bit_rate;
                codec->rc_buffer_size = checked((int)ParseBitrate(string.IsNullOrWhiteSpace(config.BufferSize) ? config.Bitrate : config.BufferSize));
            }
        }
        else if (config.RateControl == VideoRateControl.ConstantQuality || config.Quality.HasValue)
        {
            if (config.Encoder.EndsWith("_nvenc", StringComparison.Ordinal))
                codec->bit_rate = 0;
            if (config.Encoder.EndsWith("_qsv", StringComparison.Ordinal))
            {
                codec->global_quality = checked((int)Math.Round(config.Quality ?? 21));
                codec->bit_rate = 0;
            }
            if (config.Encoder.EndsWith("_videotoolbox", StringComparison.Ordinal))
            {
                codec->global_quality = checked((int)Math.Round((config.Quality ?? 21) * FF_QP2LAMBDA));
                codec->flags |= AV_CODEC_FLAG_QSCALE;
                codec->bit_rate = 0;
            }
        }
        if (config.RateControl != VideoRateControl.ConstantBitrate)
        {
            if (!string.IsNullOrWhiteSpace(config.MaxBitrate)) codec->rc_max_rate = ParseBitrate(config.MaxBitrate);
            if (!string.IsNullOrWhiteSpace(config.BufferSize)) codec->rc_buffer_size = checked((int)ParseBitrate(config.BufferSize));
        }
    }

    private static long ParseBitrate(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) throw new ArgumentException("Bitrate must be positive");
        text = text.Trim();
        double factor = 1;
        switch (char.ToUpperInvariant(text[^1]))
        {
            case 'K': factor = 1000; text = text[..^1]; break;
            case 'M': factor = 1000000; text = text[..^1]; break;
            case 'G': factor = 1000000000; text = text[..^1]; break;
        }
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double value) ||
            !double.IsFinite(value) || value <= 0 || value * factor > long.MaxValue)
            throw new ArgumentException("Bitrate must be a positive number with an optional K, M or G suffix");
        return checked((long)(value * factor));
    }
}

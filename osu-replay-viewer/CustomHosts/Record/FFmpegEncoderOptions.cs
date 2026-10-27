using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;

namespace osu_replay_renderer_netcore.CustomHosts.Record;

internal sealed class FFmpegEncoderCapabilities
{
    private static readonly ConcurrentDictionary<(string Executable, string Encoder), Lazy<FFmpegEncoderCapabilities>> cache = new();
    private readonly HashSet<string> options;

    internal FFmpegEncoderCapabilities(IEnumerable<string> options) => this.options = new HashSet<string>(options, StringComparer.Ordinal);
    internal bool Supports(string option) => options.Contains(option);

    internal static FFmpegEncoderCapabilities Get(string executable, string encoder) =>
        cache.GetOrAdd((executable, encoder), key => new Lazy<FFmpegEncoderCapabilities>(() => Probe(key.Executable, key.Encoder))).Value;

    private static FFmpegEncoderCapabilities Probe(string executable, string encoder)
    {
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            }
        };
        foreach (var argument in new[] { "-hide_banner", "-h", $"encoder={encoder}" })
            process.StartInfo.ArgumentList.Add(argument);
        process.Start();
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(10000))
        {
            process.Kill(true);
            process.WaitForExit();
            throw new InvalidOperationException($"FFmpeg encoder capability probe timed out: {encoder}");
        }
        string help = output.GetAwaiter().GetResult() + error.GetAwaiter().GetResult();
        if (process.ExitCode != 0 || !help.Contains($"Encoder {encoder} [", StringComparison.Ordinal))
            throw new NotSupportedException($"FFmpeg does not provide encoder {encoder}: {help.Trim()}");
        var names = new List<string>();
        foreach (Match match in Regex.Matches(help, @"(?m)^\s+-(\w+)\s+<"))
            names.Add(match.Groups[1].Value);
        return new FFmpegEncoderCapabilities(names);
    }
}

internal static class FFmpegEncoderOptions
{
    internal static IReadOnlyDictionary<string, string> BuildNative(EncoderConfig config, Func<string, bool> supports)
    {
        string[] privateNames = { "preset", "rc", "qp", "qp_i", "qp_p", "qp_b", "crf", "cq", "qvbr_quality_level", "constant_bit_rate", "quality" };
        var capabilities = new FFmpegEncoderCapabilities(Array.FindAll(privateNames, option => supports(option)));
        var arguments = Build(config, capabilities);
        var result = new Dictionary<string, string>();
        for (int i = 0; i < arguments.Count; i += 2)
        {
            string option = arguments[i][1..];
            if (Array.IndexOf(privateNames, option) >= 0)
                result.Add(option, arguments[i + 1]);
        }
        return result;
    }

    internal static List<string> Build(EncoderConfig config, FFmpegEncoderCapabilities capabilities)
    {
        var arguments = new List<string>();
        if (config.GopSize is <= 0)
            throw new ArgumentOutOfRangeException(nameof(config.GopSize), "GOP size must be a positive frame count");
        if (config.AudioBitrate is <= 0)
            throw new ArgumentOutOfRangeException(nameof(config.AudioBitrate), "Audio bitrate must be positive, in bits per second");
        string name = config.Encoder ?? throw new ArgumentException("An encoder must be selected");
        bool nvenc = name.EndsWith("_nvenc", StringComparison.Ordinal);
        bool qsv = name.EndsWith("_qsv", StringComparison.Ordinal);
        bool amf = name.EndsWith("_amf", StringComparison.Ordinal);
        bool videoToolbox = name.EndsWith("_videotoolbox", StringComparison.Ordinal);
        bool x26x = name is "libx264" or "libx265";
        double quality = config.Quality ?? 21;
        if (!double.IsFinite(quality) || quality < 0)
            throw new ArgumentOutOfRangeException(nameof(config.Quality));
        string qualityText = quality.ToString(CultureInfo.InvariantCulture);

        void Add(string option, string value)
        {
            arguments.Add("-" + option);
            arguments.Add(value);
        }
        void Private(string option, string value)
        {
            if (!capabilities.Supports(option))
                throw new NotSupportedException($"FFmpeg encoder {name} does not support {option}");
            Add(option, value);
        }

        var mode = config.RateControl;
        if (mode == null)
        {
            if (nvenc)
                mode = VideoRateControl.ConstantQuantizer;
            else if (x26x)
                mode = VideoRateControl.ConstantQuality;
            else if (config.Quality != null)
                mode = VideoRateControl.ConstantQuality;
        }

        if (mode.HasValue && !Enum.IsDefined(mode.Value))
            throw new ArgumentOutOfRangeException(nameof(config.RateControl));
        if (config.Quality.HasValue && mode is VideoRateControl.ConstantBitrate or VideoRateControl.VariableBitrate)
            throw new ArgumentException("Quality applies to ConstantQuality or ConstantQuantizer rate control");
        if (mode == VideoRateControl.ConstantQuantizer &&
            (!string.IsNullOrWhiteSpace(config.MaxBitrate) || !string.IsNullOrWhiteSpace(config.BufferSize)))
            throw new ArgumentException("ConstantQuantizer does not support bitrate limits; select bitrate or constant quality rate control");
        if (mode is VideoRateControl.ConstantQuality or VideoRateControl.ConstantQuantizer)
        {
            if ((nvenc || qsv || amf || x26x) && quality > 51 || videoToolbox && quality > 100)
                throw new ArgumentOutOfRangeException(nameof(config.Quality), "Quality is outside the encoder's supported range");
            if ((mode == VideoRateControl.ConstantQuantizer || amf) && quality != Math.Truncate(quality))
                throw new ArgumentException("The selected encoder requires an integer quality value");
        }

        switch (mode)
        {
            case VideoRateControl.ConstantQuantizer:
                if (nvenc)
                {
                    Private("rc", "constqp");
                    Private("qp", qualityText);
                }
                else if (amf)
                {
                    Private("rc", "cqp");
                    Private("qp_i", qualityText);
                    Private("qp_p", qualityText);
                    if (capabilities.Supports("qp_b")) Private("qp_b", qualityText);
                }
                else if (x26x)
                    Private("qp", qualityText);
                else
                    throw new NotSupportedException($"Constant quantizer mode is not supported for {name}");
                break;
            case VideoRateControl.ConstantQuality:
                if (x26x)
                    Private("crf", qualityText);
                else if (nvenc)
                {
                    Private("rc", "vbr");
                    Private("cq", qualityText);
                    Add("b:v", "0");
                }
                else if (qsv)
                    Add("global_quality", qualityText);
                else if (amf)
                {
                    Private("rc", "qvbr");
                    Private("qvbr_quality_level", qualityText);
                }
                else if (videoToolbox)
                    Add("q:v", qualityText);
                else
                    throw new NotSupportedException($"Constant quality mode is not supported for {name}");
                break;
            case VideoRateControl.VariableBitrate:
            case VideoRateControl.ConstantBitrate:
                if (string.IsNullOrWhiteSpace(config.Bitrate))
                    throw new ArgumentException("Bitrate is required for bitrate rate control");
                bool constant = mode == VideoRateControl.ConstantBitrate;
                if (nvenc) Private("rc", constant ? "cbr" : "vbr");
                if (amf) Private("rc", constant ? "cbr" : "vbr_peak");
                if (videoToolbox && constant) Private("constant_bit_rate", "1");
                Add("b:v", config.Bitrate);
                if (constant)
                {
                    Add("minrate", config.Bitrate);
                    Add("maxrate", config.Bitrate);
                    Add("bufsize", string.IsNullOrWhiteSpace(config.BufferSize) ? config.Bitrate : config.BufferSize);
                }
                break;
        }

        if (mode != VideoRateControl.ConstantBitrate)
        {
            if (!string.IsNullOrWhiteSpace(config.MaxBitrate)) Add("maxrate", config.MaxBitrate);
            if (!string.IsNullOrWhiteSpace(config.BufferSize)) Add("bufsize", config.BufferSize);
        }
        if (!string.IsNullOrWhiteSpace(config.Preset))
        {
            if (capabilities.Supports("preset"))
                Private("preset", config.Preset);
            else if (amf && capabilities.Supports("quality"))
                Private("quality", config.Preset switch { "slow" => "quality", "medium" => "balanced", "fast" => "speed", _ => config.Preset });
        }
        if (config.GopSize.HasValue)
            Add("g", config.GopSize.Value.ToString(CultureInfo.InvariantCulture));
        return arguments;
    }
}

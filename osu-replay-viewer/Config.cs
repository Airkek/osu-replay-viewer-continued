using System.Drawing;
using Newtonsoft.Json;
using System.IO;
using Newtonsoft.Json.Converters;
using osu_replay_renderer_netcore.CustomHosts;
using osu_replay_renderer_netcore.CustomHosts.Record;
using osu.Framework;

namespace osu_replay_renderer_netcore;

public enum FFmpegMode
{
    Pipe,
    Binding
}

public class GameSettings
{
    [JsonProperty("skip_intro")] public bool SkipIntro = false;
    [JsonProperty("background_dim")] public double BackgroundDim = 0.75;
    [JsonProperty("show_storyboard_or_video")] public bool ShowStoryboard = true;
    [JsonProperty("use_beatmap_hitsounds")] public bool BeatmapHitsounds = false;
    [JsonProperty("use_beatmap_skin")] public bool BeatmapSkin = false;
    [JsonProperty("use_beatmap_colors")] public bool BeatmapColors = false;
    [JsonProperty("music_volume")] public double VolumeMusic = 0.6;
    [JsonProperty("effects_volume")] public double VolumeEffects = 0.6;
    [JsonProperty("master_volume")] public double VolumeMaster = 0.6;
}

public class Config
{
    public class RecordOptionsObject
    {
        [JsonProperty("fps")] public int FrameRate = 60;
        [JsonProperty("resolution")] public string Resolution = "1280x720";
        [JsonProperty("renderer")] public GlRenderer Renderer = GlRenderer.Auto;
    }
    [JsonProperty("record_options")] public RecordOptionsObject RecordOptions = new();
    
    public class FFmpegOptionsObject
    {
        [JsonProperty("mode")] public FFmpegMode Mode = FFmpegMode.Pipe;
        [JsonProperty("libraries_path")] public string LibrariesPath = string.Empty;
        [JsonProperty("ffmpeg_executable")] public string Executable = "ffmpeg";
        [JsonProperty("video_encoder")] public string VideoEncoder = "libx264";
        [JsonProperty("video_encoder_preset")] public string VideoEncoderPreset = "slow";
        [JsonProperty("video_encoder_bitrate")] public string VideoEncoderBitrate = "100M";
        
    }
    [JsonProperty("ffmpeg_options")] public FFmpegOptionsObject FFmpegOptions = new();
    
    public class OutputOptionsObject
    {
        [JsonProperty("pixel_format")] public PixelFormatMode PixelFormat = PixelFormatMode.RGB;
    }
    [JsonProperty("output_options")] public OutputOptionsObject OutputOptions = new();

    [JsonProperty("game_settings")] public GameSettings GameSettings = new();
    
    private Config() { }

    public static Config ReadFromFile(string file)
    {
        Config res = null;
        if (File.Exists(file))
        {
            var configText = File.ReadAllText(file);
            res = JsonConvert.DeserializeObject<Config>(configText, new StringEnumConverter());
        }
        else
        {
            res = new Config();
        }
        
        res.SaveToFile(file); // update schema
        return res;
    }

    public void SaveToFile(string file)
    {
        var config = JsonConvert.SerializeObject(this, Formatting.Indented, new StringEnumConverter());
        File.WriteAllText(file, config);
    }
}
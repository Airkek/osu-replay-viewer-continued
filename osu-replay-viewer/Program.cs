using osu.Framework;
using osu.Framework.Platform;
using osu_replay_renderer_netcore.CLI;
using osu_replay_renderer_netcore.CustomHosts;
using osu_replay_renderer_netcore.Patching;
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using osu_replay_renderer_netcore.Audio.Conversion;
using osu_replay_renderer_netcore.CustomHosts.CustomClocks;
using osu_replay_renderer_netcore.CustomHosts.Record;
using osu.Framework.Timing;

namespace osu_replay_renderer_netcore
{
    class Program
    {
        const string GAME_NAME = "osu_replay_viewer";
        const string OSU_GAME_NAME = "osu";
        
        static void Main(string[] args)
        {
            // Command tree
            OptionDescription alwaysYes;
            OptionDescription modOverride;
            OptionDescription query;
            OptionDescription osuGameName;
            
            OptionDescription generalHelp;
            OptionDescription generalList;
            OptionDescription generalView;

            OptionDescription recordMode;
            OptionDescription recordRenderer;
            OptionDescription recordOutput;
            OptionDescription recordResolution;
            OptionDescription recordFPS;

            OptionDescription ffmpegType;
            OptionDescription ffmpegLibPath;
            OptionDescription ffmpegExec;
            OptionDescription ffmpegPreset;
            OptionDescription ffmpegFramesBlending;
            OptionDescription ffmpegMotionInterpolation;
            OptionDescription ffmpegVideoEncoder;
            OptionDescription ffmpegBitrate;
            OptionDescription ffmpegShowOutput;

            OptionDescription experimental;
            OptionDescription overrideOverlayOptions;
            OptionDescription applySkin;
            OptionDescription listSkins;
            OptionDescription beatmapImport;
            
            CommandLineProcessor cli = new()
            {
                Options = new[]
                {
                    // General
                    alwaysYes = new()
                    {
                        Name = "Always Yes",
                        Description = "Always answer yes to all prompts. Similar to 'command | yes'",
                        DoubleDashes = new[] { "yes" }
                    },
                    modOverride = new()
                    {
                        Name = "Mod Override",
                        Description = "Override Mod(s). You can use 'no-mod' or 'acronyms:NM' to clear all mods",
                        DoubleDashes = new[] { "mod-override" },
                        SingleDash = new[] { "MOD" },
                        Parameters = new[] { "<Mod Name/acronyms:AC>" }
                    },
                    query = new()
                    {
                        Name = "Query",
                        Description = "Query data (Eg: find something in help index or query replays)",
                        DoubleDashes = new[] { "query" },
                        SingleDash = new[] { "q" },
                        Parameters = new[] { "Keyword" }
                    },
                    osuGameName = new()
                    {
                        Name = "osu!lazer mode",
                        Description = "Use osu!lazer data (songs, skins, replays)",
                        DoubleDashes = new[] { "osu-mode" },
                        SingleDash = new[] { "osu" }
                    },
                    beatmapImport = new()
                    {
                        Name = "Import beatmap",
                        Description = "Import beatmap from file",
                        DoubleDashes = new[] { "import-beatmap" },
                        SingleDash = new[] { "osz" },
                        Parameters = new[] { "path/to/File.osz" }
                    },
                    generalList = new()
                    {
                        Name = "List Replays",
                        Description = "List all local replays",
                        DoubleDashes = new[] { "list" },
                        SingleDash = new[] { "list", "l" }
                    },
                    generalView = new()
                    {
                        Name = "View Replay",
                        Description = "Select a replay to view. This options must be always present (excluding -list options)",
                        DoubleDashes = new[] { "view" },
                        SingleDash = new[] { "view", "i" },
                        Parameters = new[] { "Type (local/online/file/auto)", "Score GUID/Beatmap ID (auto)/File.osr" }
                    },
                    generalHelp = new()
                    {
                        Name = "Help Index",
                        Description = "View help with details",
                        DoubleDashes = new[] { "help" },
                        SingleDash = new[] { "h" }
                    },

                    // Record options
                    recordMode = new()
                    {
                        Name = "Record Mode",
                        Description = "Switch to record mode",
                        DoubleDashes = new[] { "record" },
                        SingleDash = new[] { "R" }
                    },
                    recordRenderer = new()
                    {
                        Name = "Record mode Renderer",
                        Description = "Select osu!framework renderer for record mode",
                        DoubleDashes = new[] { "record-renderer" },
                        SingleDash = new[] { "RR" },
                        Parameters = new []{ "Type (auto/veldrid/deferred/legacy)" },
                        ProcessedParameters = new [] { "auto" }
                    },
                    recordOutput = new()
                    {
                        Name = "Record Output",
                        Description = "Set record output",
                        DoubleDashes = new[] { "record-output" },
                        SingleDash = new[] { "O" },
                        Parameters = new[] { "Output = osu-replay.mp4" },
                        ProcessedParameters = new[] { "osu-replay.mp4" }
                    },
                    recordResolution = new()
                    {
                        Name = "Record Resolution",
                        Description = "Set the output resolution",
                        DoubleDashes = new[] { "record-resolution" },
                        SingleDash = new[] { "RSL" },
                        Parameters = new[] { "Width = 1280", "Height = 720" },
                        ProcessedParameters = new[] { "1280", "720" }
                    },
                    recordFPS = new()
                    {
                        Name = "Record FPS",
                        Description = "Set the output FPS",
                        DoubleDashes = new[] { "record-fps" },
                        SingleDash = new[] { "FPS" },
                        Parameters = new[] { "FPS = 60" },
                        ProcessedParameters = new[] { "60" }
                    },

                    // FFmpeg options
                    ffmpegType = new()
                    {
                        Name = "FFmpeg type",
                        Description = "Which type of ffmpeg should we use",
                        DoubleDashes = new[] { "ffmpeg-type" },
                        SingleDash = new[] { "FT" },
                        Parameters = new[] { "Type (external/bindings)" },
                        ProcessedParameters = new[] { "pipe" }
                    },
                    ffmpegLibPath = new()
                    {
                        Name = "FFmpeg folder path",
                        Description = "Path to directory with ffmpeg binary/libs",
                        DoubleDashes = new[] { "ffmpeg-path" },
                        SingleDash = new[] { "FLP" },
                        Parameters = new[] { "Path" },
                    },
                    ffmpegExec = new()
                    {
                        Name = "FFmpeg executable path",
                        Description = "Path to ffmpeg executable binary",
                        DoubleDashes = new[] { "ffmpeg-exec" },
                        SingleDash = new[] { "FEXE" },
                        Parameters = new[] { "Path" },
                        ProcessedParameters = new[] { "ffmpeg" }
                    },
                    ffmpegPreset = new()
                    {
                        Name = "FFmpeg H264 Encoding Preset",
                        Description = "Set the FFmpeg H264 Encoding preset",
                        DoubleDashes = new[] { "ffmpeg-preset" },
                        SingleDash = new[] { "FPR" },
                        Parameters = new[] { "Preset = slow" },
                        ProcessedParameters = new[] { "slow" }
                    },
                    ffmpegFramesBlending = new()
                    {
                        Name = "FFmpeg Frames Blending",
                        Description = "Blend multiple frames to create smooth transition. Default is 1x",
                        DoubleDashes = new[] { "ffmpeg-frames-blending" },
                        SingleDash = new[] { "FBL" },
                        Parameters = new[] { "Blending = 1" },
                        ProcessedParameters = new[] { "1" }
                    },
                    ffmpegMotionInterpolation = new()
                    {
                        Name = "FFmpeg Motion Interpolation",
                        Description = "Use motion interpolation to create smooth transition",
                        DoubleDashes = new[] { "ffmpeg-minterpolation" },
                        SingleDash = new[] { "FMI" }
                    },
                    ffmpegVideoEncoder = new()
                    {
                        Name = "FFmpeg Video Encoder",
                        Description = "Set video encoder for FFmpeg. 'ffmpeg -encoders' for the list",
                        DoubleDashes = new[] { "ffmpeg-encoder" },
                        SingleDash = new[] { "FENC" },
                        Parameters = new[] { "Encoder (libx264/h264_nvenc/h264_qsv/h264_amf/h264_videotoolbox)" },
                        ProcessedParameters = new[] { "libx264" }
                    },
                    ffmpegBitrate = new()
                    {
                        Name = "FFmpeg Global Quality",
                        Description = "Set the max bitrate for output video",
                        DoubleDashes = new[] { "ffmpeg-bitrate" },
                        SingleDash = new[] { "FQ" },
                        Parameters = new[] { "Bitrate = 100M" },
                        ProcessedParameters = new[] { "100M" }
                    },
                    ffmpegShowOutput = new()
                    {
                        Name = "Show ffmpeg output",
                        Description = "Show ffmpeg output (applicable only to external ffmpeg)",
                        DoubleDashes = new[] { "ffmpeg-show-output" },
                        SingleDash = new[] { "FSO" }
                    },

                    // Misc
                    experimental = new()
                    {
                        Name = "Experimental Toggle",
                        Description = "Toggle experimental feature",
                        DoubleDashes = new[] { "experimental" },
                        SingleDash = new[] { "experimental" },
                        Parameters = new[] { "Flag" }
                    },
                    overrideOverlayOptions = new()
                    {
                        Name = "Override Overlay Options",
                        Description = "Control the visiblity of player overlay",
                        DoubleDashes = new[] { "overlay-override" },
                        SingleDash = new[] { "overlay" },
                        Parameters = new[] { "true/false" }
                    },
                    applySkin = new()
                    {
                        Name = "Select Skin",
                        Description = "Select a skin to use in replay",
                        DoubleDashes = new[] { "skin" },
                        SingleDash = new[] { "skin", "s" },
                        Parameters = new[] { "Type (import/select)", "Skin name/File.osk" }
                    },
                    listSkins = new()
                    {
                        Name = "List Skins",
                        Description = "List all available skins",
                        DoubleDashes = new[] { "list-skin", "list-skins" },
                        SingleDash = new [] { "lskins", "lskin" }
                    }
                }
            };


            var patched = false;
            // Apply patches
            if (ShouldApplyPatch(args))
            { 
                new AudioPatcher().DoPatching();
                new ClockPatcher().DoPatching();
                new RenderPatcher().DoPatching();
                new WindowPatcher().DoPatching();
                patched = true;
            }

            var modsOverride = new List<string>();
            var experimentalFlags = new List<string>();
            
            modOverride.OnOptions += (args) => { modsOverride.Add(args[0]); };
            experimental.OnOptions += (args) => { experimentalFlags.Add(args[0]); };
            GameHost host;
            OsuGameRecorder game;

            try
            {
                var progParams = cli.ProcessOptionsAndFilter(args);
                if (args.Length == 0 || generalHelp.Triggered)
                {
                    Console.WriteLine("Usage:");
                    Console.WriteLine("  dotnet run osu-replay-viewer [options...]");
                    Console.WriteLine("  osu-replay-viewer [options...]");
                    Console.WriteLine();
                    cli.PrintHelp(generalHelp.Triggered, query.Triggered ? query[0] : null);
                    return;
                }

                var gameName = GAME_NAME;

                if (osuGameName.Triggered)
                {
                    gameName = OSU_GAME_NAME;
                }

                if (recordMode.Triggered)
                {
                    if (!CLIUtils.AskFileDelete(alwaysYes.Triggered, recordOutput[0])) return;

                    var fps = ParseIntOrThrow(recordFPS[0]);
                    var blending = ParseIntOrThrow(ffmpegFramesBlending[0]);
                    
                    var recordClock = new RecordClock(fps * blending);
                    if (patched)
                    {
                        ClockPatcher.OnStopwatchClockSetAsSource += clock =>
                        {
                            clock.ChangeSource(new WrappedClock(recordClock, clock.Source as StopwatchClock));
                        };
                    }

                    var resolution = new Size
                    {
                        Width = ParseIntOrThrow(recordResolution[0]),
                        Height = ParseIntOrThrow(recordResolution[1])
                    };

                    var rendererType = ParseRenderer(recordRenderer[0]);

                    var config = new EncoderConfig
                    {
                        FPS = fps,
                        Resolution = resolution,
                        OutputPath = recordOutput[0],
                        Preset = ffmpegPreset[0],
                        Encoder = ffmpegVideoEncoder[0],
                        Bitrate = ffmpegBitrate[0],
                        
                        // External only
                        FFmpegExec = ffmpegExec[0],
                        ShowFFmpegOutput = ffmpegShowOutput.Triggered,

                        // Smoothing options (external only atm)
                        FramesBlending = blending,
                        MotionInterpolation = ffmpegMotionInterpolation.Triggered,
                    };
                    
                    FFmpegAudioTools.FFmpegExec = ffmpegExec[0];
                    FFmpegAudioTools.ShowOutput = ffmpegShowOutput.Triggered;

                    if (ffmpegLibPath.Triggered)
                    {
                        config.FFmpegPath = ffmpegLibPath[0];
                    }

                    EncoderBase encoder = ffmpegType[0] switch
                    {
                        "pipe" or "external" => new ExternalFFmpegEncoder(config),
                        "bindings" => new FFmpegAutoGenEncoder(config),
                        _ => throw new CLIException
                        {
                            Cause = "Command-line Arguments (Parsing)",
                            DisplayMessage = $"Value {ffmpegType[0]} is invaild"
                        }
                    };

                    host = new ReplayRecordGameHost(gameName, encoder, recordClock, rendererType, patched);
                }
                else
                {
                    host = Host.GetSuitableDesktopHost(gameName);
                }
                
                game = new OsuGameRecorder();
                game.ModsOverride = modsOverride;
                game.ExperimentalFlags = experimentalFlags;

                if (applySkin.Triggered)
                {
                    game.SkinActionType = (SkinAction)Enum.Parse(typeof(SkinAction), applySkin[0][0].ToString().ToUpper() + applySkin[0].Substring(1));
                    game.Skin = applySkin[1];
                }

                if (beatmapImport.Triggered)
                {
                    game.BeatmapPath = beatmapImport[0];
                }
                
                if (generalList.Triggered)
                {
                    game.ListReplays = true;
                    game.ListQuery = query.Triggered ? query[0] : null;
                }
                else if (listSkins.Triggered)
                {
                    game.SkinActionType = SkinAction.List;
                }
                else if (!generalView.Triggered) throw new CLIException
                {
                    Cause = "General Problem",
                    DisplayMessage = "--view must be present (except for --list and --list-skins)",
                    Suggestions = new[] {
                        "Add --list <Type> <ID/Path> to your command",
                        "Add --list-skins to your command"
                    }
                };
                else
                {
                    string path = generalView[1];
                    bool isValidInteger = long.TryParse(generalView[1], out long id);
                    bool isValidInt32 = id < int.MaxValue;
                    bool isValidGuid = Guid.TryParse(generalView[1], out var guid);

                    if (
                        (generalView[0].Equals("local") && !isValidGuid) ||
                        ((
                            generalView[0].Equals("auto") ||
                            generalView[0].Equals("online")
                        ) && !isValidInteger)
                    ) throw new CLIException
                    {
                        Cause = "Command-line Arguments (Parsing)",
                        DisplayMessage = $"Value {generalView[1]} is invaild"
                    };

                    switch (generalView[0])
                    {
                        case "local":
                        case "auto":
                            if (generalView[0].Equals("local")) game.ReplayOfflineScoreID = guid;
                            else
                            {
                                if (!isValidInt32) throw new CLIException
                                {
                                    Cause = "Command-line Arguments (Parsing)",
                                    DisplayMessage = $"{id} exceed int32 limit (larger than {int.MaxValue})",
                                    Suggestions = new[] { "Keep the number lower than the limit" }
                                };
                                game.ReplayAutoBeatmapID = (int)id;
                            }
                            break;

                        case "online": game.ReplayOnlineScoreID = id; break;

                        case "file":
                            if (!File.Exists(path)) throw new CLIException
                            {
                                Cause = "Files",
                                DisplayMessage = $"{path} doesn't exists"
                            };
                            if (File.GetAttributes(path).HasFlag(FileAttributes.Directory)) throw new CLIException
                            {
                                Cause = "Files",
                                DisplayMessage = $"{path} is detected as directory"
                            };
                            game.ReplayFileLocation = path;
                            break;

                        default:
                            throw new CLIException
                            {
                                Cause = "Command-line Arguments (Options)",
                                DisplayMessage = $"Unknown type: {generalView[0]}",
                                Suggestions = new[] { "Available types: local/online/file/auto" }
                            };
                    }
                    game.ReplayViewType = generalView[0];
                }

                // Misc
                if (overrideOverlayOptions.Triggered) game.HideOverlaysInPlayer = ParseBoolOrThrow(overrideOverlayOptions.ProcessedParameters[0]);
                else if (recordMode.Triggered) game.HideOverlaysInPlayer = true;
                else game.HideOverlaysInPlayer = false;

            } catch (CLIException cliException)
            {
                Console.WriteLine("Error while processing CLI arguments:");
                Console.WriteLine($"  Cause:      {cliException.Cause}");
                Console.WriteLine($"  Message:    {cliException.DisplayMessage}");
                if (cliException.Suggestions.Length == 0) return;
                else if (cliException.Suggestions.Length == 1) Console.WriteLine($"  Suggestion: {cliException.Suggestions[0]}");
                else
                {
                    Console.WriteLine("  Suggestions:");
                    for (int i = 0; i < cliException.Suggestions.Length; i++) Console.WriteLine("  - " + cliException.Suggestions[i]);
                }
                return;
            }

            host.Run(game);
            host.Dispose();
        }
        
        static GlRenderer ParseRenderer(string str)
        {
            switch (str.ToLower().Trim())
            {
                case "auto":
                    return GlRenderer.Auto;
                case "veldrid":
                    return GlRenderer.Veldrid;
                case "deferred":
                    return GlRenderer.Deferred;
                case "legacy":
                case "gl":
                    return GlRenderer.Legacy;
                default:
                    throw new CLIException
                    {
                        Cause = "Command-line Arguments (Parsing)",
                        DisplayMessage = $"Invalid integer: {str}"
                    };
            }
        }

        static int ParseIntOrThrow(string str)
        {
            if (!int.TryParse(str, out int val)) throw new CLIException
            {
                Cause = "Command-line Arguments (Parsing)",
                DisplayMessage = $"Invalid integer: {str}"
            };
            return val;
        }

        static bool ParseBoolOrThrow(string str)
        {
            str = str.ToLower();
            return str switch {
                "true" or "yes" or "1" => true,
                "false" or "no" or "0" => false,
                _ => throw new CLIException
                {
                    Cause = "Command-line Arguments (Parsing)",
                    DisplayMessage = $"Invaild boolean: {str}",
                    Suggestions = new[] { "Allowed values: true/yes/1 or false/no/0" }
                }
            };
        }

        private static bool CanApplyPatch()
        {
            // https://github.com/pardeike/Harmony/issues/607 -> https://github.com/MonoMod/MonoMod/issues/90
            return RuntimeInformation.ProcessArchitecture == Architecture.X86 ||
                   RuntimeInformation.ProcessArchitecture == Architecture.X64;
        }

        /// <summary>
        /// Determine when to apply Harmony patches. Could decrease start time.
        /// </summary>
        /// <param name="args"></param>
        /// <returns></returns>
        private static bool ShouldApplyPatch(string[] args)
        {
            return CanApplyPatch() && args.Any(arg => arg.Equals("--record") || arg.Equals("-R"));
        }
    }
}

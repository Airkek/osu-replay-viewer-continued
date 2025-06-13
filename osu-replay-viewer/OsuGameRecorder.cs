using AutoMapper.Internal;
using osu.Framework.Allocation;
using osu.Framework.Configuration;
using osu.Framework.Graphics.Containers;
using osu.Framework.Input;
using osu.Framework.Screens;
using osu.Framework.Timing;
using osu.Game;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.Graphics.Containers;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Game.Screens.Play;
using osu.Game.Screens.Ranking;
using osu.Game.Screens.Ranking.Statistics;
using osu_replay_renderer_netcore.Audio.Conversion;
using osu_replay_renderer_netcore.CustomHosts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using osu_replay_renderer_netcore.CustomHosts.CustomClocks;
using osu.Game.Configuration;
using osu.Game.Rulesets.Mania.Configuration;
using osu.Game.Rulesets.Osu.Configuration;
using osu.Game.Skinning;
using osu.Game.Tests.Rulesets;

namespace osu_replay_renderer_netcore
{
    partial class OsuGameRecorder : OsuGameBase
    {
        public List<string> ModsOverride = new();
        public List<string> ExperimentalFlags = new();

        RecorderScreenStack ScreenStack;
        RecorderReplayPlayer Player;

        public bool ListReplays = false;
        public string ListQuery = null;

        public string ReplayViewType;
        public long ReplayOnlineScoreID;
        public Guid ReplayOfflineScoreID;
        public int ReplayAutoBeatmapID;
        public string ReplayFileLocation;

        public SkinAction SkinActionType { get; set; } = SkinAction.Select;
        public string Skin { get; set; } = string.Empty;

        public string BeatmapPath { get; set; } = string.Empty;
        
        public bool HideOverlaysInPlayer = false;

        private DependencyContainer dependencies;
        private TestRulesetConfigCache configCache = new TestRulesetConfigCache();

        private GameSettings settings;

        public OsuGameRecorder(GameSettings settings)
        {
            this.settings = settings;
        }

        private string TempFolder = Path.GetTempPath();
        
        public Live<SkinInfo> ImportSkin(string skinPath)
        {
            if (!File.Exists(skinPath))
            {
                Console.Error.WriteLine($"Skin file not found: {skinPath}");
                Exit();
                return null;
            }

            if (!Directory.Exists(TempFolder))
            {
                Directory.CreateDirectory(TempFolder);
            }

            var tmpFile = Path.Combine(TempFolder, Path.GetFileName(skinPath));

            try
            {
                File.Copy(skinPath, tmpFile);
            }
            catch
            {
                Console.Error.WriteLine("Cannot copy skin file to temp folder");
                Exit();
                return null;
            }

            try
            {
                var skin = SkinManager.Import(new ImportTask(null!, tmpFile)).GetAwaiter().GetResult();
                skin.PerformRead(_ => { });
                return skin;
            }
            finally
            {
                File.Delete(tmpFile);
            }
        }

        public Live<BeatmapSetInfo> ImportBeatmapSet(string beatmapSetPath)
        {
            if (!File.Exists(beatmapSetPath))
            {
                Console.Error.WriteLine($"Beatmap file not found: {beatmapSetPath}");
                Exit();
                return null;
            }
            
            if (!Directory.Exists(TempFolder))
            {
                Directory.CreateDirectory(TempFolder);
            }

            var tmpFile = Path.Combine(TempFolder, Path.GetFileName(beatmapSetPath));

            try
            {
                File.Copy(beatmapSetPath, tmpFile);
            }
            catch
            {
                Console.Error.WriteLine("Cannot copy beatmapset file to temp folder");
                Exit();
                return null;
            }

            try
            {
                var beatmap = BeatmapManager.Import(new ImportTask(null!, tmpFile)).GetAwaiter().GetResult();
                beatmap.PerformRead(_ => { });
                return beatmap;
            }
            finally
            {
                File.Delete(tmpFile);
            }
        }
        
        public void SelectSkin(Live<SkinInfo> skin)
        {
            Console.WriteLine($"Selected skin: {skin.Value.Name}");
            SkinManager.CurrentSkinInfo.Value = skin;
        }

        public string GetCurrentBeatmapAudioPath()
        {
            return Storage.GetFullPath(@"files" + Path.DirectorySeparatorChar + Beatmap.Value.BeatmapSetInfo.GetPathForFile(Beatmap.Value.Metadata.AudioFile));
        }

        public WorkingBeatmap WorkingBeatmap { get => Beatmap.Value; }

        protected override void LoadComplete()
        {
            if (ListReplays)
            {
                Console.WriteLine();
                Console.WriteLine("--------------------");
                Console.WriteLine("Listing all downloaded scores:");
                if (ListQuery != null) Console.WriteLine($"(Query = '{ListQuery}')");
                Console.WriteLine();

                // Hacky way to get realm access
                RealmAccess realm = (RealmAccess) typeof(ScoreManager).GetProperty("Realm", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(ScoreManager);

                foreach (ScoreInfo info in realm.Run(r => r.All<ScoreInfo>().Detach()))
                {
                    if (!(
                        ListQuery == null ||
                        (
                            info.BeatmapInfo.GetDisplayTitle().Contains(ListQuery, StringComparison.OrdinalIgnoreCase) ||
                            info.User.Username.Contains(ListQuery, StringComparison.OrdinalIgnoreCase)
                        )
                    )) continue;

                    try
                    {
                        long scoreId = info.OnlineID;
                        if (scoreId <= 0) scoreId = -1;

                        string onlineScoreID = scoreId == -1 ? "" : $" (Online Score ID: #{scoreId})";
                        string mods = "(no mod)";
                        if (info.Mods.Length > 0)
                        {
                            mods = "";
                            foreach (var mod in info.Mods) mods += (mods.Length > 0 ? ", " : "") + mod.Name;
                        }

                        Console.WriteLine($"{info.BeatmapInfo.GetDisplayTitle()} | {info.BeatmapInfo.StarRating:F1}*");
                        Console.WriteLine($"View replay: --view local {info.ID}");
                        Console.WriteLine($"{info.Ruleset.Name} | Played by {info.User.Username}{onlineScoreID} | Ranked Score: {info.TotalScore:N0} ({info.DisplayAccuracy} {RankToActualRank(info.Rank)}) | Mods: {mods}");
                        Console.WriteLine();
                    }
                    catch (RulesetLoadException) { }
                }
                Console.WriteLine("--------------------");
                Console.WriteLine();
                Exit();
                return;
            }
            if (SkinActionType == SkinAction.List)
            {

                Console.WriteLine();
                Console.WriteLine("--------------------");
                Console.WriteLine("Listing all available skins:");

                RealmAccess realm = (RealmAccess) typeof(SkinManager).GetProperty("Realm", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(SkinManager);

                foreach (SkinInfo info in realm.Run(r => r.All<SkinInfo>().Detach()))
                {
                    Console.WriteLine($"- '{info.Name}'");
                }
                Console.WriteLine("--------------------");
                Console.WriteLine();
                Exit();
                return;
            }

            if (!string.IsNullOrWhiteSpace(BeatmapPath))
            {
                ImportBeatmapSet(BeatmapPath);
            }
            
            Score score;
            ScoreInfo scoreInfo = null;
            switch (ReplayViewType)
            {
                case "local":
                    scoreInfo = ScoreManager.Query(v => v.ID == ReplayOfflineScoreID);
                    if (scoreInfo == null)
                    {
                        Console.Error.WriteLine();
                        Console.Error.WriteLine("Unable to find local replay: " + ReplayOfflineScoreID);
                        Console.Error.WriteLine("- Make sure the replay ID exists when you use --list argument");
                        Console.Error.WriteLine("- You could have deleted that replay in your osu!lazer installation");
                        Console.Error.WriteLine();
                        Exit();
                        return;
                    }
                    score = ScoreManager.GetScore(scoreInfo);
                    break;
                case "online":
                    scoreInfo = ScoreManager.Query(v => v.OnlineID == ReplayOnlineScoreID);
                    if (scoreInfo == null)
                    {
                        Console.Error.WriteLine();
                        Console.Error.WriteLine("Unable to find local replay with online ID = " + ReplayOnlineScoreID);
                        Console.Error.WriteLine("- Make sure you have downloaded that replay");
                        Console.Error.WriteLine();
                        Exit();
                        return;
                    }
                    score = ScoreManager.GetScore(scoreInfo);
                    break;
                case "auto":
                    var beatmapInfo = BeatmapManager.QueryBeatmap(v => v.OnlineID == ReplayAutoBeatmapID);
                    if (beatmapInfo == null)
                    {
                        Console.Error.WriteLine("Beatmap not found: " + ReplayAutoBeatmapID);
                        Console.Error.WriteLine("Please make sure the beatmap is imported in your osu!lazer installation");
                        Exit();
                        return;
                    }

                    var ruleset = beatmapInfo.Ruleset.CreateInstance();
                    var working = BeatmapManager.GetWorkingBeatmap(beatmapInfo);
                    var beatmap = working.GetPlayableBeatmap(ruleset.RulesetInfo, new[] { ruleset.GetAutoplayMod() });
                    score = ruleset.GetAutoplayMod().CreateScoreFromReplayData(beatmap, new[] { ruleset.GetAutoplayMod() });
                    score.ScoreInfo.BeatmapInfo = beatmapInfo;
                    score.ScoreInfo.Mods = new[] { ruleset.GetAutoplayMod() };
                    score.ScoreInfo.Ruleset = ruleset.RulesetInfo;
                    break;
                case "file":
                    // ReplayFileLocation is already checked at CLI stage
                    using (FileStream stream = new(ReplayFileLocation, FileMode.Open))
                    {
                        var decoder = new DatabasedLegacyScoreDecoder(RulesetStore, BeatmapManager);

                        try
                        {
                            score = decoder.Parse(stream);
                        }
                        catch (LegacyScoreDecoder.BeatmapNotFoundException e)
                        {
                            Console.Error.WriteLine("Beatmap not found while opening replay: " + e.Message);
                            Console.Error.WriteLine(
                                "Please make sure the beatmap is imported in your osu!lazer installation");
                            score = null;
                            Exit();
                        }
                    }

                    break;
                default: throw new Exception($"Unknown type {ReplayViewType}");
            }

            if (score == null)
            {
                Console.Error.WriteLine("Unable to open: Score not found in osu!lazer installation");
                Console.Error.WriteLine("Please make sure the score is imported in your osu!lazer installation");
                Exit();
            }

            if (ModsOverride.Count > 0)
            {
                List<Mod> mods = new();
                foreach (var mod in score.ScoreInfo.Ruleset.CreateInstance().AllMods)
                {
                    if (mod is Mod mm && ModsOverride.Any(v => v.StartsWith("acronyms:") ? v[9..] == mod.Acronym : v == mod.Name)) mods.Add(mm);
                }
                score.ScoreInfo.Mods = mods.ToArray();
            }

            LoadViewer(score);
        }

        [Resolved]
        private FrameworkConfigManager config { get; set; }

        private void LoadViewer(Score score)
        {
            var sw = new Stopwatch();            
            // Apply some stuffs
            config.SetValue(FrameworkSetting.ConfineMouseMode, ConfineMouseMode.Never);
            if (Host is not ReplayRecordGameHost)
            {
                config.SetValue(FrameworkSetting.FrameSync, FrameSync.VSync);
            }
            else
            {
                config.SetValue(FrameworkSetting.FrameSync, FrameSync.Unlimited);
            }
            
            config.SetValue(FrameworkSetting.VolumeMusic, settings.VolumeMusic);
            config.SetValue(FrameworkSetting.VolumeEffect, settings.VolumeEffects);
            config.SetValue(FrameworkSetting.VolumeUniversal, settings.VolumeMaster);
            
            LocalConfig.SetValue(OsuSetting.HitLighting, false);
                
            Audio.Balance.Value = 0;
            
            ScreenStack = new RecorderScreenStack();
            LoadComponent(ScreenStack);
            Add(ScreenStack);
            
            var rulesetInfo = score.ScoreInfo.Ruleset;
            Ruleset.Value = rulesetInfo;

            var beatmap = BeatmapManager.QueryBeatmap(beatmap => beatmap.ID == score.ScoreInfo.BeatmapInfo.ID);
            var working = BeatmapManager.GetWorkingBeatmap(beatmap);
            working.LoadTrack();
            Beatmap.Value = working;
            SelectedMods.Value = score.ScoreInfo.Mods;

            if (Host is ReplayRecordGameHost recordHost && recordHost.NeedAudio)
            {
                var speed = score.ScoreInfo.Mods.OfType<IApplicableToRate>().Aggregate<IApplicableToRate, double>(1, (current, rateMod) => rateMod.ApplyToRate(0, current));
                var pitch = 1d;
                foreach (var mod in score.ScoreInfo.Mods)
                {
                    if (mod is ModDaycore or ModNightcore or ModDoubleTime { AdjustPitch.Value: true } or ModHalfTime { AdjustPitch.Value: true })
                    {
                        pitch = speed;
                    } 
                }
                var volumeMusic = config.Get<double>(FrameworkSetting.VolumeMusic);
                var volumeUniversal = config.Get<double>(FrameworkSetting.VolumeUniversal);
                
                Console.WriteLine("Decoding audio...");
                sw.Restart();
                var track = FFmpegAudioTools.Decode(GetCurrentBeatmapAudioPath(), tempoFactor: speed, pitchFactor: pitch, volume: volumeMusic * volumeUniversal);
                sw.Stop();
                Console.WriteLine($"Audio decoded in {sw.ElapsedMilliseconds}ms");
                recordHost.SetAudioTrack(track);
            }

            Player = new RecorderReplayPlayer(score, HideOverlaysInPlayer);

            Player.OnFailed += () =>
            {
                (Host as ReplayRecordGameHost)?.AudioEnded();
            };

            if (!string.IsNullOrEmpty(Skin))
            {
                Live<SkinInfo> skin;
                if (SkinActionType == SkinAction.Import)
                {
                    skin = ImportSkin(Skin);
                }
                else
                {
                    skin = SkinManager.Query(c => c.Name == Skin);
                }

                if (skin is null)
                {
                    Console.Error.WriteLine("Skin not found.");
                    Exit();
                    return;
                }

                SelectSkin(skin);
                LocalConfig.GetBindable<bool>(OsuSetting.BeatmapColours).Value = settings.BeatmapColors;
                LocalConfig.GetBindable<bool>(OsuSetting.BeatmapSkins).Value = settings.BeatmapSkin;
                LocalConfig.GetBindable<bool>(OsuSetting.BeatmapHitsounds).Value = settings.BeatmapHitsounds;
                LocalConfig.GetBindable<bool>(OsuSetting.ShowStoryboard).Value = settings.ShowStoryboard;
                LocalConfig.GetBindable<double>(OsuSetting.DimLevel).Value = settings.BackgroundDim;
            }
            
            Console.WriteLine("Loading player");
            sw.Restart();
            var loader = new RecorderReplayPlayerLoader(Player);
            loader.OnLoadComplete += _ =>
            {
                sw.Stop();
                Console.WriteLine($"Player loaded in {sw.ElapsedMilliseconds}ms");
                if (Host is ReplayRecordGameHost record)
                {
                    record.StartRecording();
                }
            };
            ScreenStack.Push(loader);
            ScreenStack.ScreenPushed += ScreenStack_ScreenPushed;
            
            //MenuCursorContainer.Cursor.RemoveAll(v => true, true);

            var configMgr = configCache.GetConfigFor(Ruleset.Value.CreateInstance());
            if (configMgr is OsuRulesetConfigManager osuMgr)
            {
                osuMgr.SetValue(OsuRulesetSetting.ShowCursorRipples, false);
                osuMgr.SetValue(OsuRulesetSetting.SnakingInSliders, false);
                osuMgr.SetValue(OsuRulesetSetting.SnakingOutSliders, false);
                osuMgr.SetValue(OsuRulesetSetting.ReplayFrameMarkersEnabled, false);
                osuMgr.SetValue(OsuRulesetSetting.ReplayClickMarkersEnabled, false);
            } else if (configMgr is ManiaRulesetConfigManager maniaMgr)
            {
                maniaMgr.SetValue(ManiaRulesetSetting.ScrollSpeed, 26d);
            }
        }

        [BackgroundDependencyLoader]
        private void load(ReadableKeyCombinationProvider keyCombinationProvider, FrameworkConfigManager frameworkConfig)
        {
            dependencies.CacheAs<IRulesetConfigCache>(configCache);
        }

        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent) =>
            dependencies = new DependencyContainer(base.CreateChildDependencies(parent));

        private void ScreenStack_ScreenPushed(IScreen lastScreen, IScreen newScreen)
        {
            Console.WriteLine("screen push: " + newScreen.GetType());
            ScreenStack.Parallax = 0.0f;

            if (newScreen is SoloResultsScreen soloResult)
            {
                soloResult.OnLoadComplete += (d) =>
                {
                    //MethodInfo internalChildMethod = typeof(CompositeDrawable).GetDeclaredMethod("get_InternalChild");
                    //GridContainer grid = internalChildMethod.Invoke(soloResult, null) as GridContainer;
                    
                    /*PropertyInfo internalChildProperty = typeof(CompositeDrawable).GetProperty("InternalChild", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            MethodInfo getter = internalChildProperty.GetGetMethod(nonPublic: true);
            return getter.Invoke(drawable, null) as Drawable;*/

                    MethodInfo scrollContentMethod = typeof(ResultsScreen).GetInstanceMethod("get_VerticalScrollContent");
                    var scrollContent = scrollContentMethod.Invoke(soloResult, null) as OsuScrollContainer;

                    var statisticsPanel = (scrollContent.Child as Container).Children[1] as StatisticsPanel;
                    var container2 = DrawablesUtils.GetInternalChild(statisticsPanel) as Container;
                    container2.Remove(container2.Children[1], true); // kill the loading spinner
                    
                    Scheduler.AddDelayed(() =>
                    {
                        statisticsPanel.ToggleVisibility();
                    }, 2500);
                    
                    if (Host is ReplayRecordGameHost)
                    {
                        Scheduler.AddDelayed(() =>
                        {
                            (Host as ReplayRecordGameHost)?.FinishRecording();
                            Exit();
                        }, 11000);
                    }
                };
            }
            if (newScreen is RecorderReplayPlayer player && Host is ReplayRecordGameHost)
            {
                player.ManipulateClock = true;

                MethodInfo getGameplayClockContainer = typeof(Player).GetInstanceMethod("get_GameplayClockContainer");
                var clockContainer = getGameplayClockContainer.Invoke(player, null) as GameplayClockContainer;
                FieldInfo gameplayClockField = typeof(GameplayClockContainer)
                    .GetField("GameplayClock", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var clock = gameplayClockField.GetValue(clockContainer) as FramedBeatmapClock;
                var wrapped = new WrappedClock(Clock, clock.Source as IAdjustableClock);

                FieldInfo decoupledTrackField = typeof(FramedBeatmapClock).GetField("decoupledTrack",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                var dc = decoupledTrackField.GetValue(clock) as DecouplingFramedClock;
                dc.AllowDecoupling = false;
                
                clock.ChangeSource(wrapped);
            }
        }

        private static string RankToActualRank(ScoreRank rank)
        {
            return rank switch
            {
                ScoreRank.D or ScoreRank.C or ScoreRank.B or ScoreRank.A or ScoreRank.S => rank.ToString(),
                ScoreRank.SH => "S+",
                ScoreRank.X => "SS",
                ScoreRank.XH => "SS+",
                _ => "n/a",
            };
        }
    }

    internal enum SkinAction
    {
        Import,
        Select,
        List
    }
}

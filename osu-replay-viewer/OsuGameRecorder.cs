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
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Leaderboards;
using osu_replay_renderer_netcore.CLI;
using osu_replay_renderer_netcore.Online;
using Newtonsoft.Json;
using System.Net.Http;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osu.Game.Screens.Play;
using osu.Game.Screens.Ranking;
using osu.Game.Screens.Ranking.Statistics;
using osu_replay_renderer_netcore.Audio;
using osu_replay_renderer_netcore.Audio.Conversion;
using osu_replay_renderer_netcore.CustomHosts;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using HarmonyLib;
using osu_replay_renderer_netcore.CustomHosts.CustomClocks;
using osu.Framework.Platform;
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

        public bool PreserveImportedFiles { get; set; } = true;
        public bool ExtendedImportInfo { get; set; } = false;
        
        public bool HideOverlaysInPlayer = false;
        public bool SkipIntro = false;

        private DependencyContainer dependencies;
        private TestRulesetConfigCache configCache = new TestRulesetConfigCache();

        private GameSettings settings;
        private readonly Config.OsuApiOptionsObject apiOptions;

        public OsuGameRecorder(GameSettings settings, Config.OsuApiOptionsObject apiOptions = null)
        {
            this.settings = settings;
            this.apiOptions = apiOptions;

            // Avoid automatic login with the shared osu!lazer credentials
            API = new DummyAPIAccess();
            API.Logout();
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

            var toImport = skinPath;
            if (PreserveImportedFiles)
            {
                if (!Directory.Exists(TempFolder))
                {
                    Directory.CreateDirectory(TempFolder);
                }

                var tmpFile = Path.Combine(TempFolder, Path.GetFileName(skinPath));
                if (File.Exists(tmpFile))
                {
                    File.Delete(tmpFile);
                }

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

                toImport = tmpFile;
            }

            try
            {
                var skin = SkinManager.Import(new ImportTask(null!, toImport)).GetAwaiter().GetResult();
                skin.PerformRead(_ => { });

                if (ExtendedImportInfo)
                {
                    Console.WriteLine($"IMPORTED_SKIN_ID::{skin.Value.ID}");
                    Console.WriteLine($"IMPORTED_SKIN_NAME::{skin.Value.Name}");
                }
                
                return skin;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine("Cannot import skin");
                Console.Error.WriteLine(e);
            }
            finally
            {
                if (File.Exists(toImport))
                {
                    File.Delete(toImport);
                }
            }
            
            Exit();
            return null;
        }

        public Live<BeatmapSetInfo> ImportBeatmapSet(string beatmapSetPath)
        {
            if (!File.Exists(beatmapSetPath))
            {
                Console.Error.WriteLine($"Beatmap file not found: {beatmapSetPath}");
                Exit();
                return null;
            }

            var toImport = beatmapSetPath;

            if (PreserveImportedFiles)
            {
                if (!Directory.Exists(TempFolder))
                {
                    Directory.CreateDirectory(TempFolder);
                }

                var tmpFile = Path.Combine(TempFolder, Path.GetFileName(beatmapSetPath));
                if (File.Exists(tmpFile))
                {
                    File.Delete(tmpFile);
                }

                try
                {
                    File.Copy(beatmapSetPath, tmpFile);
                }
                catch (Exception e)
                {
                    Console.Error.WriteLine("Cannot copy beatmapset file to temp folder");
                    Exit();
                    return null;
                }
                
                toImport = tmpFile;
            }

            try
            {
                var beatmap = BeatmapManager.Import(new ImportTask(null!, toImport)).GetAwaiter().GetResult();
                beatmap.PerformRead(_ => { });

                if (ExtendedImportInfo)
                {
                    Console.WriteLine($"IMPORTED_BEATMAPSET_ID::{beatmap.Value.ID}");
                    Console.WriteLine($"IMPORTED_BEATMAPSET_ONLINE_ID::{beatmap.Value.OnlineID}");
                    Console.WriteLine($"IMPORTED_BEATMAPSET_BEATMAP_IDS::{string.Join(',', beatmap.Value.Beatmaps.Select(x => x.ID))}");
                    Console.WriteLine($"IMPORTED_BEATMAPSET_BEATMAP_HASHES::{string.Join(',', beatmap.Value.Beatmaps.Select(x => x.Hash))}");
                    Console.WriteLine($"IMPORTED_BEATMAPSET_BEATMAP_MD5HASHES::{string.Join(',', beatmap.Value.Beatmaps.Select(x => x.MD5Hash))}");
                    Console.WriteLine($"IMPORTED_BEATMAPSET_BEATMAP_ONLINE_IDS::{string.Join(',', beatmap.Value.Beatmaps.Select(x => x.OnlineID))}");
                }
                return beatmap;
            }
            catch (Exception e)
            {
                Console.Error.WriteLine(("Cannot import beatmapset"));
                Console.Error.WriteLine(e);
            }
            finally
            {
                if (File.Exists(toImport))
                {
                    File.Delete(toImport);
                }
            }
            
            Exit();
            return null;
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

                var scores = realm.Run(r =>
                {
                    var entries = new List<(string Beatmap, string ReplayCommand, string Details)>();
                    foreach (ScoreInfo info in r.All<ScoreInfo>())
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
                            var scoreMods = info.Mods;
                            if (scoreMods.Length > 0)
                            {
                                mods = "";
                                foreach (var mod in scoreMods) mods += (mods.Length > 0 ? ", " : "") + mod.Name;
                            }

                            entries.Add((
                                $"{info.BeatmapInfo.GetDisplayTitle()} | {info.BeatmapInfo.StarRating:F1}*",
                                $"View replay: --view local {info.ID}",
                                $"{info.Ruleset.Name} | Played by {info.User.Username}{onlineScoreID} | Ranked Score: {info.TotalScore:N0} ({info.DisplayAccuracy} {RankToActualRank(info.Rank)}) | Mods: {mods}"));
                        }
                        catch (RulesetLoadException) { }
                    }
                    return entries;
                });

                foreach (var scoreEntry in scores)
                {
                    Console.WriteLine(scoreEntry.Beatmap);
                    Console.WriteLine(scoreEntry.ReplayCommand);
                    Console.WriteLine(scoreEntry.Details);
                    Console.WriteLine();
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

                var skins = realm.Run(r => r.All<SkinInfo>().AsEnumerable().Select(info => (info.Name, info.ID)).ToArray());
                foreach (var skin in skins)
                {
                    var extendedInfo = ExtendedImportInfo ? $", ID: '{skin.ID}'" : "";
                    Console.WriteLine($"- '{skin.Name}'{extendedInfo}");
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
                            Console.Error.WriteLine("Beatmap not found");
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
            if (ReplayViewType != "auto")
                LoadReplayUser(score.ScoreInfo);

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
            
            if (!string.IsNullOrEmpty(Skin))
            {
                Live<SkinInfo> skin;
                switch (SkinActionType)
                {
                    case SkinAction.Import:
                        skin = ImportSkin(Skin);
                        break;
                    case SkinAction.Select:
                        skin = SkinManager.Query(c => c.Name == Skin);
                        break;
                    case SkinAction.Match:
                        skin = SkinManager.Query(c => c.Name.Contains(Skin));
                        break;
                    case SkinAction.Id:
                        skin = SkinManager.Query(c => c.ID == new Guid(Skin));
                        break;
                    case SkinAction.List:
                        Debug.Assert(false);
                        Exit();
                        return;
                    default:
                        skin = null;
                        break;
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

            if (Host is ReplayRecordGameHost recordHost && recordHost.NeedAudio)
            {
                var volumeMusic = config.Get<double>(FrameworkSetting.VolumeMusic);
                var volumeUniversal = config.Get<double>(FrameworkSetting.VolumeUniversal);
                
                Console.WriteLine("Reading audio format...");
                sw.Restart();
                var track = new FFmpegMusicSource(GetCurrentBeatmapAudioPath(), FFmpegAudioTools.FFmpegExec,
                    volumeMusic * volumeUniversal);
                sw.Stop();
                Console.WriteLine($"Audio format loaded in {sw.ElapsedMilliseconds}ms");
                recordHost.SetMusicSource(track);
            }

            LocalConfig.SetValue(OsuSetting.GameplayLeaderboard, settings.ShowLeaderboard);
            var leaderboard = settings.ShowLeaderboard ? LoadLeaderboard(score, beatmap) : null;
            Player = new RecorderReplayPlayer(score, HideOverlaysInPlayer, SkipIntro, leaderboard);
            
            Player.OnFailed += () =>
            {
                (Host as ReplayRecordGameHost)?.AudioEnded();
            };
            
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
                maniaMgr.SetValue(ManiaRulesetSetting.ScrollSpeed, Math.Clamp(settings.ScrollSpeed, 1.0, 40.0));
            }
        }

        private void LoadReplayUser(ScoreInfo score)
        {
            try
            {
                ReplayUserResolver.ResolveAsync(score, () => OsuApiCommands.CreateClient(apiOptions)).GetAwaiter().GetResult();
            }
            catch (CLIException e)
            {
                Console.Error.WriteLine($"[Player] {e.DisplayMessage}; continuing with the replay's player data");
            }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or ArgumentException or JsonException or OperationCanceledException)
            {
                Console.Error.WriteLine($"[Player] {e.Message}; continuing with the replay's player data");
            }
        }

        private LeaderboardScores LoadLeaderboard(Score score, BeatmapInfo beatmap)
        {
            if (beatmap.OnlineID <= 0)
            {
                Console.Error.WriteLine("[Leaderboard] The beatmap has no online ID; continuing without a leaderboard");
                return null;
            }

            try
            {
                const int limit = 50;
                Console.WriteLine("Loading online leaderboard...");
                using var client = OsuApiCommands.CreateClient(apiOptions);
                using var response = client.GetLeaderboardAsync(beatmap.OnlineID, score.ScoreInfo.Ruleset.ShortName, limit).GetAwaiter().GetResult();
                var collection = JsonConvert.DeserializeObject<APIScoresCollection>(response.RootElement.GetRawText());
                if (collection?.Scores == null || collection.Scores.Any(s => s == null))
                    throw new InvalidOperationException("osu! API returned an invalid leaderboard");

                var scores = collection.Scores.Select(s => s.ToScoreInfo(RulesetStore, beatmap)).OrderByTotalScore().ToArray();
                for (int i = 0; i < scores.Length; i++)
                    scores[i].Position = i + 1;

                int totalScores = collection.ScoresCount;
                if (totalScores == 0)
                    totalScores = scores.Length < limit ? scores.Length : int.MaxValue;
                return LeaderboardScores.Success(scores, limit, totalScores, null);
            }
            catch (CLIException e)
            {
                Console.Error.WriteLine($"[Leaderboard] {e.DisplayMessage}; continuing without a leaderboard");
            }
            catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or ArgumentException or JsonException or OperationCanceledException)
            {
                Console.Error.WriteLine($"[Leaderboard] {e.Message}; continuing without a leaderboard");
            }
            return null;
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
            if (newScreen is RecorderReplayPlayer player && Host is ReplayRecordGameHost recordHost)
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
                recordHost.SetGameplayClock(wrapped, Beatmap.Value.Track, () => clock.TotalAppliedOffset);
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
        List,
        Match,
        Id
    }
}

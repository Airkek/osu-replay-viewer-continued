using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Testing;
using osu.Framework.Screens;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Difficulty;
using osu.Game.Rulesets.Judgements;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Objects;
using osu.Game.Rulesets.Objects.Drawables;
using osu.Game.Scoring;
using osu.Game.Screens.Play;
using osu.Game.Screens.Play.HUD;
using osu_replay_renderer_netcore.HUD.Builtin;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using osu_replay_renderer_netcore.CustomHosts.CustomClocks;
using osu.Game.Rulesets.Osu.UI;
using osu.Game.Rulesets.Osu.UI.Cursor;
using osu.Game.Skinning;

namespace osu_replay_renderer_netcore
{
    partial class RecorderReplayPlayer : ReplayPlayer
    {
        public Score GivenScore { get; private set; }
        public bool ManipulateClock { get; set; } = false;
        public bool HideOverlays { get; private set; } = false;

        public RecorderReplayPlayer(Score score, bool hideOverlays) : base(score, new PlayerConfiguration
        {
            AllowRestart = false,
            AllowPause = false,
            AllowFailAnimation = true,
            AllowUserInteraction = !hideOverlays,
            ShowLeaderboard = false,
            AllowSkipping = !hideOverlays,
            AutomaticallySkipIntro = false
        })
        {
            GivenScore = score;
            HideOverlays = hideOverlays;
        }

        public Action OnFailed;

        protected override void OnFail()
        {
            OnFailed?.Invoke();
            this.Push(CreateResults(GivenScore.ScoreInfo));
        }
        
        protected override bool CheckModsAllowFailure()
        {
            return GameplayState.Mods.OfType<IApplicableFailOverride>().All((Func<IApplicableFailOverride, bool>) (m => m.PerformFail()));
        }

        protected override void LoadComplete()
        {
            base.LoadComplete();
            HUDOverlay.ShowHud.Value = false;
            HUDOverlay.HoldToQuit.Hide();

            if (HideOverlays)
            {
                HUDOverlay.RemoveRecursive(v => v == HUDOverlay.PlayerSettingsOverlay);
                GameplayClockContainer.RemoveRecursive(v => v is SkipOverlay);
            }

            var game = Game as OsuGameRecorder;

            if (
                game.ExperimentalFlags.Contains("performance-graph") ||
                game.ExperimentalFlags.Contains("performance-points-graph") ||
                game.ExperimentalFlags.Contains("pp-graph")
            ) SetupPerformanceGraph();
        }

        private void SetupPerformanceGraph()
        {
            PerformanceGraph performanceGraph;
            AddInternal(performanceGraph = new()
            {
                Anchor = Anchor.TopLeft,
                Origin = Anchor.TopLeft,
                Width = 300,
                Margin = new MarginPadding { Left = 10f, Top = 50f }
            });

            BeatmapDifficultyCache diffCache = null;
            Bindable<int> ppCounter = null;
            List<TimedDifficultyAttributes> timedAttrs = null;

            Action<DrawableHitObject, JudgementResult> ppChange = (dho, judgement) =>
            {
                if (diffCache == null)
                {
                    diffCache = Game.ChildrenOfType<BeatmapDifficultyCache>().First();
                    var task = diffCache.GetTimedDifficultyAttributesAsync(
                        (Game as OsuGameRecorder).WorkingBeatmap,
                        GameplayState.Ruleset,
                        Mods.Value.ToArray()
                    );
                    task.Wait();
                    timedAttrs = task.Result;
                }
                if (ppCounter == null) ppCounter = HUDOverlay.ChildrenOfType<PerformancePointsCounter>().First().Current;

                // Get attribute at judgement time
                int attribIndex = timedAttrs.BinarySearch(new TimedDifficultyAttributes(dho.HitObject.GetEndTime(), null));
                if (attribIndex < 0) attribIndex = ~attribIndex - 1;
                var attrib = timedAttrs[Math.Clamp(attribIndex, 0, timedAttrs.Count - 1)].Attributes;

                // Calculate
                PerformanceCalculator calc = GameplayState.Ruleset.CreatePerformanceCalculator();
                performanceGraph.PP.Value = calc.Calculate(GameplayState.Score.ScoreInfo, attrib).Total;

                // TODO: Expose PP to OsuGameRecorder
            };

            DrawableRuleset.Playfield.NewResult += ppChange;
            //DrawableRuleset.Playfield.RevertResult += ppChange;
        }

        protected override void StartGameplay()
        {
            if (ManipulateClock)
            {
                GameplayClockContainer.Reset();
                GameplayClockContainer.Start();
                FieldInfo gameplayClockField = typeof(GameplayClockContainer)
                    .GetField("GameplayClock", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                
                var ogClock = gameplayClockField.GetValue(GameplayClockContainer) as FramedBeatmapClock;
                var clock = ogClock.Source as WrappedClock;
                foreach (Mod mod in GivenScore.ScoreInfo.Mods)
                {
                    if (mod is IApplicableToRate rateMod) clock.RateMod = rateMod;
                }
            } else base.StartGameplay();
        }
    }
}

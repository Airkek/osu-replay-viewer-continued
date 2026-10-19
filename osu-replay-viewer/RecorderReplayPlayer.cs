using osu.Framework.Screens;
using osu.Game.Beatmaps;
using osu.Game.Rulesets.Mods;
using osu.Game.Scoring;
using osu.Game.Screens.Play;
using System;
using System.Linq;
using System.Reflection;
using osu_replay_renderer_netcore.CustomHosts.CustomClocks;

namespace osu_replay_renderer_netcore
{
    partial class RecorderReplayPlayer : ReplayPlayer
    {
        public Score GivenScore { get; private set; }
        public bool ManipulateClock { get; set; } = false;
        public bool HideOverlays { get; private set; } = false;

        public RecorderReplayPlayer(Score score, bool hideOverlays, bool skipIntro) : base(score, new PlayerConfiguration
        {
            AllowRestart = false,
            AllowPause = false,
            AllowUserInteraction = !hideOverlays,
            ShowLeaderboard = false,
            AllowSkipping = !hideOverlays,
            AutomaticallySkipIntro = skipIntro
        })
        {
            GivenScore = score;
            HideOverlays = hideOverlays;
        }

        public Action OnFailed;

        protected override void PerformFail()
        {
            base.PerformFail();
            OnFailed?.Invoke();
            this.Push(CreateResults(GivenScore.ScoreInfo));
        }

        public override void OnSuspending(ScreenTransitionEvent e)
        {
            // Suppress debug assertion
            ValidForResume = false;
            base.OnSuspending(e);
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
                ReplayOverlay.Hide();
                GameplayClockContainer.RemoveRecursive(v => v is SkipOverlay);
            }
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

                if (Configuration.AutomaticallySkipIntro)
                {
                    SchedulerAfterChildren.Add(() =>
                    {
                        (GameplayClockContainer as MasterGameplayClockContainer)?.Skip();
                    });
                }
            } else base.StartGameplay();
        }
    }
}

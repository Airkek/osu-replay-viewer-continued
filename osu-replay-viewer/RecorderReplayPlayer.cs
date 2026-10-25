using osu.Framework.Screens;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Game.Rulesets.Mods;
using osu.Game.Online.Leaderboards;
using osu.Game.Scoring;
using osu.Game.Screens.Play;
using System;
using System.Linq;

namespace osu_replay_renderer_netcore
{
    partial class RecorderReplayPlayer : ReplayPlayer
    {
        public Score GivenScore { get; private set; }
        public bool ManipulateClock { get; set; } = false;
        public bool HideOverlays { get; private set; } = false;

        private readonly LeaderboardScores leaderboard;
        private readonly Bindable<LeaderboardScores> leaderboardSnapshot;
        private LeaderboardManager replayLeaderboardManager;

        public RecorderReplayPlayer(Score score, bool hideOverlays, bool skipIntro, LeaderboardScores leaderboard = null) : base(score, new PlayerConfiguration
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
            this.leaderboard = leaderboard;
            Configuration.ShowLeaderboard = leaderboard != null;
            if (leaderboard != null)
                leaderboardSnapshot = new Bindable<LeaderboardScores>(leaderboard);
        }

        protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
        {
            var dependencies = new DependencyContainer(base.CreateChildDependencies(parent));
            if (leaderboard != null)
            {
                replayLeaderboardManager = new LeaderboardManager();
                replayLeaderboardManager.Scores.BindTo(leaderboardSnapshot);
                dependencies.CacheAs(replayLeaderboardManager);
            }
            return dependencies;
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
            if (!LoadedBeatmapSuccessfully)
                return;

            if (!HUDOverlay.ShowHud.Disabled)
                HUDOverlay.ShowHud.Value = leaderboard != null;
            HUDOverlay.HoldToQuit.Hide();

            if (HideOverlays)
            {
                ReplayOverlay.Hide();
                GameplayClockContainer.RemoveRecursive(v => v is SkipOverlay);
            }
        }

        protected override void Dispose(bool isDisposing)
        {
            base.Dispose(isDisposing);
            replayLeaderboardManager?.Dispose();
            leaderboardSnapshot?.UnbindAll();
        }

        protected override void StartGameplay()
        {
            if (ManipulateClock)
            {
                GameplayClockContainer.Reset();
                GameplayClockContainer.Start();
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

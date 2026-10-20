using System.Linq;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Graphics;
using osu.Framework.Graphics.Containers;
using osu.Game.Online.Leaderboards;
using osu.Game.Scoring;
using osu.Game.Screens.Play;
using osu.Game.Screens.Play.HUD;
using osu.Game.Screens.Play.Leaderboards;

namespace osu_replay_renderer_netcore.Online;

internal partial class ReplayLeaderboard : CompositeDrawable
{
    private readonly ReplayLeaderboardProvider provider;

    public ReplayLeaderboard(LeaderboardScores leaderboard, GameplayState gameplayState)
    {
        AutoSizeAxes = Axes.Both;
        Anchor = Anchor.CentreLeft;
        Origin = Anchor.CentreLeft;
        X = 20;
        Depth = -1;
        InternalChildren = new Drawable[]
        {
            provider = new ReplayLeaderboardProvider(leaderboard, gameplayState),
            new DrawableGameplayLeaderboard
            {
                AlwaysShown = true,
                CollapseDuringGameplay = { Value = false }
            }
        };
    }

    protected override IReadOnlyDependencyContainer CreateChildDependencies(IReadOnlyDependencyContainer parent)
    {
        var dependencies = new DependencyContainer(base.CreateChildDependencies(parent));
        dependencies.CacheAs<IGameplayLeaderboardProvider>(provider);
        return dependencies;
    }
}

internal partial class ReplayLeaderboardProvider : Component, IGameplayLeaderboardProvider
{
    public IBindableList<GameplayLeaderboardScore> Scores => scores;
    private readonly BindableList<GameplayLeaderboardScore> scores = new();
    private readonly GameplayLeaderboardScore trackedScore;
    private readonly bool isPartial;

    public ReplayLeaderboardProvider(LeaderboardScores leaderboard, GameplayState gameplayState)
    {
        isPartial = leaderboard.IsPartial;
        scores.AddRange(leaderboard.AllScores.OrderByTotalScore().Select(score =>
            new GameplayLeaderboardScore(score, tracked: false, GameplayLeaderboardScore.ComboDisplayMode.Highest)));
        trackedScore = new GameplayLeaderboardScore(gameplayState, tracked: true, GameplayLeaderboardScore.ComboDisplayMode.Highest)
        {
            TotalScoreTiebreaker = long.MaxValue
        };
        scores.Add(trackedScore);
        trackedScore.TotalScore.BindValueChanged(_ => sort());
        sort();
    }

    private void sort()
    {
        var ordered = scores.OrderByDescending(score => score.TotalScore.Value)
                            .ThenBy(score => score.TotalScoreTiebreaker)
                            .ToList();
        int delta = 0;
        for (int i = 0; i < ordered.Count; i++)
        {
            var score = ordered[i];
            score.DisplayOrder.Value = i + 1;
            if (!isPartial)
                score.Position.Value = i + 1;
            else if (score.Tracked)
            {
                int? previous = i > 0 ? ordered[i - 1].InitialPosition : 0;
                int? next = i < ordered.Count - 1 ? ordered[i + 1].InitialPosition : null;
                if (previous != null && next != null && previous + 1 == next)
                {
                    score.Position.Value = previous + 1;
                    delta++;
                }
                else
                    score.Position.Value = null;
            }
            else
                score.Position.Value = score.InitialPosition + delta;
        }
    }

    protected override void Dispose(bool isDisposing)
    {
        trackedScore.TotalScore.UnbindAll();
        trackedScore.Accuracy.UnbindAll();
        trackedScore.Combo.UnbindAll();
        base.Dispose(isDisposing);
    }
}

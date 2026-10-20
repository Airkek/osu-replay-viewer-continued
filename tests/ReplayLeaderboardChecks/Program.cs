using System.Reflection;
using osu.Framework.Allocation;
using osu.Game.Beatmaps;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Leaderboards;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Scoring;
using osu.Game.Screens.Play;
using osu.Game.Screens.Play.Leaderboards;
using osu_replay_renderer_netcore;

Assembly viewer = typeof(GameSettings).Assembly;
Type providerType = viewer.GetType("osu_replay_renderer_netcore.Online.ReplayLeaderboardProvider", throwOnError: true)!;
int checks = 0;

Check("complete leaderboard tracks replay progress and puts equal live score after submitted scores", () =>
{
    using var fixture = new Fixture(providerType, [Top("alice", 900, 1), Top("bob", 700, 2)], totalScores: 2);
    Assert(fixture.Tracked.Position.Value == 3, "initial complete rank");
    fixture.State.ScoreProcessor.TotalScore.Value = 700;
    Assert(fixture.Tracked.Position.Value == 3, "live tie precedes existing submitted score");
    fixture.State.ScoreProcessor.TotalScore.Value = 701;
    Assert(fixture.Tracked.Position.Value == 2 && fixture.Row("bob").Position.Value == 3, "live overtaking did not update rank");
    Assert(fixture.OrderedUsers().SequenceEqual(new[] { "alice", "replay-user", "bob" }), "display order is stale");
    fixture.State.ScoreProcessor.TotalScore.Value = 1000;
    Assert(fixture.Tracked.Position.Value == 1 && fixture.Row("alice").Position.Value == 2, "leading live score rank");
});

Check("partial leaderboard keeps unknown bottom rank and reverses rank shifts after seeking backwards", () =>
{
    using var fixture = new Fixture(providerType, [Top("alice", 900, 1), Top("bob", 700, 2)], totalScores: 100);
    Assert(fixture.Tracked.Position.Value == null, "invented a rank below fetched top scores");
    fixture.State.ScoreProcessor.TotalScore.Value = 701;
    Assert(fixture.Tracked.Position.Value == 2 && fixture.Row("bob").Position.Value == 3, "known adjacent rank not used");
    fixture.State.ScoreProcessor.TotalScore.Value = 1000;
    Assert(fixture.Tracked.Position.Value == 1 && fixture.Row("alice").Position.Value == 2 && fixture.Row("bob").Position.Value == 3, "partial leading score rank");
    fixture.State.ScoreProcessor.TotalScore.Value = 0;
    Assert(fixture.Tracked.Position.Value == null, "seek retained previously known live rank");
    Assert(fixture.Row("alice").Position.Value == 1 && fixture.Row("bob").Position.Value == 2, "seek accumulated static rank shifts");
    Assert(fixture.OrderedUsers().SequenceEqual(new[] { "alice", "bob", "replay-user" }), "seek retained stale display order");
});

Check("gaps in partial leaderboard do not turn a relative position into a global rank", () =>
{
    using var fixture = new Fixture(providerType, [Top("alice", 900, 1), Top("bob", 700, 10)], totalScores: 100);
    fixture.State.ScoreProcessor.TotalScore.Value = 800;
    Assert(fixture.Tracked.Position.Value == null, "invented a global rank inside an unfetched gap");
    Assert(fixture.Row("alice").Position.Value == 1 && fixture.Row("bob").Position.Value == 10, "static ranks changed across a gap");
    Assert(fixture.Tracked.DisplayOrder.Value == 2, "relative order must still be known");
});

Check("live row binds replay score processor rather than showing final score or local account", () =>
{
    using var fixture = new Fixture(providerType, [Top("alice", 900, 1)], totalScores: 1);
    Assert(fixture.Tracked.User.Username == "replay-user" && fixture.Tracked.User.OnlineID == 42, "tracked row uses another identity");
    Assert(fixture.Tracked.TotalScore.Value == 0, "tracked row starts with final replay score");
    fixture.State.ScoreProcessor.TotalScore.Value = 123;
    fixture.State.ScoreProcessor.Accuracy.Value = 0.975;
    fixture.State.ScoreProcessor.Combo.Value = 3;
    fixture.State.ScoreProcessor.HighestCombo.Value = 12;
    Assert(fixture.Tracked.TotalScore.Value == 123 && fixture.Tracked.Accuracy.Value == 0.975, "live score and accuracy not bound");
    Assert(fixture.Tracked.Combo.Value == 12, "highest gameplay combo not reflected");
    fixture.State.ScoreProcessor.HighestCombo.Value = 2;
    fixture.State.ScoreProcessor.Accuracy.Value = 0.5;
    Assert(fixture.Tracked.Combo.Value == 2 && fixture.Tracked.Accuracy.Value == 0.5, "rewind does not update live metrics");
    Assert(fixture.State.Score.ScoreInfo.TotalScore == 999999, "provider changed stored final replay score");
});

Check("empty complete and partial leaderboards distinguish known first rank from unknown rank", () =>
{
    using var complete = new Fixture(providerType, [], totalScores: 0);
    using var partial = new Fixture(providerType, [], totalScores: 100);
    Assert(complete.Tracked.Position.Value == 1, "empty complete leaderboard first rank");
    Assert(partial.Tracked.Position.Value == null, "empty partial leaderboard invented first rank");
});

Check("replay constructor restores explicit leaderboard choice after upstream replay defaults", () =>
{
    using var fixture = new Fixture(providerType, [], totalScores: 0);
    Type playerType = viewer.GetType("osu_replay_renderer_netcore.RecorderReplayPlayer", throwOnError: true)!;
    using var disabled = (Player)Activator.CreateInstance(playerType, [fixture.State.Score, true, false, null])!;
    using var recorded = (Player)Activator.CreateInstance(playerType, [fixture.State.Score, true, false, fixture.Leaderboard])!;
    using var viewed = (Player)Activator.CreateInstance(playerType, [fixture.State.Score, false, false, fixture.Leaderboard])!;
    Assert(!disabled.Configuration.ShowLeaderboard, "upstream replay default enabled an absent leaderboard");
    Assert(recorded.Configuration.ShowLeaderboard, "hidden recording overlays disabled requested leaderboard");
    Assert(viewed.Configuration.ShowLeaderboard, "replay viewing disabled requested leaderboard");
});

Check("overlay caches provider locally and preserves parent offline API and provider", () =>
{
    using var fixture = new Fixture(providerType, [], totalScores: 0);
    using var api = new DummyAPIAccess();
    api.Logout();
    var parentProvider = new EmptyGameplayLeaderboardProvider();
    var parent = new DependencyContainer();
    parent.CacheAs<IAPIProvider>(api);
    parent.CacheAs<IGameplayLeaderboardProvider>(parentProvider);
    Type overlayType = viewer.GetType("osu_replay_renderer_netcore.Online.ReplayLeaderboard", throwOnError: true)!;
    using var overlay = (IDisposable)Activator.CreateInstance(overlayType, fixture.Leaderboard, fixture.State)!;
    var create = overlayType.GetMethod("CreateChildDependencies", BindingFlags.Instance | BindingFlags.NonPublic)!;
    var scoped = (IReadOnlyDependencyContainer)create.Invoke(overlay, [parent])!;
    Assert(scoped.Get<IGameplayLeaderboardProvider>().GetType() == providerType, "overlay did not cache its provider");
    Assert(ReferenceEquals(parent.Get<IGameplayLeaderboardProvider>(), parentProvider), "overlay replaced global leaderboard provider");
    Assert(ReferenceEquals(scoped.Get<IAPIProvider>(), api) && ReferenceEquals(parent.Get<IAPIProvider>(), api), "overlay replaced shared offline API");
    Assert(!api.IsLoggedIn && api.State.Value == APIState.Offline, "overlay logged in shared API");
});

Console.WriteLine($"{checks} checks passed");

void Check(string name, Action test)
{
    test();
    checks++;
    Console.WriteLine($"PASS {name}");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static ScoreInfo Top(string name, long total, int position) => new()
{
    User = new APIUser { Id = position, Username = name },
    TotalScore = total,
    Position = position,
    OnlineID = position,
    MaxCombo = 50,
    Accuracy = 0.99
};

sealed class Fixture : IDisposable
{
    public GameplayState State { get; }
    public LeaderboardScores Leaderboard { get; }
    public IGameplayLeaderboardProvider Provider { get; }
    public GameplayLeaderboardScore Tracked => Provider.Scores.Single(score => score.Tracked);

    public Fixture(Type providerType, ScoreInfo[] scores, int totalScores)
    {
        var beatmap = new Beatmap();
        beatmap.HitObjects.Add(new HitCircle { StartTime = 1000 });
        var ruleset = new OsuRuleset();
        var replay = new Score
        {
            ScoreInfo = new ScoreInfo(beatmap.BeatmapInfo, ruleset.RulesetInfo)
            {
                User = new APIUser { Id = 42, Username = "replay-user" },
                TotalScore = 999999
            }
        };
        State = new GameplayState(beatmap, ruleset, score: replay);
        Leaderboard = LeaderboardScores.Success(scores, scores.Length, totalScores, null);
        Provider = (IGameplayLeaderboardProvider)Activator.CreateInstance(providerType, Leaderboard, State)!;
    }

    public GameplayLeaderboardScore Row(string username) => Provider.Scores.Single(score => score.User.Username == username);
    public IEnumerable<string> OrderedUsers() => Provider.Scores.OrderBy(score => score.DisplayOrder.Value).Select(score => score.User.Username);
    public void Dispose() => ((IDisposable)Provider).Dispose();
}

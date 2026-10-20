using System.Reflection;
using osu.Framework.Allocation;
using osu.Framework.Bindables;
using osu.Framework.Testing;
using osu.Game.Beatmaps;
using osu.Game.Configuration;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Online.Leaderboards;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Osu.Objects;
using osu.Game.Rulesets.Scoring;
using osu.Game.Scoring;
using osu.Game.Screens.Play;
using osu.Game.Screens.Play.Leaderboards;
using osu_replay_renderer_netcore;

int checks = 0;
Check("disabled leaderboard keeps native defaults and parent dependencies", () =>
{
    using var fixture = new Fixture();
    using var player = fixture.CreatePlayer(null);
    var scoped = fixture.Dependencies(player);
    Assert(!player.Configuration.ShowLeaderboard, "upstream ReplayPlayer default enabled an absent leaderboard");
    Assert(ReferenceEquals(scoped.Get<LeaderboardManager>(), fixture.Manager), "disabled replay replaced global leaderboard manager");
    Assert(ReferenceEquals(scoped.Get<OsuConfigManager>(), fixture.Config), "disabled replay replaced source config");
    Assert(ReferenceEquals(scoped.Get<IGameplaySettings>(), fixture.Config), "disabled replay replaced gameplay settings");
});

Check("enabled replay scopes supplied scores without replacing parent manager or offline API", () =>
{
    using var fixture = new Fixture();
    using var player = fixture.CreatePlayer(fixture.Leaderboard);
    var scoped = fixture.Dependencies(player);
    var manager = scoped.Get<LeaderboardManager>();
    Assert(!ReferenceEquals(manager, fixture.Manager), "replay reused global manager for downloaded scores");
    Assert(ReferenceEquals(manager.Scores.Value, fixture.Leaderboard), "scoped manager lost supplied scores");
    Assert(fixture.Manager.Scores.Value == null && ReferenceEquals(fixture.Parent.Get<LeaderboardManager>(), fixture.Manager), "parent leaderboard changed");
    Assert(ReferenceEquals(scoped.Get<IAPIProvider>(), fixture.Api) && !fixture.Api.IsLoggedIn, "replay changed shared offline API");
    Assert(ReferenceEquals(scoped.Get<OsuConfigManager>(), fixture.Config), "replay replaced the native source config");
    Assert(ReferenceEquals(scoped.Get<IGameplaySettings>(), fixture.Config), "replay replaced native gameplay settings");
});

Check("native replay provider consumes scoped scores and binds live replay metrics", () =>
{
    using var fixture = new Fixture();
    using var player = fixture.CreatePlayer(fixture.Leaderboard);
    var scoped = new DependencyContainer(fixture.Dependencies(player));
    scoped.CacheAs(fixture.State);
    var provider = (SoloGameplayLeaderboardProvider)typeof(ReplayPlayer)
        .GetField("leaderboardProvider", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(player)!;
    scoped.Inject(provider);
    typeof(SoloGameplayLeaderboardProvider).GetMethod("LoadComplete", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(provider, null);
    Assert(provider.Scores.Count == 3, "native provider did not consume downloaded scores plus replay row");
    Assert(provider.Scores.Count(row => !row.Tracked) == 2, "native provider omitted downloaded scores");
    var tracked = provider.Scores.Single(row => row.Tracked);
    Assert(tracked.User.Username == "replay-user" && tracked.User.OnlineID == 42, "native row has the wrong replay identity");
    Assert(tracked.TotalScore.Value == 0 && tracked.Position.Value == null, "native provider substituted stored final score or invented a rank below fetched scores");
    fixture.State.ScoreProcessor.TotalScore.Value = 800;
    fixture.State.ScoreProcessor.HighestCombo.Value = 12;
    fixture.State.ScoreProcessor.Accuracy.Value = 0.975;
    typeof(SoloGameplayLeaderboardProvider).GetMethod("sort", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(provider, null);
    Assert(tracked.TotalScore.Value == 800 && tracked.Combo.Value == 12 && tracked.Accuracy.Value == 0.975, "native provider did not bind live metrics");
    Assert(tracked.Position.Value == 2, "native provider did not rank supplied scores");
    fixture.State.ScoreProcessor.TotalScore.Value = 0;
    typeof(SoloGameplayLeaderboardProvider).GetMethod("sort", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(provider, null);
    Assert(tracked.Position.Value == null, "native provider retained old rank after seeking backwards");
});

Check("native leaderboard enablement survives hidden recording overlays and normal replay viewing", () =>
{
    using var fixture = new Fixture();
    using var recorded = fixture.CreatePlayer(fixture.Leaderboard, hideOverlays: true);
    using var viewed = fixture.CreatePlayer(fixture.Leaderboard, hideOverlays: false);
    Assert(recorded.Configuration.ShowLeaderboard && viewed.Configuration.ShowLeaderboard, "requested native leaderboard was disabled");
    Assert(!recorded.Configuration.AllowUserInteraction && viewed.Configuration.AllowUserInteraction, "leaderboard changed replay interaction policy");
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

sealed class Fixture : IDisposable
{
    private readonly Type playerType = typeof(GameSettings).Assembly.GetType("osu_replay_renderer_netcore.RecorderReplayPlayer", throwOnError: true)!;
    public TemporaryNativeStorage Storage { get; } = new("replay-leaderboard-" + Guid.NewGuid().ToString("N"));
    public OsuConfigManager Config { get; }
    public LeaderboardManager Manager { get; } = new();
    public DummyAPIAccess Api { get; } = new();
    public DependencyContainer Parent { get; } = new();
    public GameplayState State { get; }
    public LeaderboardScores Leaderboard { get; }
    public Fixture()
    {
        Config = new OsuConfigManager(Storage);
        Config.SetValue(OsuSetting.GameplayLeaderboard, false);
        Api.Logout();
        Parent.CacheAs(Config);
        Parent.CacheAs<IGameplaySettings>(Config);
        Parent.CacheAs(Manager);
        Parent.CacheAs<IAPIProvider>(Api);
        var beatmap = new Beatmap();
        beatmap.HitObjects.Add(new HitCircle { StartTime = 1000 });
        var ruleset = new OsuRuleset();
        Parent.CacheAs(new Bindable<WorkingBeatmap>());
        Parent.CacheAs(new Bindable<RulesetInfo>(ruleset.RulesetInfo));
        Parent.CacheAs(new Bindable<IReadOnlyList<Mod>>(Array.Empty<Mod>()));
        var replay = new Score
        {
            ScoreInfo = new ScoreInfo(beatmap.BeatmapInfo, ruleset.RulesetInfo)
            {
                User = new APIUser { Id = 42, Username = "replay-user" },
                TotalScore = 999999
            }
        };
        State = new GameplayState(beatmap, ruleset, score: replay);
        Leaderboard = LeaderboardScores.Success([Top("alice", 900, 1), Top("bob", 700, 2)], 2, 100, null);
    }

    public Player CreatePlayer(LeaderboardScores leaderboard, bool hideOverlays = true) =>
        (Player)Activator.CreateInstance(playerType, [State.Score, hideOverlays, false, leaderboard])!;

    public IReadOnlyDependencyContainer Dependencies(Player player) => (IReadOnlyDependencyContainer)playerType
        .GetMethod("CreateChildDependencies", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(player, [Parent])!;

    public void Dispose()
    {
        Api.Dispose();
        Manager.Dispose();
        Config.Dispose();
        Storage.Dispose();
    }

    private static ScoreInfo Top(string name, long total, int position) => new()
    {
        User = new APIUser { Id = position, Username = name },
        TotalScore = total,
        Position = position,
        OnlineID = position,
        MaxCombo = 50,
        Accuracy = 0.99
    };
}

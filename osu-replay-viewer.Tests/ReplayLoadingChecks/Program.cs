using System.Diagnostics;
using System.IO.Compression;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using osu.Framework;
using osu.Framework.Screens;
using osu.Framework.Testing;
using osu.Game.Beatmaps;
using osu.Game.Online.API;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Replays;
using osu.Game.Replays.Legacy;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Catch;
using osu.Game.Rulesets.Mania;
using osu.Game.Rulesets.Mods;
using osu.Game.Rulesets.Osu;
using osu.Game.Rulesets.Scoring;
using osu.Game.Rulesets.Taiko;
using osu.Game.Scoring;
using osu.Game.Scoring.Legacy;
using osu_replay_renderer_netcore;

var assembly = typeof(GameSettings).Assembly;
var compatibility = assembly.GetType("osu_replay_renderer_netcore.ReplayCompatibility", true)!;

if (args.Length > 0)
{
    RunHost(args[0]);
    return;
}

int checks = 0;
Check("official rulesets and supported mods remain accepted", () =>
{
    foreach (var ruleset in new Ruleset[] { new OsuRuleset(), new TaikoRuleset(), new CatchRuleset(), new ManiaRuleset() })
    {
        Assert(Invoke(compatibility.GetMethod("CreateRuleset")!, null, ruleset.RulesetInfo) is Ruleset, "ruleset was rejected");
        ValidateMods(ruleset.AllMods.OfType<Mod>().ToArray());
    }
    ValidateMods(Array.Empty<Mod>());
});
Check("unknown STC mod reports its original acronym", () =>
{
    var mods = new ScoreInfo
    {
        Ruleset = new OsuRuleset().RulesetInfo,
        APIMods = [new APIMod { Acronym = "STC" }]
    }.Mods;
    Assert(mods.Single() is UnknownMod, "fixture is no longer an unknown mod");
    Expect<NotSupportedException>(() => ValidateMods(mods), "STC");
});
Check("missing and unavailable rulesets produce explicit errors", () =>
{
    var create = compatibility.GetMethod("CreateRuleset")!;
    Expect<RulesetLoadException>(() => Invoke(create, null, [null]), "does not specify a ruleset");
    var unavailable = new RulesetInfo { ShortName = "custom", OnlineID = 42 };
    Expect<RulesetLoadException>(() => Invoke(create, null, unavailable), "custom");
    unavailable.Available = true;
    unavailable.InstantiationInfo = "Missing.Ruleset, Missing.Assembly";
    Expect<RulesetLoadException>(() => Invoke(create, null, unavailable), "42");
});
foreach (var scenario in new[] { "unknown-ruleset", "unknown-mod", "empty-beatmap", "supported-replay", "mod-override" })
{
    var start = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true };
    start.Environment["OSU_API_CLIENT_ID"] = "";
    start.Environment["OSU_API_CLIENT_SECRET"] = "";
    if (Path.GetFileNameWithoutExtension(Environment.ProcessPath) == "dotnet")
        start.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
    start.ArgumentList.Add(scenario);
    using var process = Process.Start(start)!;
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    var timer = Stopwatch.StartNew();
    try
    {
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(30));
    }
    catch (TimeoutException)
    {
        process.Kill(entireProcessTree: true);
        throw new Exception($"{scenario} did not exit within 30 seconds\n{await stderr}");
    }
    string error = await stderr;
    string output = await stdout;
    bool shouldPlay = scenario is "supported-replay" or "mod-override";
    string expected = scenario switch
    {
        "unknown-ruleset" => "Unknown replay ruleset ID 255",
        "unknown-mod" => "Unsupported replay mod(s): STC",
        _ => "Player could not load the beatmap"
    };
    Assert(shouldPlay
        ? process.ExitCode == 0 && output.Contains("Gameplay entered")
        : process.ExitCode == 1 && error.Contains(expected), $"{scenario}: exit={process.ExitCode}\n{error}\n{output}");
    checks++;
    Console.WriteLine($"PASS {scenario}: host exited with code {process.ExitCode} in {timer.Elapsed.TotalSeconds:F2}s");
}
Console.WriteLine($"{checks} checks passed");

void ValidateMods(Mod[] mods) => Invoke(compatibility.GetMethod("ValidateMods")!, null, (object)mods);

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

static object Invoke(MethodInfo method, object target, params object[] args)
{
    try { return method.Invoke(target, args); }
    catch (TargetInvocationException error) when (error.InnerException != null)
    {
        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error.InnerException).Throw();
        throw;
    }
}

static void Expect<T>(Action action, string message) where T : Exception
{
    try { action(); }
    catch (T error) when (error.Message.Contains(message)) { return; }
    throw new Exception($"Expected {typeof(T).Name} containing '{message}'");
}

void RunHost(string scenario)
{
    string folder = Path.Combine(Path.GetTempPath(), "replay-loading-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(folder);
    try
    {
        string replayPath = Path.Combine(folder, "replay.osr");
        string beatmapPath = Path.Combine(folder, "beatmap.osz");
        if (scenario == "unknown-ruleset")
            File.WriteAllBytes(replayPath, [255]);
        else
        {
            string beatmap = "osu file format v14\n[General]\nMode:0\n[Metadata]\nTitle:Regression\nArtist:Test\nCreator:Test\nVersion:Test\n[Difficulty]\nHPDrainRate:5\nCircleSize:4\nOverallDifficulty:5\nApproachRate:5\nSliderMultiplier:1.4\nSliderTickRate:1\n[TimingPoints]\n0,500,4,2,1,60,1,0\n[HitObjects]\n";
            if (scenario != "empty-beatmap")
                beatmap += "256,192,1000,1,0,0:0:0:0:\n";
            byte[] data = Encoding.UTF8.GetBytes(beatmap);
            using (var zip = ZipFile.Open(beatmapPath, ZipArchiveMode.Create))
            using (var entry = zip.CreateEntry("test.osu").Open())
                entry.Write(data);
            var score = new Score
            {
                Replay = new Replay
                {
                    Frames =
                    [
                        new LegacyReplayFrame(0, 256, 192, ReplayButtonState.None),
                        new LegacyReplayFrame(1000, 256, 192, ReplayButtonState.Left1),
                        new LegacyReplayFrame(1100, 256, 192, ReplayButtonState.None)
                    ]
                },
                ScoreInfo = new ScoreInfo
                {
                    Ruleset = new OsuRuleset().RulesetInfo,
                    BeatmapInfo = new BeatmapInfo { MD5Hash = Convert.ToHexString(MD5.HashData(data)).ToLowerInvariant() },
                    User = new APIUser { Username = "regression" },
                    MaximumStatistics = new Dictionary<HitResult, int> { [HitResult.Great] = 1 },
                    Statistics = new Dictionary<HitResult, int> { [HitResult.Great] = 1 },
                    APIMods = scenario is "unknown-mod" or "mod-override" ? [new APIMod { Acronym = "STC" }] : []
                }
            };
            using var stream = File.Create(replayPath);
            new LegacyScoreEncoder(score, null).Encode(stream);
        }

        var gameType = assembly.GetType("osu_replay_renderer_netcore.OsuGameRecorder", true)!;
        using var game = (Game)Activator.CreateInstance(gameType, [new GameSettings(), null])!;
        gameType.GetField("ReplayViewType")!.SetValue(game, "file");
        gameType.GetField("ReplayFileLocation")!.SetValue(game, replayPath);
        if (scenario != "unknown-ruleset")
            gameType.GetProperty("BeatmapPath")!.SetValue(game, beatmapPath);
        if (scenario == "mod-override")
            ((List<string>)gameType.GetField("ModsOverride")!.GetValue(game)!).Add("no-mod");
        using var host = new TestRunHeadlessGameHost("replay-loading-" + Guid.NewGuid().ToString("N"));
        if (scenario is "supported-replay" or "mod-override")
        {
            var scheduler = (osu.Framework.Threading.Scheduler)typeof(osu.Framework.Graphics.Drawable)
                .GetProperty("Scheduler", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)!;
            scheduler.AddDelayed(() =>
            {
                if (gameType.GetField("Player", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(game)
                    is osu.Game.Screens.Play.Player player && player.IsCurrentScreen())
                {
                    Assert(player.LoadedBeatmapSuccessfully, "player entered without loading the beatmap");
                    Console.WriteLine("Gameplay entered");
                    host.Exit();
                }
            }, 10, repeat: true);
        }
        host.Run(game);
    }
    finally
    {
        Directory.Delete(folder, recursive: true);
    }
}

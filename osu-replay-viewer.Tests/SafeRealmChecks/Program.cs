using System.Reflection;
using System.Runtime.CompilerServices;
using System.IO.Compression;
using osu.Game;
using osu.Game.Overlays.Notifications;
using System.Security.Cryptography;
using System.Text;
using osu.Framework.Platform;
using osu.Game.Beatmaps;
using Decoder = osu.Game.Beatmaps.Formats.Decoder;
using osu.Game.Database;
using osu.Game.IO.Archives;
using osu.Game.Online.API;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Osu;
using osu.Game.Scoring;
using osu.Game.Skinning;
using osu_replay_renderer_netcore;
using Realms;

int checks = 0;
Type patcher = typeof(GameSettings).Assembly.GetType("osu_replay_renderer_netcore.Patching.SafeRealmPatcher", true)!;

Check("runtime writes and pending-deletion cleanup leave the source database and files unchanged", () =>
{
    using var fixture = new Fixture();
    byte[] before = File.ReadAllBytes(fixture.Path);
    DateTime modified = File.GetLastWriteTimeUtc(fixture.Path);
    using (Protect())
    using (var access = new RealmAccess(fixture.Storage, "client"))
    {
        access.Write(r => r.Add(new SkinInfo { Name = "runtime-only" }));
        Assert(access.Run(r => r.All<SkinInfo>().Any(s => s.Name == "runtime-only")), "working database cannot write");
        Assert(access.Run(r => r.Config.DatabasePath) != fixture.Path, "runtime opened the source database");
        new RealmFileStore(access, fixture.Storage).Cleanup();
        Assert(File.Exists(fixture.Sentinel), "file cleanup removed an osu! asset");
    }
    Assert(before.SequenceEqual(File.ReadAllBytes(fixture.Path)), "source database bytes changed");
    Assert(modified == File.GetLastWriteTimeUtc(fixture.Path), "source timestamp changed");
    using var source = fixture.Open();
    Assert(source.All<SkinInfo>().Count() == 1 && source.All<SkinInfo>().Single().DeletePending, "source pending deletion was processed");
});

Check("startup does not recover a newer-version backup over the source", () =>
{
    using var fixture = new Fixture();
    byte[] before = File.ReadAllBytes(fixture.Path);
    string recovery = fixture.Storage.GetFullPath("client_newer_version.realm");
    File.WriteAllText(recovery, "must not be restored or deleted");
    using (Protect())
    using (var access = new RealmAccess(fixture.Storage, "client"))
        Assert(access.Run(r => r.All<SkinInfo>().Any()), "snapshot did not open");
    Assert(before.SequenceEqual(File.ReadAllBytes(fixture.Path)), "recovery replaced the source");
    Assert(File.ReadAllText(recovery) == "must not be restored or deleted", "recovery file changed");
});

Check("corrupt and missing databases fail without deletion, recreation or backup files", () =>
{
    foreach (bool corrupt in new[] { false, true })
    {
        using var fixture = new Fixture(seed: false);
        if (corrupt) File.WriteAllText(fixture.Path, "not a Realm database");
        using (Protect())
            Throws(() => { using var access = new RealmAccess(fixture.Storage, "client"); });
        Assert(File.Exists(fixture.Path) == corrupt, "missing database was created or corrupt file deleted");
        if (corrupt) Assert(File.ReadAllText(fixture.Path) == "not a Realm database", "corrupt file overwritten");
        Assert(!Directory.EnumerateFiles(fixture.Root, "*corrupt*").Any(), "automatic recovery backup created");
    }
});

Check("beatmap archive import persists to source and publishes matching beatmap IDs and files to snapshot", () =>
{
    using var fixture = new Fixture();
    using (Protect())
    using (var access = new RealmAccess(fixture.Storage, "client"))
    using (var rulesets = new RealmRulesetStore(access))
    {
        Decoder.RegisterDependencies(rulesets);
        var importer = new BeatmapImporter(fixture.Storage, access);
        using var archive = BeatmapArchive();
        var result = MapManager(importer).Import(new BeatmapSetInfo(), archive)!;
        Assert(result != null, "beatmap import failed");
        Guid id = result.ID;
        Guid difficulty = result.PerformRead(s => s.Beatmaps.Single().ID);
        Assert(result.PerformRead(s => s.Files.Count) == 2, "snapshot lost imported file records");
        using (var repeated = BeatmapArchive())
            Assert(MapManager(importer).Import(new BeatmapSetInfo(), repeated)!.ID == id, "repeat import changed the stored set ID");
        using var source = fixture.Open();
        var stored = source.Find<BeatmapSetInfo>(id)!;
        Assert(stored != null && stored.Beatmaps.Single().ID == difficulty, "source and snapshot IDs differ");
        Assert(stored.Files.Count == 2, "source archive files missing");
        Assert(source.All<SkinInfo>().Single().DeletePending, "import cleaned unrelated pending deletions");
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture.Path)));
        access.Write(r => r.Find<BeatmapSetInfo>(id)!.OnlineID = 9999);
        Assert(hash == Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(fixture.Path))), "write permission leaked after import");
    }
});

Check("skin archive import persists without changing unrelated source records", () =>
{
    using var fixture = new Fixture();
    using (Protect())
    using (var access = new RealmAccess(fixture.Storage, "client"))
    {
        var importer = new SkinImporter(fixture.Storage, access, null!);
        var task = ZipTask("test-skin.osk", ("skin.ini", "[General]\nName: test-skin\nAuthor: test\nVersion: 2.7\n"));
        var result = SkinManager(importer).Import(task).GetAwaiter().GetResult()!;
        Assert(result != null && result.PerformRead(s => s.Files.Count) > 0, "skin missing from snapshot");
        using var source = fixture.Open();
        Assert(source.Find<SkinInfo>(result.ID) != null, "skin not persisted to source");
        Assert(source.All<SkinInfo>().Count(s => s.DeletePending) == 1, "import removed an unrelated skin");
    }
});

Check("replay archive import preserves its score ID and beatmap reference in both databases", () =>
{
    using var fixture = new Fixture();
    using (Protect())
    using (var access = new RealmAccess(fixture.Storage, "client"))
    using (var rulesets = new RealmRulesetStore(access))
    using (var api = new DummyAPIAccess())
    {
        Decoder.RegisterDependencies(rulesets);
        using var beatmapArchive = BeatmapArchive();
        var map = MapManager(new BeatmapImporter(fixture.Storage, access)).Import(new BeatmapSetInfo(), beatmapArchive)!;
        BeatmapInfo difficulty = map.PerformRead(s => s.Beatmaps.Single().Detach());
        var item = new ScoreInfo(difficulty, rulesets.GetRuleset(0)!) { OnlineID = -1 };
        var importer = new ScoreImporter(rulesets, () => null!, fixture.Storage, access, api);
        using var archive = new MemoryArchive("replay", ("replay.osr", "test replay data"));
        var result = ScoreManager(importer).Import(item, archive)!;
        Assert(result.PerformRead(s => s.BeatmapInfo!.ID) == difficulty.ID, "snapshot score lost its beatmap");
        using var source = fixture.Open();
        var stored = source.Find<ScoreInfo>(result.ID);
        Assert(stored != null && stored.BeatmapInfo!.ID == difficulty.ID && stored.Files.Count == 1, "replay not persisted to source");
    }
});

Check("replay import brings in its beatmap when osu! added that beatmap after the working snapshot was created", () =>
{
    using var fixture = new Fixture();
    using (Protect())
    using (var access = new RealmAccess(fixture.Storage, "client"))
    using (var rulesets = new RealmRulesetStore(access))
    using (var api = new DummyAPIAccess())
    {
        BeatmapInfo difficulty;
        using (var source = fixture.Open(readOnly: false))
        {
            var set = new BeatmapSetInfo();
            var info = new BeatmapInfo(new OsuRuleset().RulesetInfo, new BeatmapDifficulty(), new BeatmapMetadata())
            {
                BeatmapSet = set, Hash = "external-map", MD5Hash = "0123456789abcdef0123456789abcdef"
            };
            set.Beatmaps.Add(info);
            source.Write(() =>
            {
                info.Ruleset = source.Find<RulesetInfo>("osu")!;
                source.Add(set);
            });
            difficulty = info.Detach();
        }
        var importer = new ScoreImporter(rulesets, () => null!, fixture.Storage, access, api);
        using var archive = new MemoryArchive("external-map-replay", ("replay.osr", "external replay data"));
        var result = ScoreManager(importer).Import(new ScoreInfo(difficulty, rulesets.GetRuleset(0)!) { OnlineID = -1 }, archive)!;
        Assert(result.PerformRead(s => s.BeatmapInfo!.ID) == difficulty.ID, "replay dependency was not published to the snapshot");
        Assert(access.Run(r => r.Find<BeatmapInfo>(difficulty.ID) != null), "external beatmap is missing from the snapshot");
    }
});

Check("older-schema import fails instead of migrating the source and does not leave write permission enabled", () =>
{
    using var fixture = new Fixture(version: 51);
    using (Protect())
    using (var access = new RealmAccess(fixture.Storage, "client"))
    {
        var task = ZipTask("skin.osk", ("skin.ini", "[General]\nName: skin\n"));
        Throws(() => SkinManager(new SkinImporter(fixture.Storage, access, null!)).Import(task).GetAwaiter().GetResult());
        access.Write(r => r.Add(new SkinInfo { Name = "private-after-error" }));
    }
    using var source = fixture.Open();
    Assert(source.All<SkinInfo>().Count() == 1 && source.All<SkinInfo>().Single().DeletePending, "failed import changed original data");
    Assert(!Directory.EnumerateFiles(fixture.Root, "*_corrupt.realm").Any(), "failed import triggered destructive recovery");
});

Check("batch, notification and path-based manager imports all persist and reuse file records", () =>
{
    using var fixture = new Fixture();
    using (Protect())
    using (var access = new RealmAccess(fixture.Storage, "client"))
    {
        var manager = SkinManager(new SkinImporter(fixture.Storage, access, null!));
        manager.Import(new[] { ZipTask("batch.osk", ("skin.ini", "[General]\nName: batch\n")) }).GetAwaiter().GetResult();
        var imported = manager.Import(new ProgressNotification(),
            new[] { ZipTask("notified.osk", ("skin.ini", "[General]\nName: notified\n")) }).GetAwaiter().GetResult();
        Assert(imported.Count() == 1, "notification import returned no skin");
        string path = System.IO.Path.Combine(fixture.Root, "path.osk");
        using (var stream = ZipTask("path.osk", ("skin.ini", "[General]\nName: path\n")).Stream!)
        using (var file = File.Create(path)) stream.CopyTo(file);
        manager.Import(path).GetAwaiter().GetResult();
        using var source = fixture.Open();
        Assert(source.All<SkinInfo>().Count() == 4, "one of the manager import overloads did not persist");
    }
});

Check("a background callback inherited from an import cannot keep write access to osu!", () =>
{
    using var fixture = new Fixture();
    using (Protect())
    using (var access = new RealmAccess(fixture.Storage, "client"))
    {
        using var release = new ManualResetEventSlim();
        var importer = new DeferredBeatmapImporter(fixture.Storage, access, release);
        using var archive = BeatmapArchive();
        MapManager(importer).Import(new BeatmapSetInfo(), archive);
        byte[] before = File.ReadAllBytes(fixture.Path);
        release.Set();
        Assert(importer.Callback!.Wait(TimeSpan.FromSeconds(10)), "deferred callback did not finish");
        Assert(before.SequenceEqual(File.ReadAllBytes(fixture.Path)), "expired import scope wrote to the source");
        Assert(access.Run(r => r.All<SkinInfo>().Any(s => s.Name == "deferred")), "callback did not use the working snapshot");
    }
});

Check("shutdown cancels and drains an unfinished import before removing the protection", () =>
{
    using var fixture = new Fixture();
    byte[] before = File.ReadAllBytes(fixture.Path);
    using var protection = Protect();
    using var access = new RealmAccess(fixture.Storage, "client");
    using var importer = new BlockingSkinImporter(fixture.Storage, access);
    var task = SkinManager(importer).Import(ZipTask("blocked.osk", ("skin.ini", "[General]\nName: blocked\n")));
    Assert(importer.Started.Wait(TimeSpan.FromSeconds(10)), "import did not start");
    var closing = Task.Run(() => protection.Dispose());
    var shutdown = (CancellationTokenSource)patcher.GetField("shutdown", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(protection)!;
    Assert(SpinWait.SpinUntil(() => shutdown.IsCancellationRequested, TimeSpan.FromSeconds(10)), "shutdown did not cancel imports");
    Assert(!closing.IsCompleted, "protection was removed while an import was still active");
    importer.Release.Set();
    Assert(closing.Wait(TimeSpan.FromSeconds(10)) && task.IsCompleted, "shutdown did not drain the import");
    Assert(before.SequenceEqual(File.ReadAllBytes(fixture.Path)), "cancelled import changed the source database");
});

Check("newer schemas fail without downgrading or replacing the source", () =>
{
    using var fixture = new Fixture(version: 53);
    byte[] before = File.ReadAllBytes(fixture.Path);
    using (Protect())
        Throws(() => { using var access = new RealmAccess(fixture.Storage, "client"); });
    Assert(before.SequenceEqual(File.ReadAllBytes(fixture.Path)), "newer source database was replaced");
    Assert(!File.Exists(fixture.Storage.GetFullPath("client_newer_version.realm")), "downgrade recovery ran against the source");
});

#if DEBUG
Check("Debug uses an existing versioned Realm without creating or migrating files in osu! storage", () =>
{
    using var fixture = new Fixture();
    byte[] before = File.ReadAllBytes(fixture.Path);
    string versioned = fixture.Storage.GetFullPath("client_52.realm");
    File.Move(fixture.Path, versioned);
    using (Protect())
    using (var access = new RealmAccess(fixture.Storage, "client"))
        Assert(access.Filename == "client_52.realm", "versioned database was not selected");
    Assert(before.SequenceEqual(File.ReadAllBytes(versioned)) && !File.Exists(fixture.Path), "Debug modified the source database");
});
#endif

Check("the actual recorder starts headlessly and lists replays without modifying the source", () =>
{
    using var fixture = new Fixture();
    byte[] before = File.ReadAllBytes(fixture.Path);
    using (Protect())
    {
        string previousDirectory = Environment.CurrentDirectory;
        try
        {
            Environment.CurrentDirectory = fixture.Root;
            using var host = new HeadlessGameHost("orv-safe-realm-check");
            Type recorder = typeof(GameSettings).Assembly.GetType("osu_replay_renderer_netcore.OsuGameRecorder", true)!;
            var game = (OsuGameBase)Activator.CreateInstance(recorder, [new GameSettings(), null])!;
            typeof(OsuGameBase).GetProperty("Storage", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(game, fixture.Storage);
            recorder.GetField("ListReplays")!.SetValue(game, true);
            host.Run(game);
        }
        finally
        {
            Environment.CurrentDirectory = previousDirectory;
        }
    }
    Assert(before.SequenceEqual(File.ReadAllBytes(fixture.Path)), "recorder startup modified the original Realm");
});

Console.WriteLine($"{checks} checks passed");

IDisposable Protect() => (IDisposable)Activator.CreateInstance(patcher)!;
void Check(string name, Action action)
{
    action();
    checks++;
    Console.WriteLine("PASS " + name);
}
static void Assert(bool value, string message) { if (!value) throw new Exception(message); }
static void Throws(Action action)
{
    try { action(); } catch (Exception) { return; }
    throw new Exception("Expected operation to fail");
}
static BeatmapManager MapManager(BeatmapImporter importer)
{
    var manager = (BeatmapManager)RuntimeHelpers.GetUninitializedObject(typeof(BeatmapManager));
    typeof(BeatmapManager).GetField("beatmapImporter", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, importer);
    return manager;
}
static ScoreManager ScoreManager(ScoreImporter importer)
{
    var manager = (ScoreManager)RuntimeHelpers.GetUninitializedObject(typeof(ScoreManager));
    typeof(ScoreManager).GetField("scoreImporter", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, importer);
    return manager;
}
static SkinManager SkinManager(SkinImporter importer)
{
    var manager = (SkinManager)RuntimeHelpers.GetUninitializedObject(typeof(SkinManager));
    typeof(SkinManager).GetField("skinImporter", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(manager, importer);
    return manager;
}
static ImportTask ZipTask(string name, params (string name, string content)[] files)
{
    var stream = new MemoryStream();
    using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        foreach (var file in files)
        {
            using var writer = new StreamWriter(zip.CreateEntry(file.name).Open());
            writer.Write(file.content);
        }
    stream.Position = 0;
    return new ImportTask(stream, name);
}

static MemoryArchive BeatmapArchive() => new("test-map", ("test.osu", """
osu file format v14
[General]
AudioFilename: audio.mp3
Mode: 0
[Metadata]
Title: Test
Artist: Test
Creator: Test
Version: Test
BeatmapID: 123
BeatmapSetID: 456
[Difficulty]
HPDrainRate: 5
CircleSize: 4
OverallDifficulty: 5
ApproachRate: 5
SliderMultiplier: 1.4
SliderTickRate: 1
[TimingPoints]
0,500,4,2,1,100,1,0
[HitObjects]
256,192,1000,1,0,0:0:0:0:
"""), ("audio.mp3", "dummy audio"));

sealed class MemoryArchive(string name, params (string name, string text)[] files) : ArchiveReader(name)
{
    public override IEnumerable<string> Filenames => files.Select(f => f.name);
    public override Stream GetStream(string name) => new MemoryStream(Encoding.UTF8.GetBytes(files.Single(f => f.name == name).text));
    public override void Dispose() { }
}

sealed class Fixture : IDisposable
{
    public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orv-safe-test-" + Guid.NewGuid().ToString("N"));
    public NativeStorage Storage { get; }
    public string Path => Storage.GetFullPath("client.realm");
    public string Sentinel => Storage.GetFullPath("files/keep-this-file");
    private readonly ulong version;
    public Fixture(bool seed = true, ulong version = 52)
    {
        this.version = version;
        Directory.CreateDirectory(Root);
        Storage = new NativeStorage(Root);
        if (seed)
        {
            using var realm = Open(readOnly: false);
            realm.Write(() =>
            {
                realm.Add(new OsuRuleset().RulesetInfo);
                realm.Add(new SkinInfo { Name = "keep-pending", DeletePending = true });
            });
        }
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Sentinel)!);
        File.WriteAllText(Sentinel, "keep");
    }
    public Realm Open(bool readOnly = true) => Realm.GetInstance(new RealmConfiguration(Path) { SchemaVersion = version, IsReadOnly = readOnly });
    public void Dispose() { Directory.Delete(Root, recursive: true); }
}

sealed class DeferredBeatmapImporter(NativeStorage storage, RealmAccess access, ManualResetEventSlim release) : BeatmapImporter(storage, access)
{
    public Task Callback = null!;
    protected override void PostImport(BeatmapSetInfo model, Realm realm, ImportParameters parameters)
    {
        base.PostImport(model, realm, parameters);
        Callback ??= Task.Run(() =>
        {
            release.Wait();
            Realm.Write(r => r.Add(new SkinInfo { Name = "deferred" }));
        });
    }
}

sealed class BlockingSkinImporter(NativeStorage storage, RealmAccess access) : SkinImporter(storage, access, null!), IDisposable
{
    public readonly ManualResetEventSlim Started = new();
    public readonly ManualResetEventSlim Release = new();
    protected override SkinInfo CreateModel(ArchiveReader archive, ImportParameters parameters)
    {
        Started.Set();
        Release.Wait();
        return base.CreateModel(archive, parameters);
    }
    public void Dispose() { Release.Set(); Release.Dispose(); Started.Dispose(); }
}

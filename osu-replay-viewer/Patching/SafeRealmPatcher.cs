using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Reflection.Emit;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HarmonyLib;
using osu.Game.Beatmaps;
using osu.Game.Database;
using osu.Game.IO.Archives;
using osu.Game.Models;
using osu.Game.Overlays.Notifications;
using osu.Game.Scoring;
using osu.Game.Skinning;
using Realms;

namespace osu_replay_renderer_netcore.Patching;

// osu! managers need writable databases for runtime caches and subscriptions
// Keep that work in a private snapshot and persist only archive imports to osu!
internal sealed class SafeRealmPatcher : PatcherBase, IDisposable
{
    private static SafeRealmPatcher current;
    private readonly string directory = Path.Combine(Path.GetTempPath(), "orv-realm-" + Guid.NewGuid().ToString("N"));
    private readonly Dictionary<string, string> snapshots = new();
    private readonly object sync = new();
    private readonly SemaphoreSlim importGate = new(1);
    private readonly AsyncLocal<ImportScope> importing = new();
    private readonly CancellationTokenSource shutdown = new();
    private readonly List<Task> pendingImports = new();
    private bool stopping;

    private sealed class ImportScope(RealmAccess access)
    {
        public readonly RealmAccess Access = access;
        public volatile bool Active = true;
    }

    public override string PatcherId() => "osureplayrenderer.SafeRealm";

    public SafeRealmPatcher()
    {
        if (current != null)
            throw new InvalidOperationException("Safe Realm access is already enabled");

        current = this;
        try
        {
            DoPatching();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    private RealmConfiguration configure(RealmAccess access, RealmConfiguration original)
    {
        string source = original.DatabasePath;
        if (importing.Value is { Active: true } scope && ReferenceEquals(scope.Access, access))
        {
            shutdown.Token.ThrowIfCancellationRequested();
            if (!File.Exists(source))
                throw new FileNotFoundException("The osu! database disappeared during import", source);

            // Never run osu!'s migrations against the source, even during an import
            original.MigrationCallback = (_, _) => throw new InvalidOperationException(
                "The osu! database needs migration; open it with a compatible osu!lazer version before importing");
            original.ShouldDeleteIfMigrationNeeded = false;
            original.ShouldCompactOnLaunch = null;
            return original;
        }

        lock (sync)
        {
            if (!snapshots.TryGetValue(source, out string snapshot))
            {
                if (!File.Exists(source))
                    throw new FileNotFoundException("Safe osu! mode requires an existing database", source);

                Directory.CreateDirectory(directory);
                snapshot = Path.Combine(directory, snapshots.Count + ".realm");
                using (var realm = Realm.GetInstance(new RealmConfiguration(source) { IsDynamic = true, IsReadOnly = true }))
                    realm.WriteCopy(new RealmConfiguration(snapshot));
                snapshots.Add(source, snapshot);
            }

            return new RealmConfiguration(snapshot)
            {
                SchemaVersion = original.SchemaVersion,
                Schema = original.Schema,
                MigrationCallback = original.MigrationCallback,
                FallbackPipePath = original.FallbackPipePath,
            };
        }
    }

    private Task<IEnumerable<Live<T>>> import<T>(RealmArchiveModelImporter<T> importer,
        Func<CancellationToken, Task<IEnumerable<Live<T>>>> operation, ImportParameters parameters, CancellationToken cancellation)
        where T : RealmObject, IHasRealmFiles, IHasGuidPrimaryKey, ISoftDelete
    {
        lock (sync)
        {
            ObjectDisposedException.ThrowIf(stopping, this);
            var task = runImport();
            pendingImports.Add(task);
            return task;
        }

        Task<IEnumerable<Live<T>>> runImport() => Task.Run(async () =>
        {
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellation, shutdown.Token);
            await importGate.WaitAsync(linked.Token).ConfigureAwait(false);
            try
            {
                var access = (RealmAccess)AccessTools.Field(typeof(RealmArchiveModelImporter<T>), "Realm").GetValue(importer);
                var scope = new ImportScope(access);
                var detached = new List<T>();
                importing.Value = scope;
                try
                {
                    foreach (var imported in await operation(linked.Token).ConfigureAwait(false))
                    {
                        if (imported == null)
                            continue;
                        detached.Add(imported.PerformRead(copyModel));
                    }
                }
                finally
                {
                    scope.Active = false;
                    importing.Value = null;
                }

                // Publish the imported IDs and metadata to the working snapshot without
                // decoding the archive again or generating different beatmap IDs
                return detached.Select(model => access.Run(realm =>
                {
                    using var transaction = realm.BeginWrite();
                    AccessTools.Method(importer.GetType(), "PreImport").Invoke(importer, new object[] { model, realm });
                    bindFiles(model, realm);
                    if (model is ScoreInfo score)
                    {
                        var beatmap = realm.Find<BeatmapInfo>(score.BeatmapInfo.ID);
                        if (beatmap == null)
                        {
                            var set = score.BeatmapInfo.BeatmapSet;
                            bindFiles(set, realm);
                            realm.Add(set, update: true);
                            beatmap = realm.Find<BeatmapInfo>(score.BeatmapInfo.ID);
                        }
                        score.BeatmapInfo = beatmap;
                        score.Ruleset = realm.Find<osu.Game.Rulesets.RulesetInfo>(score.Ruleset.ShortName);
                    }
                    realm.Add(model, update: true);
                    AccessTools.Method(importer.GetType(), "PostImport").Invoke(importer, new object[] { model, realm, parameters });
                    transaction.Commit();
                    return model.ToLive(access);
                })).ToArray().AsEnumerable();
            }
            finally
            {
                importGate.Release();
            }
        });
    }

    private static void bindFiles(IHasRealmFiles model, Realm realm)
    {
        foreach (var file in model.Files)
            file.File = realm.Find<RealmFile>(file.File.Hash) ?? realm.Add(file.File);
    }

    private static T copyModel<T>(T model) where T : RealmObject
    {
        var copy = model.Detach();
        // The upstream beatmap-set detacher deliberately omits file records
        if (model is BeatmapSetInfo set)
            foreach (var file in set.Files.Detach())
                ((BeatmapSetInfo)(object)copy).Files.Add(file);
        if (model is ScoreInfo score && score.BeatmapInfo?.BeatmapSet is BeatmapSetInfo scoreSet)
        {
            var copiedSet = copyModel(scoreSet);
            ((ScoreInfo)(object)copy).BeatmapInfo = copiedSet.Beatmaps.Single(b => b.ID == score.BeatmapInfo.ID);
        }
        return copy;
    }

    private static Task ImportPaths<T>(RealmArchiveModelImporter<T> importer, string[] paths)
        where T : RealmObject, IHasRealmFiles, IHasGuidPrimaryKey, ISoftDelete =>
        ImportTasks(importer, paths.Select(p => new ImportTask(p)).ToArray(), default);

    private static Task ImportTasks<T>(RealmArchiveModelImporter<T> importer, ImportTask[] tasks, ImportParameters parameters)
        where T : RealmObject, IHasRealmFiles, IHasGuidPrimaryKey, ISoftDelete
    {
        var notification = new ProgressNotification { State = ProgressNotificationState.Active };
        importer.PostNotification?.Invoke(notification);
        return ImportNotification(importer, notification, tasks, parameters);
    }

    private static Task<IEnumerable<Live<T>>> ImportNotification<T>(RealmArchiveModelImporter<T> importer,
        ProgressNotification notification, ImportTask[] tasks, ImportParameters parameters)
        where T : RealmObject, IHasRealmFiles, IHasGuidPrimaryKey, ISoftDelete =>
        current.import(importer, async token =>
        {
            using var registration = token.Register(() => notification.State = ProgressNotificationState.Cancelled);
            return await importer.Import(notification, tasks, parameters).ConfigureAwait(false);
        }, parameters, notification.CancellationToken);

    private static async Task<Live<T>> ImportTask<T>(RealmArchiveModelImporter<T> importer, ImportTask task,
        ImportParameters parameters, CancellationToken cancellation)
        where T : RealmObject, IHasRealmFiles, IHasGuidPrimaryKey, ISoftDelete =>
        (await current.import(importer, async token => new[] { await importer.Import(task, parameters, token).ConfigureAwait(false) },
            parameters, cancellation).ConfigureAwait(false)).SingleOrDefault();

    private static Live<T> ImportModel<T>(RealmArchiveModelImporter<T> importer, T item, ArchiveReader archive,
        ImportParameters parameters, CancellationToken cancellation)
        where T : RealmObject, IHasRealmFiles, IHasGuidPrimaryKey, ISoftDelete
    {
        // Runtime model creation stays private; only explicit archive imports persist
        if (archive == null)
            return importer.ImportModel(item, archive, parameters, cancellation);

        return current.import(importer,
            token => Task.FromResult<IEnumerable<Live<T>>>(new[] { importer.ImportModel(item, archive, parameters, token) }),
            parameters, cancellation).GetAwaiter().GetResult().SingleOrDefault();
    }

    public void Dispose()
    {
        Task[] pending;
        lock (sync)
        {
            if (stopping)
                return;
            stopping = true;
            pending = pendingImports.ToArray();
        }
        shutdown.Cancel();
        try
        {
            Task.WhenAll(pending).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            // Import failures are reported by the importer or its caller
        }
        importGate.Dispose();
        shutdown.Dispose();
        Harmony?.UnpatchAll(PatcherId());
        current = null;
        if (Directory.Exists(directory))
        {
            try
            {
                Directory.Delete(directory, recursive: true);
            }
            catch (IOException e)
            {
                Console.Error.WriteLine($"Could not remove temporary Realm snapshot: {e.Message}");
            }
            catch (UnauthorizedAccessException e)
            {
                Console.Error.WriteLine($"Could not remove temporary Realm snapshot: {e.Message}");
            }
        }
    }

    [HarmonyPatch(typeof(RealmAccess), "getConfiguration")]
    private class Configure
    {
        private static void Postfix(RealmAccess __instance, ref RealmConfiguration __result) =>
            __result = current.configure(__instance, __result);
    }

    [HarmonyPatch(typeof(RealmAccess), "prepareFirstRealmAccess")]
    private class Open
    {
        private static bool Prefix(RealmAccess __instance, ref Realm __result)
        {
            // Bypass timestamp changes, recovery, backup and deletion in osu!'s opener
            __result = (Realm)AccessTools.Method(typeof(RealmAccess), "getRealmInstance").Invoke(__instance, null);
            return false;
        }
    }

    [HarmonyPatch(typeof(RealmAccess), "cleanupPendingDeletions")]
    private class SkipPendingDeletions
    {
        private static bool Prefix() => false;
    }

    [HarmonyPatch(typeof(RealmFileStore), nameof(RealmFileStore.Cleanup))]
    private class SkipFileCleanup
    {
        private static bool Prefix() => false;
    }

#if DEBUG
    [HarmonyPatch(typeof(RealmAccess), "applyFilenameSchemaSuffix")]
    private class SelectExistingDatabase
    {
        private static bool Prefix(ref string filename, osu.Framework.Platform.Storage ___storage)
        {
            int version = (int)AccessTools.Field(typeof(RealmAccess), "schema_version").GetRawConstantValue();
            string versioned = Path.GetFileNameWithoutExtension(filename) + "_" + version + ".realm";
            if (___storage.Exists(versioned))
                filename = versioned;
            return false;
        }
    }
#endif

    [HarmonyPatch]
    private class Import
    {
        private static IEnumerable<MethodBase> TargetMethods() =>
            new[] { typeof(BeatmapManager), typeof(SkinManager), typeof(ScoreManager) }
                .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                .Where(m => m.Name == "Import");

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            int replacements = 0;
            foreach (var instruction in instructions)
            {
                if (instruction.operand is MethodInfo method && method.DeclaringType?.IsGenericType == true &&
                    method.DeclaringType.GetGenericTypeDefinition() == typeof(RealmArchiveModelImporter<>))
                {
                    string wrapper = method.Name == "ImportModel" ? nameof(ImportModel) :
                        method.GetParameters()[0].ParameterType == typeof(string[]) ? nameof(ImportPaths) :
                        method.GetParameters()[0].ParameterType == typeof(ImportTask[]) ? nameof(ImportTasks) :
                        method.GetParameters()[0].ParameterType == typeof(ProgressNotification) ? nameof(ImportNotification) :
                        method.GetParameters()[0].ParameterType == typeof(ImportTask) ? nameof(ImportTask) :
                        throw new InvalidOperationException("Unsupported osu! import signature");
                    instruction.opcode = OpCodes.Call;
                    instruction.operand = AccessTools.Method(typeof(SafeRealmPatcher), wrapper)
                        .MakeGenericMethod(method.DeclaringType.GetGenericArguments());
                    replacements++;
                }
                yield return instruction;
            }

            if (replacements != 1)
                throw new InvalidOperationException("Could not protect the osu! import entry point");
        }
    }
}

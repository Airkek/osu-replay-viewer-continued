using System;
using System.IO;
using System.Linq;
using Realms;

namespace osu_replay_renderer_netcore;

/// <summary>
/// Performs the beatmap and skin lookups used by orv-runner without starting osu! or
/// constructing RealmAccess, both of which may perform database maintenance.
/// </summary>
internal static class RealmLookup
{
    private const string beatmap_command = "--realm-has-beatmap";
    private const string skin_command = "--realm-find-skin";
    private const string beatmap_result_prefix = "REALM_BEATMAP_FOUND::";
    private const string skin_result_prefix = "REALM_SKIN_ID::";

    public static bool TryHandleCommand(string[] args, out int exitCode)
    {
        exitCode = 0;

        if (args.Length == 0)
            return false;

        if (args[0].Equals(beatmap_command, StringComparison.Ordinal))
        {
            exitCode = lookupBeatmap(args);
            return true;
        }

        if (args[0].Equals(skin_command, StringComparison.Ordinal))
        {
            exitCode = lookupSkin(args);
            return true;
        }

        return false;
    }

    private static int lookupBeatmap(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine($"Usage: {beatmap_command} <path-to-client.realm> <beatmap-md5>");
            return 2;
        }

        string beatmapMd5 = args[2].Trim().ToLowerInvariant();

        if (beatmapMd5.Length != 32 || !beatmapMd5.All(Uri.IsHexDigit))
        {
            Console.Error.WriteLine("Beatmap MD5 must contain exactly 32 hexadecimal characters.");
            return 2;
        }

        try
        {
            using var realm = openReadOnly(args[1]);
            if (realm == null)
            {
                writeBeatmapResult(false);
                return 0;
            }

            bool found = realm.DynamicApi
                              .All("Beatmap")
                              .Filter("MD5Hash ==[c] $0", beatmapMd5)
                              .Any();

            writeBeatmapResult(found);
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Read-only Realm lookup failed: {e.Message}");
            return 3;
        }
    }

    private static int lookupSkin(string[] args)
    {
        if (args.Length != 3)
        {
            Console.Error.WriteLine($"Usage: {skin_command} <path-to-client.realm> <skin-archive-name>");
            return 2;
        }

        string archiveName = args[2].Trim();
        if (archiveName.Length is < 1 or > 255 ||
            archiveName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 ||
            archiveName.Contains(Path.DirectorySeparatorChar) ||
            archiveName.Contains(Path.AltDirectorySeparatorChar))
        {
            Console.Error.WriteLine("Skin archive name is invalid.");
            return 2;
        }

        try
        {
            using var realm = openReadOnly(args[1]);
            if (realm == null)
            {
                writeSkinResult(null);
                return 0;
            }

            var skin = realm.DynamicApi
                            .All("Skin")
                            .Filter(
                                "DeletePending == false AND (Name ==[c] $0 OR Name ENDSWITH[c] $1)",
                                archiveName,
                                $" [{archiveName}]")
                            .FirstOrDefault();

            writeSkinResult(skin?.DynamicApi.Get<Guid>("ID"));
            return 0;
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"Read-only Realm lookup failed: {e.Message}");
            return 3;
        }
    }

    private static Realm openReadOnly(string path)
    {
        string realmPath = Path.GetFullPath(path);
        if (!Path.GetExtension(realmPath).Equals(".realm", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Realm lookup refused a file without the .realm extension.");

        // A missing database is the expected state before the first render.
        if (!File.Exists(realmPath))
            return null;

        var configuration = new RealmConfiguration(realmPath)
        {
            // Dynamic mode consumes the schema stored in the file. In particular, this
            // avoids supplying a schema version or migration callback.
            IsDynamic = true,
            // Realm enforces this at the storage layer and rejects write transactions.
            IsReadOnly = true,
        };

        return Realm.GetInstance(configuration);
    }

    private static void writeBeatmapResult(bool found) =>
        Console.WriteLine($"{beatmap_result_prefix}{found.ToString().ToLowerInvariant()}");

    private static void writeSkinResult(Guid? skinId) =>
        Console.WriteLine($"{skin_result_prefix}{skinId?.ToString("D") ?? string.Empty}");
}

using System;
using System.Globalization;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using osu_replay_renderer_netcore.CLI;

namespace osu_replay_renderer_netcore.Online;

internal static class OsuApiCommands
{
    public static OsuApiClient CreateClient(Config.OsuApiOptionsObject options)
    {
        options ??= new Config.OsuApiOptionsObject();
        int clientId = options.ClientId;
        string configuredId = Environment.GetEnvironmentVariable("OSU_API_CLIENT_ID");
        if (!string.IsNullOrWhiteSpace(configuredId) &&
            (!int.TryParse(configuredId, NumberStyles.None, CultureInfo.InvariantCulture, out clientId) || clientId <= 0))
            throw error("OSU_API_CLIENT_ID must be a positive integer");

        string secret = Environment.GetEnvironmentVariable("OSU_API_CLIENT_SECRET") ?? options.ClientSecret;
        if (clientId <= 0 || string.IsNullOrWhiteSpace(secret))
            throw error("Set osu_api.client_id and osu_api.client_secret in --config, or OSU_API_CLIENT_ID and OSU_API_CLIENT_SECRET");

        return new OsuApiClient(clientId, secret);
    }

    public static string DownloadReplay(Config config, string scoreId, string outputPath, bool overwrite)
    {
        if (!long.TryParse(scoreId, NumberStyles.None, CultureInfo.InvariantCulture, out long id) || id <= 0)
            throw error("Score ID must be a positive integer from the API v2 leaderboard's id field");

        try
        {
            string path = Path.GetFullPath(outputPath ?? id.ToString(CultureInfo.InvariantCulture) + ".osr");
            using var client = CreateClient(config.OsuApi);
            client.DownloadReplayAsync(id, path, overwrite).GetAwaiter().GetResult();
            return path;
        }
        catch (Exception e) when (e is HttpRequestException or IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException or JsonException or OperationCanceledException)
        {
            throw error(e.Message);
        }
    }

    private static CLIException error(string message) => new() { Cause = "osu! API v2", DisplayMessage = message };
}

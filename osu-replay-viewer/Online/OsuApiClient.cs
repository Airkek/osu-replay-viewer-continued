using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace osu_replay_renderer_netcore.Online;

public sealed class OsuApiClient : IDisposable
{
    private const string api_root = "https://osu.ppy.sh/api/v2/";
    private const string token_url = "https://osu.ppy.sh/oauth/token";
    private readonly int clientId;
    private readonly string clientSecret;
    private readonly HttpClient httpClient;
    private readonly bool ownsHttpClient;
    private readonly SemaphoreSlim tokenLock = new(1, 1);
    private string accessToken;
    private DateTimeOffset tokenExpiresAt;
    private bool disposed;

    public OsuApiClient(int clientId, string clientSecret, HttpClient httpClient = null)
    {
        if (clientId <= 0)
            throw new ArgumentOutOfRangeException(nameof(clientId), "osu! API client_id must be positive");
        if (string.IsNullOrWhiteSpace(clientSecret))
            throw new ArgumentException("osu! API client_secret is required", nameof(clientSecret));

        this.clientId = clientId;
        this.clientSecret = clientSecret;
        ownsHttpClient = httpClient == null;
        this.httpClient = httpClient ?? new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    public async Task<JsonDocument> GetLeaderboardAsync(int beatmapId, string ruleset = "osu", int limit = 50,
        bool legacyOnly = false, string[] mods = null, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (beatmapId <= 0)
            throw new ArgumentOutOfRangeException(nameof(beatmapId), "Beatmap ID must be positive");
        if (ruleset is not ("osu" or "taiko" or "fruits" or "mania"))
            throw new ArgumentException("Ruleset must be osu, taiko, fruits, or mania", nameof(ruleset));
        if (limit < 1 || limit > 50)
            throw new ArgumentOutOfRangeException(nameof(limit), "Leaderboard limit must be between 1 and 50");
        if (mods != null && mods.Any(mod => string.IsNullOrEmpty(mod) || !mod.All(char.IsAsciiLetterOrDigit)))
            throw new ArgumentException("Mods must contain alphanumeric acronyms such as HD, DT, or 4K", nameof(mods));

        string target = $"beatmaps/{beatmapId.ToString(CultureInfo.InvariantCulture)}/scores?type=global&mode={ruleset}&limit={limit.ToString(CultureInfo.InvariantCulture)}&legacy_only={(legacyOnly ? 1 : 0)}";
        if (mods != null)
            foreach (string mod in mods)
                target += "&mods%5B%5D=" + Uri.EscapeDataString(mod.ToUpperInvariant());

        using var request = await createApiRequestAsync(target, cancellationToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        ensureSuccess(response, "Leaderboard request");
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("osu! API returned an invalid leaderboard response");
        }

        if (document.RootElement.ValueKind != JsonValueKind.Object ||
            !document.RootElement.TryGetProperty("scores", out var scores) || scores.ValueKind != JsonValueKind.Array)
        {
            document.Dispose();
            throw new InvalidOperationException("osu! API returned a leaderboard without a scores array");
        }
        return document;
    }

    public async Task<JsonDocument> GetUserAsync(int userId, string username, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        bool byId = userId > 1;
        if (!byId && string.IsNullOrWhiteSpace(username))
            throw new ArgumentException("A user ID or username is required", nameof(username));

        string lookup = byId ? userId.ToString(CultureInfo.InvariantCulture) : Uri.EscapeDataString(username);
        using var request = await createApiRequestAsync($"users/{lookup}?key={(byId ? "id" : "username")}", cancellationToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        ensureSuccess(response, "Player profile request");
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        JsonDocument document;
        try
        {
            document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        }
        catch (JsonException)
        {
            throw new InvalidOperationException("osu! API returned an invalid player profile");
        }

        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("id", out var id) || id.ValueKind != JsonValueKind.Number || !id.TryGetInt32(out int resolvedId) || resolvedId <= 1 ||
            (byId && resolvedId != userId) ||
            !root.TryGetProperty("username", out var name) || name.ValueKind != JsonValueKind.String ||
            string.IsNullOrWhiteSpace(name.GetString()))
        {
            document.Dispose();
            throw new InvalidOperationException("osu! API returned an invalid player profile");
        }
        return document;
    }

    public async Task DownloadReplayAsync(long scoreId, string outputPath, bool overwrite = false,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (scoreId <= 0)
            throw new ArgumentOutOfRangeException(nameof(scoreId), "Score ID must be positive");
        if (string.IsNullOrWhiteSpace(outputPath))
            throw new ArgumentException("Replay output path is required", nameof(outputPath));

        string destination = Path.GetFullPath(outputPath);
        if (Directory.Exists(destination))
            throw new IOException("Replay output path is a directory");
        if (!overwrite && File.Exists(destination))
            throw new IOException("Replay output already exists; choose another path or allow overwriting");
        string directory = Path.GetDirectoryName(destination);
        if (!Directory.Exists(directory))
            throw new DirectoryNotFoundException("Replay output directory does not exist");

        using var request = await createApiRequestAsync($"scores/{scoreId.ToString(CultureInfo.InvariantCulture)}/download", cancellationToken);
        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        ensureSuccess(response, "Replay download");
        string mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType != null && (mediaType.StartsWith("text/", StringComparison.OrdinalIgnoreCase) ||
                                 mediaType.Contains("json", StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("osu! API returned an error document instead of a replay");

        string temporary = Path.Combine(directory, ".orv-replay-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var input = await response.Content.ReadAsStreamAsync(cancellationToken))
            await using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
                             81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
            {
                await input.CopyToAsync(output, cancellationToken);
                if (output.Length == 0)
                    throw new InvalidOperationException("osu! API returned an empty replay");
                output.Position = 0;
                if (output.ReadByte() > 3)
                    throw new InvalidOperationException("osu! API returned an invalid replay");
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private async Task<HttpRequestMessage> createApiRequestAsync(string target, CancellationToken cancellationToken)
    {
        string token = await getAccessTokenAsync(cancellationToken);
        var request = new HttpRequestMessage(HttpMethod.Get, api_root + target);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        // This response version uses score IDs accepted by scores/{id}/download
        request.Headers.Add("x-api-version", "20220705");
        request.Headers.UserAgent.ParseAdd("osu-replay-viewer-continued");
        return request;
    }

    private async Task<string> getAccessTokenAsync(CancellationToken cancellationToken)
    {
        await tokenLock.WaitAsync(cancellationToken);
        try
        {
            if (accessToken != null && DateTimeOffset.UtcNow < tokenExpiresAt)
                return accessToken;

            using var request = new HttpRequestMessage(HttpMethod.Post, token_url)
            {
                Content = new FormUrlEncodedContent(new Dictionary<string, string>
                {
                    ["client_id"] = clientId.ToString(CultureInfo.InvariantCulture),
                    ["client_secret"] = clientSecret,
                    ["grant_type"] = "client_credentials",
                    ["scope"] = "public"
                })
            };
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("osu-replay-viewer-continued");
            using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            ensureSuccess(response, "Authentication");
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            try
            {
                using var token = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
                var root = token.RootElement;
                if (root.ValueKind != JsonValueKind.Object ||
                    !root.TryGetProperty("access_token", out var access) || access.ValueKind != JsonValueKind.String ||
                    string.IsNullOrWhiteSpace(access.GetString()) ||
                    !access.GetString().All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.' or '_' or '~' or '+' or '/' or '=') ||
                    !root.TryGetProperty("token_type", out var type) || type.ValueKind != JsonValueKind.String ||
                    !string.Equals(type.GetString(), "Bearer", StringComparison.OrdinalIgnoreCase) ||
                    !root.TryGetProperty("expires_in", out var expiry) || expiry.ValueKind != JsonValueKind.Number ||
                    !expiry.TryGetInt32(out int seconds) || seconds <= 0)
                    throw new InvalidOperationException("osu! API returned an invalid authentication response");

                accessToken = access.GetString();
                tokenExpiresAt = DateTimeOffset.UtcNow.AddSeconds(seconds - Math.Min(60, seconds / 10));
                return accessToken;
            }
            catch (JsonException)
            {
                throw new InvalidOperationException("osu! API returned an invalid authentication response");
            }
        }
        finally
        {
            tokenLock.Release();
        }
    }

    private static void ensureSuccess(HttpResponseMessage response, string operation)
    {
        if (response.IsSuccessStatusCode)
            return;
        string reason = response.StatusCode switch
        {
            HttpStatusCode.Unauthorized => "Check the osu! API client_id and client_secret",
            HttpStatusCode.Forbidden => "The API denied access to this resource",
            HttpStatusCode.NotFound => "The resource or replay is unavailable",
            HttpStatusCode.TooManyRequests => "The API rate limit was reached; try again later",
            _ => "The API request failed"
        };
        throw new InvalidOperationException($"{operation} failed (HTTP {(int)response.StatusCode}). {reason}");
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        accessToken = null;
        if (ownsHttpClient)
            httpClient.Dispose();
        tokenLock.Dispose();
    }
}

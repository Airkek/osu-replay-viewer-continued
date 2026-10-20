using System.Net;
using System.Text;
using osu_replay_renderer_netcore.Online;

const string secret = "client+&= secret";
const string token = "private-api-token";
byte[] replay = [0, 0x7c, 0x6a, 0x34, 1, 0, 0, 0, 0, 0x7f, 0x55];
int checks = 0;

await Check("OAuth public grant, modern leaderboard IDs and query parameters", async () =>
{
    using var handler = new ScriptedHandler();
    handler.Add(async (request, _) =>
    {
        Assert(request.Method == HttpMethod.Post && request.RequestUri!.AbsoluteUri == "https://osu.ppy.sh/oauth/token", "OAuth endpoint");
        var form = Query(await request.Content!.ReadAsStringAsync());
        Assert(Value(form, "client_id") == "17" && Value(form, "client_secret") == secret, "form-encoded credentials");
        Assert(Value(form, "grant_type") == "client_credentials" && Value(form, "scope") == "public", "guest OAuth grant");
        Assert(request.Headers.Authorization == null, "OAuth must not send a previous bearer token");
        return Token();
    });
    handler.Add((request, _) =>
    {
        Assert(request.Method == HttpMethod.Get && request.RequestUri!.AbsolutePath == "/api/v2/beatmaps/123/scores", "leaderboard endpoint");
        var query = Query(request.RequestUri.Query);
        Assert(Value(query, "type") == "global" && Value(query, "mode") == "mania", "public ruleset leaderboard");
        Assert(Value(query, "limit") == "12" && Value(query, "legacy_only") == "1", "limit and legacy-only selection");
        Assert(query.Where(p => p.Key == "mods[]").Select(p => p.Value).SequenceEqual(new[] { "HD", "DT" }), "mods array");
        Assert(request.Headers.GetValues("x-api-version").Single() == "20220705", "modern score IDs version");
        Bearer(request);
        Assert(!request.RequestUri.AbsoluteUri.Contains(secret) && !request.RequestUri.AbsoluteUri.Contains(token), "credentials stay out of URLs");
        return Task.FromResult(Json("{\"scores\":[{\"id\":9007199254740993,\"has_replay\":true}]}"));
    });
    using var http = new HttpClient(handler);
    using var api = new OsuApiClient(17, secret, http);
    using var result = await api.GetLeaderboardAsync(123, "mania", 12, true, ["HD", "DT"]);
    Assert(result.RootElement.GetProperty("scores")[0].GetProperty("id").GetInt64() == 9007199254740993, "score ID lost integer precision");
    handler.Complete();
});

foreach (var lookup in new[] { (Id: 42, Name: "old-name"), (Id: 1, Name: "player +&#?"), (Id: 1, Name: "12345") })
{
    await Check("player profile lookup uses an ID or an escaped explicit username", async () =>
    {
        using var handler = new ScriptedHandler();
        handler.Add((_, _) => Task.FromResult(Token()));
        handler.Add((request, _) =>
        {
            Bearer(request);
            bool byId = lookup.Id > 1;
            Assert(Uri.UnescapeDataString(request.RequestUri!.AbsolutePath) == "/api/v2/users/" + (byId ? "42" : lookup.Name), "profile lookup changed the player identity");
            Assert(Value(Query(request.RequestUri.Query), "key") == (byId ? "id" : "username"), "ambiguous player lookup");
            return Task.FromResult(Json("{\"id\":42,\"username\":\"current-name\",\"avatar_url\":\"https://a.ppy.sh/42?updated\"}"));
        });
        using var http = new HttpClient(handler);
        using var api = new OsuApiClient(17, secret, http);
        using var profile = await api.GetUserAsync(lookup.Id, lookup.Name);
        Assert(profile.RootElement.GetProperty("avatar_url").GetString() == "https://a.ppy.sh/42?updated", "profile avatar URL lost");
        handler.Complete();
    });
}

foreach (string body in new[] { "not JSON", "null", "{}", "{\"id\":\"42\",\"username\":\"player\"}", "{\"id\":1,\"username\":\"player\"}", "{\"id\":43,\"username\":\"player\"}", "{\"id\":42,\"username\":\"\"}" })
{
    await Check("invalid or mismatched player profile is rejected", async () =>
    {
        using var handler = new ScriptedHandler();
        handler.Add((_, _) => Task.FromResult(Token()));
        handler.Add((_, _) => Task.FromResult(Json(body)));
        using var http = new HttpClient(handler);
        using var api = new OsuApiClient(17, secret, http);
        Redacted(await ThrowsAsync<InvalidOperationException>(async () => { using var result = await api.GetUserAsync(42, "player"); }));
        handler.Complete();
    });
}

await Check("one client token serves leaderboard and replay requests", async () =>
{
    using var directory = new TestDirectory();
    using var handler = new ScriptedHandler();
    handler.Add((_, _) => Task.FromResult(Token()));
    handler.Add((request, _) => { Bearer(request); return Task.FromResult(Json("{\"scores\":[]}")); });
    handler.Add((request, _) =>
    {
        Bearer(request);
        Assert(request.RequestUri!.AbsolutePath == "/api/v2/scores/9007199254740993/download", "modern score replay endpoint");
        return Task.FromResult(Binary(replay));
    });
    using var http = new HttpClient(handler);
    using var api = new OsuApiClient(17, secret, http);
    using var leaderboard = await api.GetLeaderboardAsync(123);
    string path = Path.Combine(directory.Path, "score.osr");
    await api.DownloadReplayAsync(9007199254740993, path);
    Assert(File.ReadAllBytes(path).SequenceEqual(replay), "replay bytes changed");
    Assert(Directory.GetFiles(directory.Path).Length == 1, "successful download left temporary files");
    handler.Complete();
});

await Check("expired token is renewed before the next API request", async () =>
{
    using var handler = new ScriptedHandler();
    handler.Add((_, _) => Task.FromResult(Token(expires: 1)));
    handler.Add((_, _) => Task.FromResult(Json("{\"scores\":[]}")));
    handler.Add((_, _) => Task.FromResult(Token("renewed-token")));
    handler.Add((request, _) =>
    {
        Assert(request.Headers.Authorization?.Parameter == "renewed-token", "expired token reused");
        return Task.FromResult(Json("{\"scores\":[]}"));
    });
    using var http = new HttpClient(handler);
    using var api = new OsuApiClient(17, secret, http);
    using var first = await api.GetLeaderboardAsync(123);
    await Task.Delay(1100);
    using var second = await api.GetLeaderboardAsync(123);
    handler.Complete();
});

foreach (HttpStatusCode status in new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden, HttpStatusCode.NotFound, HttpStatusCode.TooManyRequests })
{
    await Check($"HTTP {(int)status} produces a useful redacted API error", async () =>
    {
        using var handler = new ScriptedHandler();
        handler.Add((_, _) => Task.FromResult(Token()));
        handler.Add((_, _) => Task.FromResult(Json($"{{\"error\":\"{secret} {token}\"}}", status)));
        using var http = new HttpClient(handler);
        using var api = new OsuApiClient(17, secret, http);
        var error = await ThrowsAsync<InvalidOperationException>(async () => { using var result = await api.GetLeaderboardAsync(123); });
        Assert(error.Message.Contains(((int)status).ToString()), "error omits HTTP status");
        Redacted(error);
        handler.Complete();
    });
}

await Check("OAuth rejection does not expose the secret or response body", async () =>
{
    using var handler = new ScriptedHandler();
    handler.Add((_, _) => Task.FromResult(Json($"{{\"error\":\"{secret} {token}\"}}", HttpStatusCode.Unauthorized)));
    using var http = new HttpClient(handler);
    using var api = new OsuApiClient(17, secret, http);
    Redacted(await ThrowsAsync<InvalidOperationException>(async () => { using var result = await api.GetLeaderboardAsync(123); }));
    handler.Complete();
});

foreach (string body in new[] { "not JSON", "{}", "{\"access_token\":\"private-api-token\",\"token_type\":\"Bearer\",\"expires_in\":0}" })
{
    await Check("malformed OAuth response is rejected without credentials", async () =>
    {
        using var handler = new ScriptedHandler();
        handler.Add((_, _) => Task.FromResult(Json(body)));
        using var http = new HttpClient(handler);
        using var api = new OsuApiClient(17, secret, http);
        Redacted(await ThrowsAsync<InvalidOperationException>(async () => { using var result = await api.GetLeaderboardAsync(123); }));
        handler.Complete();
    });
}

foreach (string body in new[] { "not JSON", "{}", "{\"scores\":null}", "{\"scores\":{}}" })
{
    await Check("malformed leaderboard is rejected", async () =>
    {
        using var handler = new ScriptedHandler();
        handler.Add((_, _) => Task.FromResult(Token()));
        handler.Add((_, _) => Task.FromResult(Json(body)));
        using var http = new HttpClient(handler);
        using var api = new OsuApiClient(17, secret, http);
        Redacted(await ThrowsAsync<InvalidOperationException>(async () => { using var result = await api.GetLeaderboardAsync(123); }));
        handler.Complete();
    });
}

await Check("invalid inputs perform no HTTP requests", async () =>
{
    using var handler = new ScriptedHandler();
    using var http = new HttpClient(handler);
    Throws<ArgumentException>(() => new OsuApiClient(0, secret, http));
    Throws<ArgumentException>(() => new OsuApiClient(17, " ", http));
    using var api = new OsuApiClient(17, secret, http);
    await ThrowsAsync<ArgumentException>(async () => { using var result = await api.GetLeaderboardAsync(0); });
    await ThrowsAsync<ArgumentException>(async () => { using var result = await api.GetLeaderboardAsync(123, "unsupported"); });
    await ThrowsAsync<ArgumentException>(async () => { using var result = await api.GetLeaderboardAsync(123, limit: 0); });
    await ThrowsAsync<ArgumentException>(async () => { using var result = await api.GetLeaderboardAsync(123, limit: 51); });
    await ThrowsAsync<ArgumentException>(async () => { using var result = await api.GetLeaderboardAsync(123, mods: ["HD&limit=999"]); });
    await ThrowsAsync<ArgumentException>(() => api.DownloadReplayAsync(0, "unused.osr"));
    await ThrowsAsync<ArgumentException>(async () => { using var result = await api.GetUserAsync(1, " "); });
    handler.Complete();
});

await Check("existing replay is preserved without overwrite and replaced only on success", async () =>
{
    using var directory = new TestDirectory();
    string path = Path.Combine(directory.Path, "score.osr");
    byte[] original = [0, 1, 2, 3];
    File.WriteAllBytes(path, original);
    using var handler = new ScriptedHandler();
    using var http = new HttpClient(handler);
    using var api = new OsuApiClient(17, secret, http);
    await ThrowsAsync<IOException>(() => api.DownloadReplayAsync(42, path));
    Assert(File.ReadAllBytes(path).SequenceEqual(original), "existing replay changed before overwrite consent");
    handler.Add((_, _) => Task.FromResult(Token()));
    handler.Add((_, _) => Task.FromResult(Binary(replay)));
    await api.DownloadReplayAsync(42, path, overwrite: true);
    Assert(File.ReadAllBytes(path).SequenceEqual(replay), "explicit overwrite did not replace replay");
    Assert(Directory.GetFiles(directory.Path).Length == 1, "overwrite left temporary file");
    handler.Complete();
});

foreach (string failure in new[] { "http", "stream", "cancel", "empty", "json", "html" })
{
    await Check($"{failure} replay failure preserves existing output and removes partial files", async () =>
    {
        using var directory = new TestDirectory();
        string path = Path.Combine(directory.Path, "score.osr");
        byte[] original = [0, 1, 2, 3];
        File.WriteAllBytes(path, original);
        using var cancellation = new CancellationTokenSource();
        using var handler = new ScriptedHandler();
        handler.Add((_, _) => Task.FromResult(Token()));
        handler.Add((_, _) => Task.FromResult(failure switch
        {
            "http" => Json("{}", HttpStatusCode.NotFound),
            "stream" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new InterruptedStream(replay)) },
            "cancel" => new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new InterruptedStream(replay, cancellation)) },
            "empty" => Binary([]),
            "json" => Json("{\"error\":\"no replay\"}"),
            "html" => Binary(Encoding.UTF8.GetBytes("<!doctype html>not a replay")),
            _ => throw new Exception("unknown fixture")
        }));
        using var http = new HttpClient(handler);
        using var api = new OsuApiClient(17, secret, http);
        Exception error = await ThrowsAsync<Exception>(() => api.DownloadReplayAsync(42, path, overwrite: true, cancellation.Token));
        if (failure == "cancel") Assert(error is OperationCanceledException, "cancellation was not propagated");
        Assert(File.ReadAllBytes(path).SequenceEqual(original), "failed download damaged existing output");
        Assert(Directory.GetFiles(directory.Path).SequenceEqual(new[] { path }), "failed download left partial file");
        handler.Complete();
    });
}

await Check("failed new replay download never creates a final output", async () =>
{
    using var directory = new TestDirectory();
    string path = Path.Combine(directory.Path, "new.osr");
    using var handler = new ScriptedHandler();
    handler.Add((_, _) => Task.FromResult(Token()));
    handler.Add((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new InterruptedStream(replay)) }));
    using var http = new HttpClient(handler);
    using var api = new OsuApiClient(17, secret, http);
    await ThrowsAsync<Exception>(() => api.DownloadReplayAsync(42, path));
    Assert(!File.Exists(path) && Directory.GetFiles(directory.Path).Length == 0, "partial new output retained");
    handler.Complete();
});

await Check("disposing API client keeps an injected HttpClient usable", async () =>
{
    using var handler = new ScriptedHandler();
    handler.Add((_, _) => Task.FromResult(Json("{}")));
    using var http = new HttpClient(handler);
    new OsuApiClient(17, secret, http).Dispose();
    using var response = await http.GetAsync("https://osu.ppy.sh/test-injected-client");
    handler.Complete();
});

Console.WriteLine($"{checks} checks passed");

async Task Check(string name, Func<Task> test)
{
    await test();
    checks++;
    Console.WriteLine($"PASS {name}");
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new Exception(message);
}

static T Throws<T>(Action action) where T : Exception
{
    try { action(); }
    catch (T e) { return e; }
    throw new Exception($"Expected {typeof(T).Name}");
}

static async Task<T> ThrowsAsync<T>(Func<Task> action) where T : Exception
{
    try { await action(); }
    catch (T e) { return e; }
    throw new Exception($"Expected {typeof(T).Name}");
}

static List<KeyValuePair<string, string>> Query(string value) => value.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
    .Select(part => part.Split('=', 2))
    .Select(pair => new KeyValuePair<string, string>(WebUtility.UrlDecode(pair[0]), WebUtility.UrlDecode(pair.Length > 1 ? pair[1] : string.Empty)))
    .ToList();

static string Value(List<KeyValuePair<string, string>> query, string key) => query.Single(p => p.Key == key).Value;

static void Bearer(HttpRequestMessage request)
{
    Assert(request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == token, "bearer token missing from API request");
}

static void Redacted(Exception error)
{
    Assert(!error.ToString().Contains(secret) && !error.ToString().Contains(token), "credentials leaked in error");
}

static HttpResponseMessage Json(string body, HttpStatusCode status = HttpStatusCode.OK) => new(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
static HttpResponseMessage Token(string value = token, int expires = 3600) => Json($"{{\"access_token\":\"{value}\",\"token_type\":\"Bearer\",\"expires_in\":{expires}}}");
static HttpResponseMessage Binary(byte[] data) => new(HttpStatusCode.OK) { Content = new ByteArrayContent(data) };

sealed class ScriptedHandler : HttpMessageHandler
{
    private readonly Queue<Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>>> steps = new();

    public void Add(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> step) => steps.Enqueue(step);
    public void Complete() => AssertNoPending();

    private void AssertNoPending()
    {
        if (steps.Count != 0) throw new Exception($"{steps.Count} expected HTTP requests not made");
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (steps.Count == 0) throw new Exception($"Unexpected HTTP request: {request.Method} {request.RequestUri!.AbsolutePath}");
        return steps.Dequeue()(request, cancellationToken);
    }
}

sealed class TestDirectory : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "orv-api-check-" + Guid.NewGuid().ToString("N"));
    public TestDirectory() => Directory.CreateDirectory(Path);
    public void Dispose() => Directory.Delete(Path, recursive: true);
}

sealed class InterruptedStream(byte[] bytes, CancellationTokenSource cancellation = null) : Stream
{
    private bool providedBytes;
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (!providedBytes)
        {
            providedBytes = true;
            bytes.CopyTo(buffer);
            return ValueTask.FromResult(bytes.Length);
        }
        if (cancellation != null)
        {
            cancellation.Cancel();
            return ValueTask.FromCanceled<int>(cancellation.Token);
        }
        throw new IOException("Simulated interrupted replay download");
    }
}

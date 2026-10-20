using System.Net;
using System.Text;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Scoring;
using osu_replay_renderer_netcore;
using osu_replay_renderer_netcore.Online;

int checks = 0;

await Check("profile resolution completes while the game thread cannot pump continuations", async () =>
{
    var completion = new TaskCompletionSource<ScoreInfo>(TaskCreationOptions.RunContinuationsAsynchronously);
    var context = new BlockedGameContext();
    var thread = new Thread(() =>
    {
        SynchronizationContext.SetSynchronizationContext(context);
        try
        {
            var score = LazerScore();
            using var http = new HttpClient(new ProfileHandler("42", "id"));
            Resolve(score, () => new OsuApiClient(17, "secret", http)).GetAwaiter().GetResult();
            completion.SetResult(score);
        }
        catch (Exception error)
        {
            completion.SetException(error);
        }
    }) { IsBackground = true };
    thread.Start();
    var resolved = await completion.Task.WaitAsync(TimeSpan.FromSeconds(5));
    Assert(context.Posts == 0, "profile request captured the blocked game context");
    Assert(resolved.User.OnlineID == 42 && resolved.User.AvatarUrl == "https://a.ppy.sh/42?updated", "profile not applied");
});

await Check("stable replay resolves its username into the player and avatar used by native UI", async () =>
{
    var score = new ScoreInfo { User = new APIUser { Username = "12345" } };
    using var http = new HttpClient(new ProfileHandler("12345", "username"));
    await Resolve(score, () => new OsuApiClient(17, "secret", http));
    Assert(score.User.OnlineID == 42 && score.UserID == 42, "player ID was not populated");
    Assert(score.User.Username == "current-name" && score.User.AvatarUrl == "https://a.ppy.sh/42?updated", "native UI has no resolved profile");
});

await Check("lazer replay resolves by embedded ID despite its cached username-only player", async () =>
{
    var score = LazerScore();
    Assert(score.User.OnlineID == 1 && score.UserID == 42, "fixture does not reproduce decoder state");
    using var http = new HttpClient(new ProfileHandler("42", "id"));
    await Resolve(score, () => new OsuApiClient(17, "secret", http));
    Assert(score.User.OnlineID == 42 && score.User.AvatarUrl == "https://a.ppy.sh/42?updated", "lazer profile not resolved");
});

await Check("missing credentials still restores the lazer ID for the public avatar fallback", async () =>
{
    var score = LazerScore();
    await Fails(() => Resolve(score, () => throw new InvalidOperationException("No credentials")));
    Assert(score.User.OnlineID == 42 && score.UserID == 42 && score.User.Username == "old-name", "embedded identity lost on credential failure");
});

await Check("unavailable profile preserves stable replay identity", async () =>
{
    var original = new APIUser { Username = "deleted-player" };
    var score = new ScoreInfo { User = original };
    using var http = new HttpClient(new ProfileHandler("deleted-player", "username", HttpStatusCode.NotFound));
    await Fails(() => Resolve(score, () => new OsuApiClient(17, "secret", http)));
    Assert(ReferenceEquals(score.User, original), "failed lookup replaced the replay player");
});

await Check("unavailable profile preserves the repaired lazer identity", async () =>
{
    var score = LazerScore();
    using var http = new HttpClient(new ProfileHandler("42", "id", HttpStatusCode.TooManyRequests));
    await Fails(() => Resolve(score, () => new OsuApiClient(17, "secret", http)));
    Assert(score.User.OnlineID == 42 && score.User.Username == "old-name", "failed lookup discarded the embedded ID");
});

await Check("a replay without a player identity makes no API request", async () =>
{
    var score = new ScoreInfo();
    await Resolve(score, () => throw new Exception("Unexpected API client creation"));
    Assert(score.User.OnlineID <= 1, "invented player identity");
});

Console.WriteLine($"{checks} checks passed");

static Task Resolve(ScoreInfo score, Func<OsuApiClient> createClient)
{
    try
    {
        typeof(GameSettings).Assembly
            .GetType("osu_replay_renderer_netcore.Online.ReplayUserResolver", true)!
            .GetMethod("Resolve")!.Invoke(null, [score, createClient]);
        return Task.CompletedTask;
    }
    catch (System.Reflection.TargetInvocationException error) when (error.InnerException != null)
    {
        return Task.FromException(error.InnerException);
    }
}

static ScoreInfo LazerScore()
{
    var score = new ScoreInfo { User = new APIUser { Username = "old-name" } };
    score.RealmUser.OnlineID = 42;
    return score;
}

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

static async Task Fails(Func<Task> action)
{
    try { await action(); }
    catch (InvalidOperationException) { return; }
    throw new Exception("Expected profile resolution failure");
}

sealed class ProfileHandler(string lookup, string key, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await Task.Delay(20, cancellationToken).ConfigureAwait(false);
        string body;
        if (request.RequestUri!.AbsolutePath == "/oauth/token")
            body = "{\"access_token\":\"token\",\"token_type\":\"Bearer\",\"expires_in\":3600}";
        else
        {
            if (request.RequestUri.AbsolutePath != "/api/v2/users/" + lookup || request.RequestUri.Query != "?key=" + key)
                throw new Exception("Wrong profile requested: " + request.RequestUri);
            if (request.Headers.Authorization?.Parameter != "token")
                throw new Exception("Missing OAuth token");
            body = "{\"id\":42,\"username\":\"current-name\",\"avatar_url\":\"https://a.ppy.sh/42?updated\"}";
            if (status != HttpStatusCode.OK)
                return new HttpResponseMessage(status);
        }
        return new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "application/json")
        };
    }
}

sealed class BlockedGameContext : SynchronizationContext
{
    private int posts;
    public int Posts => Volatile.Read(ref posts);
    public override void Post(SendOrPostCallback callback, object state) => Interlocked.Increment(ref posts);
}

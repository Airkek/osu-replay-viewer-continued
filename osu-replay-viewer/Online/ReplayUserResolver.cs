using System;
using System.Threading.Tasks;
using Newtonsoft.Json;
using osu.Game.Online.API.Requests.Responses;
using osu.Game.Scoring;

namespace osu_replay_renderer_netcore.Online;

internal static class ReplayUserResolver
{
    public static async Task ResolveAsync(ScoreInfo score, Func<OsuApiClient> createClient)
    {
        var user = score.User;
        // The lazer replay decoder updates RealmUser after caching a username-only User
        if (score.UserID > 1 && user.OnlineID != score.UserID)
        {
            user = new APIUser
            {
                Id = score.UserID,
                Username = user.Username,
                CountryCode = user.CountryCode,
            };
            score.User = user;
        }

        if (user.OnlineID <= 1 && string.IsNullOrWhiteSpace(user.Username))
            return;

        using var client = createClient();
        using var response = await client.GetUserAsync(user.OnlineID, user.Username);
        score.User = JsonConvert.DeserializeObject<APIUser>(response.RootElement.GetRawText());
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using osu.Game.Beatmaps;
using osu.Game.Rulesets;
using osu.Game.Rulesets.Mods;
using osu.Game.Scoring.Legacy;

namespace osu_replay_renderer_netcore
{
    internal static class ReplayCompatibility
    {
        public static Ruleset CreateRuleset(IRulesetInfo info)
        {
            if (info == null)
                throw new RulesetLoadException("Replay does not specify a ruleset");

            try
            {
                return info.CreateInstance();
            }
            catch (RulesetLoadException error)
            {
                throw new RulesetLoadException($"Unsupported ruleset '{info.ShortName}' (ID {info.OnlineID}): {error.Message}");
            }
        }

        public static void ValidateMods(IEnumerable<Mod> mods)
        {
            var unknownMods = mods.OfType<UnknownMod>().Select(mod => mod.OriginalAcronym).Distinct().ToArray();
            if (unknownMods.Length > 0)
                throw new NotSupportedException($"Unsupported replay mod(s): {string.Join(", ", unknownMods)}. Update the viewer to a version that supports these mods");
        }
    }

    internal class RecorderScoreDecoder : DatabasedLegacyScoreDecoder
    {
        private readonly IRulesetStore rulesets;

        public RecorderScoreDecoder(IRulesetStore rulesets, BeatmapManager beatmaps) : base(rulesets, beatmaps)
        {
            this.rulesets = rulesets;
        }

        protected override Ruleset GetRuleset(int rulesetId)
        {
            var info = rulesets.GetRuleset(rulesetId);
            if (info == null)
                throw new RulesetLoadException($"Unknown replay ruleset ID {rulesetId}");

            return ReplayCompatibility.CreateRuleset(info);
        }
    }
}

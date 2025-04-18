﻿using HarmonyLib;
using osu.Framework.Audio.Sample;
using osu.Framework.Audio.Track;
using osu.Framework.Graphics.Audio;
using osu.Game.Skinning;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace osu_replay_renderer_netcore.Patching
{
    public class AudioPatcher : PatcherBase
    {
        public override string PatcherId() => "osureplayrenderer.Audio";
        
        public static event Action<ISample> OnSamplePlay;
        public static event Action<ITrack> OnTrackPlay;

        private static void TriggerOnSamplePlay(ISample sample) => OnSamplePlay?.Invoke(sample);
        private static void TriggerOnTrackPlay(ITrack track) => OnTrackPlay?.Invoke(track);

        
        [HarmonyPatch(typeof(PoolableSkinnableSample))]
        [HarmonyPatch("Play")]
        class PoolableSkinnableSamplePatch
        {
            static bool Prefix(PoolableSkinnableSample __instance)
            {
                TriggerOnSamplePlay(__instance.Sample);
                return false;
            }
        }
        
        [HarmonyPatch(typeof(Sample))]
        [HarmonyPatch("Play")]
        class SamplePatch
        {
            static bool Prefix(Sample __instance)
            {
                TriggerOnSamplePlay(__instance);
                return false;
            }
        }

        [HarmonyPatch(typeof(TrackBass))]
        [HarmonyPatch("Start")]
        class TrackBassPatch
        {
            static bool Prefix(TrackBass __instance)
            {
                TriggerOnTrackPlay(__instance);
                return false;
            }
        }
    }
}

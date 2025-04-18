using HarmonyLib;
using System;
using System.Diagnostics;
using osu.Framework.Timing;

namespace osu_replay_renderer_netcore.Patching
{
    public class ClockPatcher : PatcherBase
    {
        public override string PatcherId() => "osureplayrenderer.Clock";

        public static event Action<ISourceChangeableClock> OnStopwatchClockSetAsSource;
        private static void TriggerOnStopwatchClockSetAsSource(ISourceChangeableClock baseClock) => OnStopwatchClockSetAsSource?.Invoke(baseClock);

        [HarmonyPatch(typeof(FramedClock))]
        [HarmonyPatch("ChangeSource")]
        class PatchFramedClock
        {
            static void Postfix(FramedClock __instance)
            {
                if (__instance.Source is Stopwatch or null)
                {
                    TriggerOnStopwatchClockSetAsSource(__instance);
                }
            }
        }
    
        [HarmonyPatch(typeof(InterpolatingFramedClock))]
        [HarmonyPatch("ChangeSource")]
        class PatchInterpolatingFramedClock
        {
            static void Postfix(InterpolatingFramedClock __instance)
            {
                if (__instance.Source is Stopwatch or null)
                {
                    TriggerOnStopwatchClockSetAsSource(__instance);
                }
            }
        }
    
    
        [HarmonyPatch(typeof(DecouplingFramedClock))]
        [HarmonyPatch("ChangeSource")]
        class PatchDecouplingFramedClock1
        {
            static void Postfix(DecouplingFramedClock __instance)
            {
                if (__instance.Source is Stopwatch or null)
                {
                    TriggerOnStopwatchClockSetAsSource(__instance);
                }
            }
        }
        
        [HarmonyPatch(typeof(DecouplingFramedClock))]
        [HarmonyPatch("set_AllowDecoupling")]
        class PatchDecouplingFramedClock2
        {
            static void Postfix(DecouplingFramedClock __instance)
            {
                if (__instance.AllowDecoupling)
                {
                    __instance.AllowDecoupling = false;
                }
            }
        }
    }
}
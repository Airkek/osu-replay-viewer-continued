using HarmonyLib;
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
using osu_replay_renderer_netcore.CustomHosts.CustomClocks;
using osu.Framework.Graphics.Primitives;
using osu.Framework.Graphics.Rendering;
using osu.Framework.Timing;
using osu.Game.Rulesets.Osu.Skinning.Legacy;
using osu.Game.Rulesets.Osu.UI.Cursor;

namespace osu_replay_renderer_netcore.Patching
{
    public class RenderPatcher : PatcherBase
    {
        public override string PatcherId() => "osureplayrenderer.Render";

        public override void DoPatching()
        {
            base.DoPatching();
            
            var veldridDeviceType = typeof(IRenderer).Assembly.GetType("osu.Framework.Graphics.Veldrid.VeldridDevice");
            
            var swapBuffersMethod = veldridDeviceType.GetMethod("SwapBuffers");
            Harmony.Patch(swapBuffersMethod,
                GetType().GetMethod(nameof(VeldridSwapchainPrefix),
                    BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));
            
            
            // var resizeMethod = veldridDeviceType.GetMethod("Resize");
            // Harmony.Patch(resizeMethod,
            //     GetType().GetMethod(nameof(VeldridResizePrefix),
            //         BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public));
        }

        public static event Action OnDraw;
        private static void TriggerOnDraw() => OnDraw?.Invoke();

        [HarmonyPatch(typeof(Renderer))]
        [HarmonyPatch("FinishFrame")]
        class PatchFramedClock
        {
            static void Prefix(Renderer __instance)
            {
                TriggerOnDraw();
            }
        }

        static bool VeldridSwapchainPrefix(object __instance)
        {
            return false;
        }
        
        static bool VeldridResizePrefix(object __instance)
        {
            return false;
        }
    }
}
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
using osu.Framework.Bindables;
using osu.Framework.Platform;

namespace osu_replay_renderer_netcore.Patching
{
    public class WindowPatcher : PatcherBase
    {
        public override string PatcherId() => "osureplayrenderer.Window";

        public override void DoPatching()
        {
            base.DoPatching();

            var windows = new[]
            {
                "osu.Framework.Platform.SDL2.SDL2Window",
                "osu.Framework.Platform.SDL3.SDL3Window"
            };
            
            foreach (var window in windows)
            {
                var windowType = typeof(IWindow).Assembly.GetType(window);
                
                var focusedMethod = windowType.GetMethod("get_Focused", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Harmony.Patch(focusedMethod, postfix:(Delegate)SimpleReturnTrue);
                
                var visibleGetMethod = windowType.GetMethod("get_Visible", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Harmony.Patch(visibleGetMethod, postfix:(Delegate)SimpleReturnTrue);
                
                var visibleSetMethod = windowType.GetMethod("set_Visible", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Harmony.Patch(visibleSetMethod, prefix:(Delegate)CallOnlyWithFalse);
                
                var activeMethod = windowType.GetMethod("get_IsActive", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Harmony.Patch(activeMethod, postfix:(Delegate)SimpleReturnBindableTrue);
                
                var raiseMethod = windowType.GetMethod("Raise", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Harmony.Patch(raiseMethod, postfix:(Delegate)CallHide);
                
                var showMethod = windowType.GetMethod("Show", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                Harmony.Patch(showMethod, prefix:(Delegate)OverrideToHide);
            }
        }

        static bool OverrideToHide(IWindow __instance)
        {
            CallHide(__instance);
            return false;
        }
        
        static void CallHide(IWindow __instance)
        {
            __instance.Hide();
        }

        static void CallOnlyWithFalse(ref bool __0)
        {
            __0 = false;
        }

        static void SimpleReturnTrue(ref bool __result)
        {
            __result = true;
        }
        
        static void SimpleReturnBindableTrue(ref IBindable<bool> __result)
        {
            __result = new Bindable<bool>(true);
        }
    }
}

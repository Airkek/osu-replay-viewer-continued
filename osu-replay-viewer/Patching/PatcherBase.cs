using System.Reflection;
using HarmonyLib;

namespace osu_replay_renderer_netcore.Patching;

public abstract class PatcherBase
{
    protected Harmony Harmony;
    
    public virtual void DoPatching()
    {
        Harmony = new Harmony(PatcherId());
        foreach (var type in GetType().GetNestedTypes(BindingFlags.NonPublic))
        {
            var processor = Harmony.CreateClassProcessor(type);
            processor.Patch();
        }
    }

    public abstract string PatcherId();
}
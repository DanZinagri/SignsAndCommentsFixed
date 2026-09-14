using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Verse;

namespace Dark.Signs
{
    // Vanilla skips DrawGUIOverlay for fogged things:
    //     if (viewRect.Contains(thing.Position) && !fogGrid.IsFogged(thing.Position)) thing.DrawGUIOverlay();
    // Comments are placeable in fog, so their labels must still draw. We turn the fog test into
    //     HideForFog(fogGrid.IsFogged(thing.Position), thing)
    // by pushing the thing local and calling our helper right after IsFogged returns; the
    // existing branch then consumes our result instead. Nothing else in the method changes.
    [HarmonyPatch(typeof(ThingOverlays), nameof(ThingOverlays.ThingOverlaysOnGUI))]
    public static class ThingOverlays_ThingOverlaysOnGUI_Patch
    {
        private static readonly MethodInfo IsFogged =
            AccessTools.Method(typeof(FogGrid), nameof(FogGrid.IsFogged), new[] { typeof(IntVec3) });
        private static readonly MethodInfo GetPosition =
            AccessTools.PropertyGetter(typeof(Thing), nameof(Thing.Position));
        private static readonly MethodInfo HideForFogMethod =
            AccessTools.Method(typeof(ThingOverlays_ThingOverlaysOnGUI_Patch), nameof(HideForFog));

        private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions)
        {
            var codes = new List<CodeInstruction>(instructions);
            for (int i = 2; i < codes.Count; i++)
            {
                // pattern: <ldloc thing> ; call Thing.get_Position ; callvirt FogGrid.IsFogged
                if (!codes[i].Calls(IsFogged) || !codes[i - 1].Calls(GetPosition)) continue;
                CodeInstruction loadThing = codes[i - 2];
                if (!loadThing.IsLdloc()) continue;

                codes.InsertRange(i + 1, new[]
                {
                    new CodeInstruction(loadThing.opcode, loadThing.operand),
                    new CodeInstruction(OpCodes.Call, HideForFogMethod)
                });
                return codes;
            }

            Log.Error("(Signs) Could not find the fog check in ThingOverlays.ThingOverlaysOnGUI; comment labels will not draw over fog.");
            return codes;
        }

        public static bool HideForFog(bool fogged, Thing thing)
        {
            return fogged && !Comp_Sign.BuildableCanGoOverFog(thing.def);
        }
    }
}

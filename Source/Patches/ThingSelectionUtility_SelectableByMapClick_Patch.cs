using HarmonyLib;
using RimWorld;
using Verse;

namespace Dark.Signs
{
    // Comments sitting in fog are still clickable.
    [HarmonyPatch(typeof(ThingSelectionUtility), nameof(ThingSelectionUtility.SelectableByMapClick))]
    public static class ThingSelectionUtility_SelectableByMapClick_Patch
    {
        private static void Postfix(ref bool __result, Thing t)
        {
            if (!__result && t != null && Comp_Sign.BuildableCanGoOverFog(t.def))
            {
                __result = true;
            }
        }
    }
}

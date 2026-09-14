using HarmonyLib;
using RimWorld;
using Verse;

namespace Dark.Signs
{
    // Comments may be placed in fog and on top of other things.
    // The def check comes first so every other rejected placement pays one reference compare.
    [HarmonyPatch(typeof(GenConstruct), nameof(GenConstruct.CanPlaceBlueprintAt))]
    public static class GenConstruct_CanPlaceBlueprintAt_Patch
    {
        private static void Postfix(ref AcceptanceReport __result, BuildableDef entDef)
        {
            if (__result.Accepted || entDef == null || !Comp_Sign.BuildableCanGoOverFog(entDef)) return;

            string reason = __result.Reason;
            if (reason == "CannotPlaceInUndiscovered".Translate() || reason == "SpaceAlreadyOccupied".Translate())
            {
                __result = true;
            }
        }
    }
}

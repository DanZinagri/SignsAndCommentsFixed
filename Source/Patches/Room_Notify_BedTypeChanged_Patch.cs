using HarmonyLib;
using Verse;

namespace Dark.Signs
{
    // Bedroom -> prison cell / barracks changes the room role without a region rebuild.
    [HarmonyPatch(typeof(Room), nameof(Room.Notify_BedTypeChanged))]
    public static class Room_Notify_BedTypeChanged_Patch
    {
        private static void Postfix(Room __instance)
        {
            SignUtils.UpdateSignsOnRoomChange(__instance);
        }
    }
}

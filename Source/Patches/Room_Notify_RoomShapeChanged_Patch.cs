using HarmonyLib;
using Verse;

namespace Dark.Signs
{
    // Fires for every room touched by a region rebuild, including the outdoor room.
    [HarmonyPatch(typeof(Room), nameof(Room.Notify_RoomShapeChanged))]
    public static class Room_Notify_RoomShapeChanged_Patch
    {
        private static void Postfix(Room __instance)
        {
            SignUtils.UpdateSignsOnRoomChange(__instance);
        }
    }
}

using System.Collections.Generic;
using Verse;

namespace Dark.Signs
{
    // Thin front for the per-map sign registry (MapComponent_SignLabels). No static state.
    public static class SignUtils
    {
        private static readonly List<Comp_Sign> NoSigns = new List<Comp_Sign>();

        public static MapComponent_SignLabels RegistryOf(Map map)
        {
            return map?.GetComponent<MapComponent_SignLabels>();
        }

        public static void RegisterSign(Comp_Sign sign)
        {
            RegistryOf(sign.parent.Map)?.Register(sign);
        }

        public static void UnregisterSign(Comp_Sign sign, Map map)
        {
            RegistryOf(map)?.Unregister(sign);
        }

        public static void MarkPending(Comp_Sign sign)
        {
            RegistryOf(sign.parent.Map)?.MarkPending(sign);
        }

        public static List<Comp_Sign> SignsOn(Map map)
        {
            return RegistryOf(map)?.Signs ?? NoSigns;
        }

        // Regenerate every sign's world label on every map (settings changed).
        public static void DirtyAllWorldLabels()
        {
            if (Current.Game == null) return;
            List<Map> maps = Find.Maps;
            for (int i = 0; i < maps.Count; i++)
            {
                RegistryOf(maps[i])?.DirtyAllLabels();
            }
        }

        // Room hooks. The original walked room.ContainedAndAdjacentThings, which for the
        // outdoor room is every plant, chunk, item and pawn on the map, on every region
        // rebuild. This does one no-rebuild region lookup per room sign instead.
        public static void UpdateSignsOnRoomChange(Room room)
        {
            if (room == null) return;
            Map map = room.Map;
            List<Comp_Sign> signs = SignsOn(map);
            for (int i = 0; i < signs.Count; i++)
            {
                Comp_Sign sign = signs[i];
                if (!sign.isRoomSign) continue;

                // No-rebuild lookup: this runs from inside the region/room updater.
                Region region = map.regionGrid.GetRegionAt_NoRebuild_InvalidAllowed(sign.parent.Position);
                if (region != null && region.valid && region.Room == room)
                {
                    sign.SetContentsFromRoom(room);
                }
            }
        }
    }
}

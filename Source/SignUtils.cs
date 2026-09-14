using System.Collections.Generic;
using Verse;

namespace Dark.Signs
{
    public static class SignUtils
    {
        // Every spawned sign comp, on every map. A handful of entries, so lookups here are
        // cheap where a walk over listerThings or a room's contents would not be.
        private static readonly List<Comp_Sign> signs = new List<Comp_Sign>();

        public static void RegisterSign(Comp_Sign sign)
        {
            if (!signs.Contains(sign))
            {
                signs.Add(sign);
            }
        }

        public static void UnregisterSign(Comp_Sign sign)
        {
            signs.Remove(sign);
        }

        public static IEnumerable<Comp_Sign> SignsOn(Map map)
        {
            for (int i = 0; i < signs.Count; i++)
            {
                Comp_Sign sign = signs[i];
                if (sign.parent != null && sign.parent.Spawned && sign.parent.Map == map)
                {
                    yield return sign;
                }
            }
        }

        // Regenerate every sign's world label (settings changed).
        public static void DirtyAllWorldLabels()
        {
            for (int i = signs.Count - 1; i >= 0; i--)
            {
                Comp_Sign sign = signs[i];
                if (sign.parent == null || !sign.parent.Spawned)
                {
                    signs.RemoveAt(i);
                    continue;
                }
                sign.DirtyWorldLabel();
            }
        }

        // Room hooks. The original walked room.ContainedAndAdjacentThings, which for the
        // outdoor room is every plant, chunk, item and pawn on the map, on every region
        // rebuild. This does one no-rebuild region lookup per room sign instead.
        public static void UpdateSignsOnRoomChange(Room room)
        {
            if (signs.Count == 0 || room == null) return;
            Map map = room.Map;
            if (map == null) return;

            for (int i = signs.Count - 1; i >= 0; i--)
            {
                Comp_Sign sign = signs[i];
                if (!sign.isRoomSign) continue;
                Thing parent = sign.parent;
                if (parent == null || !parent.Spawned)
                {
                    signs.RemoveAt(i);
                    continue;
                }
                if (parent.Map != map) continue;

                // No-rebuild lookup: this runs from inside the region/room updater.
                Region region = map.regionGrid.GetRegionAt_NoRebuild_InvalidAllowed(parent.Position);
                if (region != null && region.valid && region.Room == room)
                {
                    sign.SetContentsFromRoom(room);
                }
            }
        }
    }
}

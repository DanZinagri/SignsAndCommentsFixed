using System.Collections.Generic;
using Verse;

namespace Dark.Signs
{
    // A label bake can need a frame (freshly requested glyphs reach the GPU atlas on the
    // next render). When SectionLayer_SignLabels meets such a sign it parks it here; each
    // frame we retry the bake and, once it's ready, dirty the sign's cell so the section
    // prints it. The list is empty almost all of the time.
    public class MapComponent_SignLabels : MapComponent
    {
        private static readonly List<Comp_Sign> pending = new List<Comp_Sign>();

        public MapComponent_SignLabels(Map map) : base(map) { }

        public static void MarkPending(Comp_Sign sign)
        {
            if (!pending.Contains(sign))
            {
                pending.Add(sign);
            }
        }

        public override void MapComponentUpdate()
        {
            if (pending.Count == 0) return;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                Comp_Sign sign = pending[i];
                if (sign.parent == null || !sign.parent.Spawned || sign.parent.Map != map)
                {
                    if (sign.parent == null || !sign.parent.Spawned) pending.RemoveAt(i);
                    continue;
                }
                if (sign.TryBakeWorldLabel())
                {
                    pending.RemoveAt(i);
                    sign.DirtyWorldLabel();
                }
            }
        }
    }
}

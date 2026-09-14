using System.Collections.Generic;
using Verse;

namespace Dark.Signs
{
    // Per-map registry of spawned sign comps, plus the retry list for label bakes that need
    // a frame (freshly requested glyphs reach the GPU atlas on the next render).
    //
    // Lives on the map on purpose: a static list would outlive the map. Dev quicktest and
    // map removal discard maps without despawning their things, so stale comps would keep
    // reporting Spawned with a Map property that resolves, by index, to whatever map took
    // the old slot, and their labels would print onto the new map.
    public class MapComponent_SignLabels : MapComponent
    {
        private readonly List<Comp_Sign> signs = new List<Comp_Sign>();
        private readonly List<Comp_Sign> pending = new List<Comp_Sign>();

        public MapComponent_SignLabels(Map map) : base(map) { }

        public List<Comp_Sign> Signs => signs;

        public void Register(Comp_Sign sign)
        {
            if (!signs.Contains(sign))
            {
                signs.Add(sign);
            }
        }

        public void Unregister(Comp_Sign sign)
        {
            signs.Remove(sign);
            pending.Remove(sign);
        }

        public void MarkPending(Comp_Sign sign)
        {
            if (!pending.Contains(sign))
            {
                pending.Add(sign);
            }
        }

        public void DirtyAllLabels()
        {
            for (int i = 0; i < signs.Count; i++)
            {
                signs[i].DirtyWorldLabel();
            }
        }

        public override void MapComponentUpdate()
        {
            if (pending.Count == 0) return;
            for (int i = pending.Count - 1; i >= 0; i--)
            {
                Comp_Sign sign = pending[i];
                if (!sign.parent.Spawned)
                {
                    pending.RemoveAt(i);
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

using Verse;

namespace Dark.Signs
{
    // Comment with graphic-swapping, copied from power switches / vents.
    // Lets comments be hidden altogether by swapping to an invisible graphic.
    public class Building_Comment : Building
    {
        private Comp_Sign signComp;

        public override Graphic Graphic
        {
            get
            {
                // Graphic can be requested before SpawnSetup (e.g. while placing)
                if (signComp == null) signComp = GetComp<Comp_Sign>();
                return signComp?.CurrentGraphic ?? base.Graphic;
            }
        }

        public override void SpawnSetup(Map map, bool respawningAfterLoad)
        {
            base.SpawnSetup(map, respawningAfterLoad);
            signComp = GetComp<Comp_Sign>();
        }
    }
}

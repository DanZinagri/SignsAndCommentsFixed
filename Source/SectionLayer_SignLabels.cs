using RimWorld;
using Verse;

namespace Dark.Signs
{
    // World-space sign labels, printed into the map mesh like any building graphic.
    // RimWorld instantiates every SectionLayer subclass per map section automatically.
    // Per frame this costs one queued Graphics.DrawMesh per sign in view and no CPU work
    // otherwise; the mesh is only rebuilt when a sign in the section is dirtied
    // (Signs_Labels flag) or fog changes.
    public class SectionLayer_SignLabels : SectionLayer
    {
        public SectionLayer_SignLabels(Section section) : base(section)
        {
            relevantChangeTypes = (ulong)SignDefOf.Signs_Labels | (ulong)MapMeshFlagDefOf.FogOfWar;
        }

        // Checked every frame by DrawLayer, so the settings/comment toggle hide labels
        // instantly without a regeneration.
        public override bool Visible => Settings.worldSpaceLabels && DoPlaySettingsGlobalControls_ShowCommentToggle.drawComments;

        public override void Regenerate()
        {
            ClearSubMeshes(MeshParts.All);
            CellRect rect = section.CellRect;
            foreach (Comp_Sign sign in SignUtils.SignsOn(Map))
            {
                if (rect.Contains(sign.parent.Position))
                {
                    sign.PrintWorldLabel(this);
                }
            }
            FinalizeMesh(MeshParts.All);
        }
    }
}

using RimWorld;
using Verse;

namespace Dark.Signs
{
    // Resolved once after defs load. Lets the hot paths compare def references instead of comparing defName strings.
    [DefOf]
    public static class SignDefOf
    {
        public static ThingDef Comment;
        public static DesignationDef CommentDummy;
        public static MapMeshFlagDef Signs_Labels;

        static SignDefOf()
        {
            DefOfHelper.EnsureInitializedInCtor(typeof(SignDefOf));
        }
    }
}

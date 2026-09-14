using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Dark.Signs
{
    public static class CommentContentClipboard
    {
        private static string copiedString = "";
        private static bool copiedHide;
        private static GameFont copiedFont = GameFont.Tiny;
        private static Color copiedColor = Color.white;
        private static bool copied;

        public static bool HasCopiedContent => copied;

        public static void CopyFrom(Comp_Sign sign)
        {
            copiedString = sign.signContent ?? "";
            copiedHide = sign.hideLabelOverride;
            copiedFont = sign.fontSize;
            copiedColor = sign.labelColor;
            copied = true;
        }

        public static void PasteInto(Comp_Sign sign)
        {
            // New string instance so the target's line cache sees a change
            sign.signContent = string.Copy(copiedString);
            sign.fontSize = copiedFont;
            sign.hideLabelOverride = copiedHide;
            sign.labelColor = copiedColor;
        }

        // Built once per sign and cached by Comp_Sign; gizmos are requested every frame while the sign is selected.
        public static Command_Action MakeCopyGizmo(Comp_Sign sign)
        {
            return new Command_Action
            {
                icon = SignTex.Copy,
                defaultLabel = "Signs_CopyGizmo".Translate(),
                defaultDesc = "Signs_CopyGizmo_desc".Translate(),
                hotKey = KeyBindingDefOf.Misc4,
                action = () =>
                {
                    SoundDefOf.Tick_High.PlayOneShotOnCamera();
                    CopyFrom(sign);
                }
            };
        }

        public static Command_Action MakePasteGizmo(Comp_Sign sign)
        {
            return new Command_Action
            {
                icon = SignTex.Paste,
                defaultLabel = "Signs_PasteGizmo".Translate(),
                defaultDesc = "Signs_PasteGizmo_desc".Translate(),
                hotKey = KeyBindingDefOf.Misc5,
                action = () =>
                {
                    SoundDefOf.Tick_High.PlayOneShotOnCamera();
                    PasteInto(sign);
                }
            };
        }
    }
}

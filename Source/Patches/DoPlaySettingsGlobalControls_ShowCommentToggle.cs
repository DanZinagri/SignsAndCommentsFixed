using System.Collections.Generic;
using HarmonyLib;
using RimWorld;
using Verse;

namespace Dark.Signs
{
    // Adds the show/hide comments toggle to the bottom-right play settings row.
    // Runs every frame, so everything it needs is cached.
    [HarmonyPatch(typeof(PlaySettings), nameof(PlaySettings.DoPlaySettingsGlobalControls))]
    public static class DoPlaySettingsGlobalControls_ShowCommentToggle
    {
        public static bool drawComments = true;
        private static bool lastVal = drawComments;

        private static string tooltip;
        private static LoadedLanguage tooltipLanguage;

        private static string Tooltip
        {
            get
            {
                LoadedLanguage lang = LanguageDatabase.activeLanguage;
                if (tooltip == null || tooltipLanguage != lang)
                {
                    tooltip = "Signs_ToggleToolTip".Translate();
                    tooltipLanguage = lang;
                }
                return tooltip;
            }
        }

        public static void Postfix(WidgetRow row, bool worldView)
        {
            if (worldView || !Settings.addCommentToggle) return;

            row.ToggleableIcon(ref drawComments, SignTex.CommentToggle, Tooltip, SoundDefOf.Mouseover_ButtonToggle);
            if (drawComments != lastVal)
            {
                lastVal = drawComments;
                RefreshAllSigns();
            }
        }

        // Comments swap graphic when hidden, so their map mesh sections must be redrawn.
        // Looks the comments up per map instead of keeping a static list that outlived them.
        public static void RefreshAllSigns()
        {
            if (Current.Game == null) return;
            List<Map> maps = Find.Maps;
            for (int m = 0; m < maps.Count; m++)
            {
                List<Thing> comments = maps[m].listerThings.ThingsOfDef(SignDefOf.Comment);
                for (int i = 0; i < comments.Count; i++)
                {
                    comments[i].TryGetComp<Comp_Sign>()?.NotifyVisibilityChange();
                }
            }
        }
    }
}

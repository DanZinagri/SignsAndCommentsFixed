using RimWorld;
using System.Text;
using UnityEngine;
using Verse;

namespace Dark.Signs
{
    public class Settings : ModSettings
    {
        public static bool useCharacterLimit = true;
        public static int characterLimit = 512;
        private static string charLimEditBuffer = characterLimit.ToString();
        public static bool hideLabelsWhenZoomedOut = false;
        public static bool alwaysShowLabels = true;
        public static bool cachedLabelRendering = true;
        public static bool worldSpaceLabels = true;
        public static float worldPixelsPerCell = 48f;   // label pixels per map cell; larger = smaller text
        public static bool addCommentToggle = true;
        public static bool commentToggleHidesComments = false;
        private static bool lastVal_commentToggleHidesComments = commentToggleHidesComments;
        private static bool lastVal_worldSpaceLabels = worldSpaceLabels;
        private static float lastVal_worldPixelsPerCell = worldPixelsPerCell;
        private static float lastVal_worldVerticalOffset = worldVerticalOffset;
        public static int minZoomLevel = (int)CameraZoomRange.Middle;
        public static int maxZoomLevel = (int)CameraZoomRange.Middle;
        public static float worldVerticalOffset = 0.5f;
        public static Color globalLabelColor = GenMapUI.DefaultThingLabelColor.ToOpaque();

        public void DoWindowContents(Rect inRect)
        {
            var listing = new Listing_Standard();
            listing.Begin(inRect);
            listing.ColumnWidth = inRect.width / 2.2f;

            listing.Label("Signs_SettingsDisplay".Translate());
            listing.GapLine();

            listing.Label("Signs_SettingsYOffset".Translate() + " " + (worldVerticalOffset - 0.5f).ToString("N2"));
            worldVerticalOffset = listing.Slider(worldVerticalOffset, -1f, 1f);
            listing.GapLine();

            var col = new StringBuilder();
            col.Append("(");
            col.Append((int)(globalLabelColor.r * 100));
            col.Append(",");
            col.Append((int)(globalLabelColor.g * 100));
            col.Append(",");
            col.Append((int)(globalLabelColor.b * 100));
            col.Append(",");
            col.Append((int)(globalLabelColor.a * 100));
            col.Append(")");
            Rect colSettingRect = listing.Label("Signs_SettingsColor".Translate() + ": " + col);
            colSettingRect.x += colSettingRect.width - 32f;
            colSettingRect.size = new Vector2(32f, 32f);
            Widgets.DrawBoxSolid(colSettingRect, globalLabelColor);
            if (Widgets.ButtonInvisible(colSettingRect, true))
            {
                Find.WindowStack.Add(new Dialog_ChooseColor("", globalLabelColor, Dark.Signs.Mod.colorChoices, selectedColor =>
                {
                    globalLabelColor = selectedColor;
                }));
            }
            listing.Gap();
            listing.GapLine();

            // World-space labels: part of the map mesh, zero per-frame cost, scale with zoom.
            listing.CheckboxLabeled("Signs_SettingsWorldLabels".Translate(), ref worldSpaceLabels, "Signs_SettingsWorldLabels_desc".Translate());
            if (worldSpaceLabels)
            {
                listing.Indent();
                listing.Label("Signs_SettingsWorldLabelSize".Translate() + ": " + worldPixelsPerCell.ToString("N0"), tooltip: "Signs_SettingsWorldLabelSize_desc".Translate());
                worldPixelsPerCell = Mathf.Round(listing.Slider(worldPixelsPerCell, 16f, 128f));
                listing.Outdent();
            }
            else
            {
                // Screen-space labels: constant pixel size, drawn every frame.
                listing.CheckboxLabeled("Signs_SettingsCachedLabels".Translate(), ref cachedLabelRendering, "Signs_SettingsCachedLabels_desc".Translate());
                listing.CheckboxLabeled("Signs_SettingsAllZooms".Translate(), ref alwaysShowLabels, "Signs_SettingsAllZooms_desc".Translate());
                if (!alwaysShowLabels)
                {
                    listing.Gap();
                    listing.Indent();
                    if (listing.RadioButton("Signs_SettingsHideOut".Translate(), active: hideLabelsWhenZoomedOut, tooltip: "Signs_SettingsHideOut_desc".Translate()))
                        hideLabelsWhenZoomedOut = true;
                    if (hideLabelsWhenZoomedOut)
                    {
                        listing.Label("Signs_SettingsMinZoom".Translate() + ": " + ((CameraZoomRange)minZoomLevel) + " (" + minZoomLevel + ")");
                        minZoomLevel = (int)listing.Slider(minZoomLevel, (int)CameraZoomRange.Closest, (int)CameraZoomRange.Furthest);
                    }

                    if (listing.RadioButton("Signs_SettingsHideIn".Translate(), active: !hideLabelsWhenZoomedOut, tooltip: "Signs_SettingsHideIn_desc".Translate()))
                        hideLabelsWhenZoomedOut = false;
                    if (!hideLabelsWhenZoomedOut)
                    {
                        listing.Label("Signs_SettingsMaxZoom".Translate() + ": " + ((CameraZoomRange)maxZoomLevel) + " (" + maxZoomLevel + ")");
                        maxZoomLevel = (int)listing.Slider(maxZoomLevel, (int)CameraZoomRange.Closest, (int)CameraZoomRange.Furthest);
                    }
                    listing.Outdent();
                }
            }
            // Printed labels are static; any setting that moves or resizes them needs a reprint.
            if (worldSpaceLabels != lastVal_worldSpaceLabels || worldPixelsPerCell != lastVal_worldPixelsPerCell
                || worldVerticalOffset != lastVal_worldVerticalOffset)
            {
                lastVal_worldSpaceLabels = worldSpaceLabels;
                lastVal_worldPixelsPerCell = worldPixelsPerCell;
                lastVal_worldVerticalOffset = worldVerticalOffset;
                SignUtils.DirtyAllWorldLabels();
            }

            listing.NewColumn();

            listing.CheckboxLabeled("Signs_SettingsAddToggle".Translate(), ref addCommentToggle, "Signs_SettingsAddToggle_desc".Translate());
            listing.CheckboxLabeled("Signs_SettingsToggleToolTipHidesComments".Translate(), ref commentToggleHidesComments, "Signs_SettingsToggleToolTipHidesComments_desc".Translate());
            if (commentToggleHidesComments != lastVal_commentToggleHidesComments)
            {
                DoPlaySettingsGlobalControls_ShowCommentToggle.RefreshAllSigns();
                lastVal_commentToggleHidesComments = commentToggleHidesComments;
            }
            listing.GapLine();

            listing.CheckboxLabeled("Signs_SettingsUseCharLimit".Translate(), ref useCharacterLimit, "Signs_SettingsUseCharLimit_desc".Translate());
            if (!useCharacterLimit)
            {
                Rect warningRect = listing.Label("Signs_SettingsUseCharLimitWarning".Translate());
                Color colred = Color.red;
                colred.a = 0.5f;
                Widgets.DrawBoxSolid(warningRect, colred);
            }
            else
            {
                listing.Indent(32);
                listing.IntEntry(ref characterLimit, ref charLimEditBuffer);
                listing.Outdent(32);
            }

            listing.End();
        }

        public override void ExposeData()
        {
            Scribe_Values.Look(ref hideLabelsWhenZoomedOut, "hideLabelsWhenZoomedOut", false);
            Scribe_Values.Look(ref alwaysShowLabels, "alwaysShowLabels", true);
            Scribe_Values.Look(ref cachedLabelRendering, "cachedLabelRendering", true);
            Scribe_Values.Look(ref worldSpaceLabels, "worldSpaceLabels", true);
            Scribe_Values.Look(ref worldPixelsPerCell, "worldPixelsPerCell", 48f);
            Scribe_Values.Look(ref addCommentToggle, "addCommentToggle", true);
            Scribe_Values.Look(ref commentToggleHidesComments, "commentToggleHidesComments", false);
            Scribe_Values.Look(ref minZoomLevel, "minZoomLevel", (int)CameraZoomRange.Middle);
            Scribe_Values.Look(ref maxZoomLevel, "maxZoomLevel", (int)CameraZoomRange.Middle);
            Scribe_Values.Look(ref worldVerticalOffset, "worldVerticalOffset", 0.5f);
            // Key kept misspelled on purpose so existing settings files still load.
            Scribe_Values.Look(ref globalLabelColor, "globalLabalColor", GenMapUI.DefaultThingLabelColor);
            if (Scribe.mode == LoadSaveMode.PostLoadInit)
            {
                lastVal_worldSpaceLabels = worldSpaceLabels;
                lastVal_worldPixelsPerCell = worldPixelsPerCell;
                lastVal_worldVerticalOffset = worldVerticalOffset;
            }
        }
    }
}

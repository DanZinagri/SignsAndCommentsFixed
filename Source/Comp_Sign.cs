using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using RimWorld;
using UnityEngine;
using Verse;
using Verse.Sound;

namespace Dark.Signs
{
    public class Comp_Sign : ThingComp, IRenameable
    {
        public CompProperties_Sign Props => (CompProperties_Sign)props;

        public bool canEditContent => Props.canEditContent;
        public bool editOnPlacement => Props.editOnPlacement;
        public bool isRoomSign => Props.isRoomSign;

        // ---- Epitaphs compatibility. Resolved once for the whole game. ----
        // The original scanned every loaded assembly in every Comp_Sign constructor
        // when Epitaphs was not installed, because only a hit set the static.
        public static readonly Assembly EpitaphAssem;
        private static readonly Type EpitaphCompType;
        private static readonly PropertyInfo EpitaphInscriptionProperty;
        private static readonly bool EpitaphsLoaded;

        static Comp_Sign()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (!assembly.FullName.StartsWith("Epitaph")) continue;
                Type compType = assembly.GetType("Epitaph.Comp_Epitaph");
                PropertyInfo inscription = compType?.GetProperty("Inscription");
                if (compType == null || inscription == null)
                {
                    Log.Warning("(Signs) Found an Epitaph assembly but not Epitaph.Comp_Epitaph.Inscription; epitaph integration disabled.");
                    break;
                }
                Log.Message("(Signs) Found Epitaph mod, patching");
                EpitaphAssem = assembly;
                EpitaphCompType = compType;
                EpitaphInscriptionProperty = inscription;
                EpitaphsLoaded = true;
                break;
            }
        }

        private bool hasEpitaph;      // this Thing also carries an epitaph comp (graves/sarcophagi)
        private object EpitaphComp;   // passed to the epitaph rename dialog

        public bool hideLabelOverride = false;
        private Graphic HiddenGraphic;
        // Baked label textures, see SignLabelTexture. Separate instances for the two modes
        // so selecting a sign in world mode doesn't rebake the printed one.
        private SignLabelTexture guiLabel;
        private SignLabelTexture worldLabel;

        // Inputs of the last world-label print, to detect changes from any source
        // (edit dialog, paste, room tick, epitaph editor, gizmos).
        private string[] printedLines;
        private GameFont printedFont;
        private Color printedColor;
        private bool printedHidden;

        public Graphic CurrentGraphic
        {
            get
            {
                // Swap to the invisible graphic while comments are toggled off
                if (Settings.commentToggleHidesComments
                    && !DoPlaySettingsGlobalControls_ShowCommentToggle.drawComments
                    && parent.def == SignDefOf.Comment)
                {
                    if (HiddenGraphic == null)
                    {
                        GraphicData gd = parent.def.graphicData;
                        HiddenGraphic = GraphicDatabase.Get(gd.graphicClass, gd.texPath + "_Hidden",
                            gd.shaderType.Shader, gd.drawSize, parent.DrawColor, parent.DrawColorTwo, null);
                    }
                    return HiddenGraphic;
                }
                return parent.DefaultGraphic;
            }
        }

        public void NotifyVisibilityChange()
        {
            if (parent.Spawned)
            {
                parent.DirtyMapMesh(parent.Map);
            }
        }

        // null until spawned so PostSpawnSetup can tell whether content already exists
        private string _signContent;
        public string signContent
        {
            get => hasEpitaph ? (string)EpitaphInscriptionProperty.GetValue(EpitaphComp) : _signContent;
            set => _signContent = value;
        }

        private GameFont _fontSize = GameFont.Tiny;
        public GameFont fontSize
        {
            get => _fontSize;
            set => _fontSize = value;
        }

        private Color _labelColor = Settings.globalLabelColor;
        public Color labelColor
        {
            get => _labelColor;
            set => _labelColor = value;
        }

        // ---- Label line cache ----
        // Split + unescape happen once per content change instead of once per line per frame.
        // Keyed on the content string reference so it also tracks the epitaph comp's string.
        private static readonly string[] LineSeparators = { "\r\n", "\n", "\r" };
        private static readonly string[] NoLines = new string[0];
        private static readonly GUIContent tmpContent = new GUIContent();
        private string cachedSource;
        private string[] cachedLines = NoLines;

        private string[] Lines
        {
            get
            {
                string content = signContent;
                if (!ReferenceEquals(content, cachedSource))
                {
                    cachedSource = content;
                    cachedLines = BuildLines(content);
                }
                return cachedLines;
            }
        }

        private static string[] BuildLines(string content)
        {
            if (string.IsNullOrEmpty(content)) return NoLines;
            string[] lines = content.Split(LineSeparators, StringSplitOptions.None);
            bool anyText = false;
            for (int i = 0; i < lines.Length; i++)
            {
                lines[i] = UnescapeContents(lines[i]);
                if (lines[i].Length > 0) anyText = true;
            }
            // Blank lines still add vertical spacing, but a sign with no text at all draws nothing.
            return anyText ? lines : NoLines;
        }

        public override void PostSpawnSetup(bool respawningAfterLoad)
        {
            base.PostSpawnSetup(respawningAfterLoad);
            labelWorldPosFor = float.NaN;   // comps survive minify/reinstall; the position may not
            if (!respawningAfterLoad)
            {
                if (isRoomSign)
                {
                    SetContentsFromRoom();
                }
                else
                {
                    if (signContent == null)
                    {
                        signContent = Props.defaultContents;
                    }

                    if (editOnPlacement)
                    {
                        Find.WindowStack.Add(new Dialog_RenameSign(this));
                    }

                    if (BuildableCanGoOverFog(parent.def) && parent.Fogged())
                    {
                        // A designation is drawn over fog where the building itself is not.
                        parent.Map.designationManager.AddDesignation(
                            new Designation(new LocalTargetInfo(parent), SignDefOf.CommentDummy));
                    }
                }
            }
            else
            {
                RemoveExtraLineEndings();
            }

            if (EpitaphsLoaded)
            {
                foreach (ThingComp comp in parent.AllComps)
                {
                    if (EpitaphCompType.IsInstanceOfType(comp))
                    {
                        EpitaphComp = comp;
                        hasEpitaph = true;
                        break;
                    }
                }
            }

            SignUtils.RegisterSign(this);
            DirtyWorldLabel();
        }

        public override void PostDeSpawn(Map map, DestroyMode mode = DestroyMode.Vanish)
        {
            base.PostDeSpawn(map, mode);
            SignUtils.UnregisterSign(this);
            // Position is still the last one; the section must drop the printed label.
            map?.mapDrawer.MapMeshDirty(parent.Position, SignDefOf.Signs_Labels);
            DisposeLabels();
        }

        public override void PostDestroy(DestroyMode mode, Map previousMap)
        {
            base.PostDestroy(mode, previousMap);
            DisposeLabels();
        }

        private void DisposeLabels()
        {
            guiLabel?.Dispose();
            guiLabel = null;
            worldLabel?.Dispose();
            worldLabel = null;
            printedLines = null;
        }

        private void RemoveExtraLineEndings()
        {
            if (signContent == null) return;
            signContent = signContent.Trim().Replace("\r", "");
        }

        public static string UnescapeContents(string raw)
        {
            if (raw.IndexOf('&') < 0) return raw;
            return raw.Replace("&apos;", "'")
                      .Replace("&quot;", "\"")
                      .Replace("&gt;", ">")
                      .Replace("&lt;", "<")
                      .Replace("&amp;", "&");
        }

        // ---- Room signs ----
        public void SetContentsFromRoom()
        {
            SetContentsFromRoom(parent.GetRoom());
        }

        public void SetContentsFromRoom(Room room)
        {
            if (room == null) return;
            // Vanilla's own label: "Bedroom", "Someone's bedroom", "X and Y's bedroom", ...
            // It's a fresh string every call, so compare by value: assigning an equal string
            // would rebake the label and regenerate the section every rare tick.
            string text = room.GetRoomRoleLabel().CapitalizeFirst();
            if (text != _signContent)
            {
                signContent = text;
            }
        }

        public override void CompTickRare()
        {
            base.CompTickRare();
            // Safety net for room changes the Room hooks miss. Only room signs tick.
            if (isRoomSign)
            {
                SetContentsFromRoom();
            }
        }

        // ---- Drawing ----
        private bool ShouldDrawLabel()
        {
            if (Find.Selector.IsSelected(parent))
                return true;

            if (hideLabelOverride)
                return false;

            if (!DoPlaySettingsGlobalControls_ShowCommentToggle.drawComments)
                return false;

            if (Settings.alwaysShowLabels)
                return true;

            if (Settings.hideLabelsWhenZoomedOut)
            {
                if ((int)Find.CameraDriver.CurrentZoom > Settings.minZoomLevel)
                    return false;
            }
            else
            {
                if ((int)Find.CameraDriver.CurrentZoom < Settings.maxZoomLevel)
                    return false;
            }
            return true;
        }

        public override void DrawGUIOverlay()
        {
            string[] lines = Lines;

            // If baking ever failed this session, fall through to the plain GUI labels.
            if (Settings.worldSpaceLabels && !SignLabelTexture.Broken)
            {
                // The printed label is static; reprint if anything it shows has changed.
                if (!ReferenceEquals(lines, printedLines) || fontSize != printedFont
                    || labelColor != printedColor || hideLabelOverride != printedHidden)
                {
                    DirtyWorldLabel();
                }
                // The printed label is the label; nothing is drawn per frame, selected or not.
                return;
            }

            if (lines.Length == 0) return;   // graves, pen markers etc. with no text cost nothing
            if (!ShouldDrawLabel()) return;
            DrawSignLabels(lines);
        }

        // ---- World-space label (SectionLayer_SignLabels) ----

        public void DirtyWorldLabel()
        {
            if (parent.Spawned)
            {
                parent.Map.mapDrawer.MapMeshDirty(parent.Position, SignDefOf.Signs_Labels);
            }
        }

        private bool WorldLabelVisible(string[] lines)
        {
            if (lines.Length == 0 || hideLabelOverride) return false;
            // Comments are meant to be visible in fog; nothing else is.
            return parent.def == SignDefOf.Comment || !parent.Fogged();
        }

        // Bake attempt for the map component's retry loop. True when ready.
        public bool TryBakeWorldLabel()
        {
            if (worldLabel == null) worldLabel = new SignLabelTexture();
            return worldLabel.EnsureBaked(Lines, fontSize, labelColor, SignLabelTexture.WorldBakeScale, true);
        }

        // Called by the section layer while regenerating the section this sign sits in.
        public void PrintWorldLabel(SectionLayer layer)
        {
            string[] lines = Lines;
            printedLines = lines;
            printedFont = fontSize;
            printedColor = labelColor;
            printedHidden = hideLabelOverride;

            if (!WorldLabelVisible(lines)) return;
            if (SignLabelTexture.Broken) return;

            if (!TryBakeWorldLabel())
            {
                MapComponent_SignLabels.MarkPending(this);   // glyphs uploading; reprint next frame
                return;
            }
            Texture2D tex = worldLabel.Texture;
            if (tex == null) return;

            // Bounds are in unscaled label pixels relative to the anchor; the label hangs
            // below the anchor on screen, i.e. towards -z in the world.
            float ppc = Mathf.Max(1f, Settings.worldPixelsPerCell);
            Rect b = worldLabel.Bounds;
            Vector3 center = LabelWorldAnchor();
            center.x += b.center.x / ppc;
            center.z -= b.center.y / ppc;
            var size = new Vector2(b.width / ppc, b.height / ppc);
            Printer_Plane.PrintPlane(layer, center, size, worldLabel.WorldMaterial);
        }

        // Cycles through font sizes, looping back around
        private void ChangeSize()
        {
            fontSize += 1;
            if (fontSize > GameFont.Medium)
            {
                fontSize = GameFont.Tiny;
            }
        }

        // Background height as a fraction of the measured line height, per font
        private float GetLineHeightBGFraction() => GetLineHeightBGFraction(fontSize);

        internal static float GetLineHeightBGFraction(GameFont font)
        {
            switch (font)
            {
                case GameFont.Small: return 0.7f;
                case GameFont.Medium: return 0.8f;
                default: return 0.6f;
            }
        }

        // Direction-specific offset for the current rotation if defined, else the generic labelOffset
        private Vector2 GetLabelOffset()
        {
            CompProperties_Sign p = Props;
            Rot4 rot = parent.Rotation;
            Vector2? specific = null;
            if (rot == Rot4.South) specific = p.labelOffset_South;
            else if (rot == Rot4.North) specific = p.labelOffset_North;
            else if (rot == Rot4.West) specific = p.labelOffset_West;
            else if (rot == Rot4.East) specific = p.labelOffset_East;
            return specific ?? p.labelOffset;
        }

        // World-space anchor of the label. Buildings don't move, so this only changes
        // with the vertical-offset setting (reinstalling respawns the comp).
        private Vector3 labelWorldPos;
        private float labelWorldPosFor = float.NaN;

        private Vector3 LabelWorldAnchor()
        {
            float vOffset = Settings.worldVerticalOffset;
            if (labelWorldPosFor != vOffset)
            {
                Vector3 position = parent.TrueCenter();
                // Half a layer above MetaOverlays: comments sit at that altitude themselves,
                // and two transparent quads at the same depth draw in undefined order.
                position.y = AltitudeLayer.MetaOverlays.AltitudeFor() + Altitudes.AltInc * 0.5f;
                Vector2 offset = GetLabelOffset();
                position.x += offset.x;
                position.z += offset.y + vOffset;
                labelWorldPos = position;
                labelWorldPosFor = vOffset;
            }
            return labelWorldPos;
        }

        private Vector2 GetSignLabelPos()
        {
            Vector2 vector = Find.Camera.WorldToScreenPoint(LabelWorldAnchor()) / Prefs.UIScale;
            vector.y = UI.screenHeight - vector.y - 1f;
            return vector;
        }

        private void DrawSignLabels(string[] lines)
        {
            Color color = labelColor;
            Vector2 drawpos = GetSignLabelPos();

            if (Settings.cachedLabelRendering && !SignLabelTexture.Broken)
            {
                if (guiLabel == null) guiLabel = new SignLabelTexture();
                if (guiLabel.Draw(drawpos, lines, fontSize, color)) return;
                // Bake not ready this frame (glyphs still uploading): draw the slow way once.
            }

            // The original measured the step with the Small font (it reset the font after
            // every line), so keep that to leave existing signs' spacing unchanged.
            Text.Font = GameFont.Small;
            float lineStep = Text.LineHeight * GetLineHeightBGFraction() * 1.1f;
            if (fontSize == GameFont.Medium) lineStep += 4f;
            Text.Font = fontSize;

            for (int i = 0; i < lines.Length; i++)
            {
                DrawSignLabel(drawpos, lines[i], color);
                drawpos.y += lineStep;
            }
            Text.Font = GameFont.Small;
        }

        // Draws one already-unescaped line. Caller has set Text.Font.
        private void DrawSignLabel(Vector2 screenPos, string s, Color color)
        {
            if (s.Length == 0) return;   // blank line: spacing only

            tmpContent.text = s;
            Vector2 size = Text.CurFontStyle.CalcSize(tmpContent);
            var labelRect = new Rect(screenPos.x - size.x / 2f, screenPos.y - 3f, size.x, size.y);
            if (!labelRect.Overlaps(new Rect(0f, 0f, UI.screenWidth, UI.screenHeight)))
            {
                return;
            }

            GUI.DrawTexture(new Rect(screenPos.x - size.x / 2f - 4f, screenPos.y, size.x + 8f, size.y * GetLineHeightBGFraction()), TexUI.GrayTextBG);
            GUI.color = color;
            Text.Anchor = TextAnchor.UpperCenter;
            Widgets.Label(labelRect, s);
            GUI.color = Color.white;
            Text.Anchor = TextAnchor.UpperLeft;
        }

        // ---- Gizmos ----
        // Requested every frame while the sign is selected, so the Command objects are built
        // once per comp and only their mutable bits are refreshed per call.
        private Command_Toggle showGizmo;
        private Command_Action sizeGizmo;
        private Command_Action colorGizmo;
        private Command_Action editGizmo;
        private Command_Action copyGizmo;
        private Command_Action pasteGizmo;

        private void BuildGizmos()
        {
            showGizmo = new Command_Toggle
            {
                defaultLabel = "Signs_ShowGizmo".Translate(),
                defaultDesc = "Signs_ShowGizmo_desc".Translate(),
                hotKey = KeyBindingDefOf.Misc3,
                icon = TexCommand.ForbidOff,
                isActive = () => !hideLabelOverride,
                toggleAction = () => hideLabelOverride = !hideLabelOverride
            };

            sizeGizmo = new Command_Action
            {
                icon = SignTex.SignSize,
                defaultDesc = "Signs_SizeGismo_desc".Translate(),
                iconDrawScale = 1.2f,
                action = () =>
                {
                    SoundDefOf.Tick_High.PlayOneShotOnCamera();
                    ChangeSize();
                }
            };

            colorGizmo = new Command_Action
            {
                icon = SignTex.ColorPicker,
                defaultLabel = "Signs_ColorGizmo".Translate(),
                defaultDesc = "Signs_SizeGismo_desc".Translate(),
                iconDrawScale = 1f,
                action = () =>
                {
                    SoundDefOf.Tick_High.PlayOneShotOnCamera();
                    Find.WindowStack.Add(new Dialog_ChooseColor("", labelColor, Mod.colorChoices, selectedColor =>
                    {
                        labelColor = selectedColor;
                    }));
                }
            };

            if (!canEditContent) return;

            editGizmo = new Command_Action
            {
                icon = TexButton.Rename,
                defaultLabel = "Signs_EditGizmo".Translate(),
                defaultDesc = "Signs_EditGizmo_desc".Translate(),
                hotKey = KeyBindingDefOf.Misc1,
                action = () =>
                {
                    if (EpitaphsLoaded && hasEpitaph && EpitaphComp != null)
                    {
                        var dlg = (Window)EpitaphAssem.CreateInstance("Epitaph.Dialog_EditEpitaph", false,
                            BindingFlags.CreateInstance, null, new[] { EpitaphComp }, null, null);
                        Find.WindowStack.Add(dlg);
                    }
                    else
                    {
                        Find.WindowStack.Add(new Dialog_RenameSign(this));
                    }
                }
            };

            if (hasEpitaph) return;
            copyGizmo = CommentContentClipboard.MakeCopyGizmo(this);
            pasteGizmo = CommentContentClipboard.MakePasteGizmo(this);
        }

        public override IEnumerable<Gizmo> CompGetGizmosExtra()
        {
            foreach (Gizmo g in base.CompGetGizmosExtra())
            {
                yield return g;
            }

            if (parent.Faction == null || !parent.Faction.IsPlayer)
            {
                yield break;
            }

            if (showGizmo == null) BuildGizmos();

            yield return showGizmo;
            sizeGizmo.defaultLabel = GetSizeName(fontSize);
            yield return sizeGizmo;
            yield return colorGizmo;
            if (editGizmo == null) yield break;
            yield return editGizmo;
            if (copyGizmo == null) yield break;
            yield return copyGizmo;
            pasteGizmo.Disabled = !CommentContentClipboard.HasCopiedContent;
            yield return pasteGizmo;
        }

        // Front-facing font names instead of the confusing internal ones
        private static string[] sizeNames;

        private static string GetSizeName(GameFont f)
        {
            if (sizeNames == null)
            {
                sizeNames = new[] { (string)"Signs_TinyFont".Translate(), (string)"Signs_SmallFont".Translate(), (string)"Signs_MediumFont".Translate() };
            }
            int i = (int)f;
            return sizeNames[i >= 0 && i < sizeNames.Length ? i : 2];
        }

        // Rebuilt only when the content changes; the inspect pane asks every frame.
        private string inspectSource;
        private string inspectCached = "";

        public override string CompInspectStringExtra()
        {
            if (hasEpitaph || _signContent == null)
            {
                return "";
            }
            string content = _signContent;
            if (ReferenceEquals(content, inspectSource))
            {
                return inspectCached;
            }
            inspectSource = content;

            string trimmedContent = UnescapeContents(content.Trim());
            if (trimmedContent.Length == 0)
            {
                return inspectCached = "";
            }
            trimmedContent = '"' + trimmedContent + '"';

            var sb = new StringBuilder();
            sb.Append("Signs_InspectStringPrefix".Translate());
            foreach (string line in trimmedContent.Split(LineSeparators, StringSplitOptions.RemoveEmptyEntries))
            {
                sb.AppendLine();
                sb.Append(line);
            }
            return inspectCached = sb.ToString();
        }

        public static bool BuildableCanGoOverFog(BuildableDef def)
        {
            return def == SignDefOf.Comment;
        }

        public override void PostExposeData()
        {
            base.PostExposeData();
            Scribe_Values.Look(ref _signContent, "content");
            Scribe_Values.Look(ref _fontSize, "fontSize");
            Scribe_Values.Look(ref _labelColor, "labelColor", Settings.globalLabelColor);
            Scribe_Values.Look(ref hideLabelOverride, "hideLabelOverride");
        }

        // ---- IRenameable ----
        private string _baseLabel;
        private bool _doneBaseLabel;

        private string GetBaseLabelRaw()
        {
            if (!_doneBaseLabel)
            {
                _doneBaseLabel = true;
                _baseLabel = Props == null ? "Empty sign" : Props.defaultContents;
            }
            return _baseLabel;
        }

        public string RenamableLabel
        {
            get => signContent ?? GetBaseLabelRaw();
            set => signContent = value;
        }

        public string BaseLabel => GetBaseLabelRaw();
        public string InspectLabel => RenamableLabel;
    }

    public class CompProperties_Sign : CompProperties
    {
        public bool canEditContent = true;
        public bool editOnPlacement = false;
        public bool isRoomSign = false;
        public bool canBeEmpty = false;
        public string defaultContents = "Empty sign";
        public Vector2 labelOffset = new Vector2(0, 0);
        public Vector2? labelOffset_East;
        public Vector2? labelOffset_West;
        public Vector2? labelOffset_South;
        public Vector2? labelOffset_North;

        public CompProperties_Sign()
        {
            compClass = typeof(Comp_Sign);
        }
    }
}

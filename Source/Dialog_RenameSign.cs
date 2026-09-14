using System.Security;
using RimWorld;
using UnityEngine;
using Verse;

namespace Dark.Signs
{
    public class Dialog_RenameSign : Dialog_Rename<Comp_Sign>
    {
        private Color curColor;
        private readonly Comp_Sign signComp;
        private Vector2 scrollbarPos = Vector2.zero;

        // Strings resolved once; DoWindowContents runs every GUI event while open.
        private readonly string headerText;
        private readonly string warningText;
        private readonly string lengthLeftPrefix;
        private readonly string clearText;
        private readonly string colorText;
        private readonly string okText;
        private string lengthLeftText;
        private int lengthLeftFor = -1;

        // Text.CalcHeight only needs re-running when the text or width changes.
        private string measuredText;
        private float measuredWidth;
        private float measuredHeight;

        protected override int MaxNameLength => Settings.characterLimit;

        public override Vector2 InitialSize
        {
            get
            {
                Vector2 size = base.InitialSize;
                size.x += 200f;
                size.y += 100f;
                return size;
            }
        }

        public Dialog_RenameSign(Comp_Sign signComp) : base(signComp)
        {
            this.signComp = signComp;
            // Content is stored escaped; edit it unescaped and escape again on save.
            curName = Comp_Sign.UnescapeContents(signComp?.signContent ?? "");
            curColor = signComp?.labelColor ?? Color.white.ToOpaque();

            headerText = "Signs_EditHeader".Translate();
            warningText = "Signs_LengthWarning".Translate();
            lengthLeftPrefix = "Signs_LengthLeft".Translate() + " ";
            clearText = "Signs_ClearButton".Translate();
            colorText = "Signs_ColorButton".Translate();
            okText = "OK".Translate();
        }

        protected override AcceptanceReport NameIsValid(string name)
        {
            if (!signComp.Props.canBeEmpty)
            {
                AcceptanceReport report = base.NameIsValid(name);
                if (!report.Accepted) return report;
            }
            if (Settings.useCharacterLimit && name.Length > Settings.characterLimit)
            {
                return new AcceptanceReport("Signs_TooLong".Translate());
            }
            return true;
        }

        internal static string SanitizeInput(string raw)
        {
            return SecurityElement.Escape(raw);
        }

        protected override void OnRenamed(string name)
        {
            name = SanitizeInput(name);
            if (signComp != null)
            {
                signComp.signContent = name.Trim();
                signComp.labelColor = curColor;
            }
            else
            {
                Log.Error("(Signs) Edit dialog has no sign or signComp to edit.");
            }
            Messages.Message("Signs_SetSign".Translate(), MessageTypeDefOf.TaskCompletion, historical: false);
        }

        private string TextAreaScrollable(Rect rect, string text)
        {
            if (!ReferenceEquals(text, measuredText) || rect.width != measuredWidth)
            {
                measuredText = text;
                measuredWidth = rect.width;
                measuredHeight = Text.CalcHeight(text, rect.width) + 10f;
            }
            var viewRect = new Rect(0f, 0f, rect.width - 16f, Mathf.Max(measuredHeight, rect.height));
            Widgets.BeginScrollView(rect, ref scrollbarPos, viewRect);
            string result = Widgets.TextArea(viewRect, text);
            Widgets.EndScrollView();
            return result;
        }

        public override void DoWindowContents(Rect inRect)
        {
            Text.Font = GameFont.Small;
            if (Event.current.type == EventType.KeyDown && Event.current.keyCode == KeyCode.Return)
            {
                // Enter inserts a newline in the text area; stop the base dialog treating it as OK
                scrollbarPos.y += 20f;
                Event.current.Use();
            }

            GUI.SetNextControlName("RenameField");
            Widgets.Label(new Rect(0f, 0f, inRect.width, 30f), headerText);
            Widgets.Label(new Rect(0f, 30f, inRect.width, 30f), warningText);

            if (Settings.useCharacterLimit)
            {
                int left = MaxNameLength - curName.Length;
                if (left != lengthLeftFor)
                {
                    lengthLeftFor = left;
                    lengthLeftText = lengthLeftPrefix + left;
                }
                var lengthRect = new Rect(inRect.width - inRect.width / 2.5f, 144f, inRect.width / 2.5f, 24f);
                Widgets.Label(lengthRect, lengthLeftText);
                if (left < 0)
                {
                    Color red = Color.red;
                    red.a = 0.6f;
                    Widgets.DrawBoxSolid(lengthRect, red);
                }
            }

            var textRect = new Rect(inRect.width * 0.13f, 64f, inRect.width * 0.9f, 70f);
            curName = TextAreaScrollable(textRect, curName);

            var clearRect = new Rect(0f, 65f, inRect.width * 0.12f, 64f);
            if (Widgets.ButtonText(clearRect, clearText, drawBackground: true, doMouseoverSound: true, Color.red))
            {
                curName = "";
            }
            Widgets.DrawBoxSolid(clearRect, new Color(0.2f, 0f, 0f, 0.6f));

            var colorButtonRect = new Rect(0f, 144f, inRect.width / 3f, 32f);
            var colorSwatchRect = new Rect(inRect.width / 3f + 8f, 144f, 32f, 32f);
            Widgets.DrawBoxSolid(colorSwatchRect, curColor);
            if (Widgets.ButtonText(colorButtonRect, colorText) || Widgets.ButtonInvisible(colorSwatchRect))
            {
                Find.WindowStack.Add(new Dialog_ChooseColor("", curColor, Mod.colorChoices, selectedColor =>
                {
                    curColor = selectedColor;
                    if (signComp != null) signComp.labelColor = selectedColor;
                }));
            }

            if (!Widgets.ButtonText(new Rect(0f, inRect.height - 45f, inRect.width, 36f), okText))
            {
                return;
            }

            AcceptanceReport acceptance = NameIsValid(curName);
            if (!acceptance.Accepted)
            {
                Messages.Message(acceptance.Reason.NullOrEmpty() ? "Signs_BlankMessage".Translate() : (TaggedString)acceptance.Reason,
                    MessageTypeDefOf.RejectInput, historical: false);
            }
            else
            {
                OnRenamed(curName);
                Find.WindowStack.TryRemove(this);
            }
        }
    }
}

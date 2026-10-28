using System.Reflection;
using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;
using Verse;

namespace Dark.Signs
{
    public class Mod : Verse.Mod
    {
        // 18 hues x 3 saturations, offered by the vanilla colour picker.
        public static readonly List<Color> colorChoices = BuildColorChoices();

        private static List<Color> BuildColorChoices()
        {
            var list = new List<Color>(54);
            for (int i = 0; i < 18; i++)
            {
                float h = i / 18f;
                list.Add(Color.HSVToRGB(h, 1f, 1f));
                list.Add(Color.HSVToRGB(h, 0.5f, 1f));
                list.Add(Color.HSVToRGB(h, 0.33f, 1f));
            }
            return list;
        }

        public Mod(ModContentPack content) : base(content)
        {
            GetSettings<Settings>();
            // The assembly is passed explicitly: on .NET 10 (1.7) the parameterless PatchAll() reads the calling
            // assembly off the stack, which breaks when the JIT inlines the call (Harmony issue #395).
            new Harmony("Dark.Signs").PatchAll(Assembly.GetExecutingAssembly());
        }

        public override void DoSettingsWindowContents(Rect inRect)
        {
            base.DoSettingsWindowContents(inRect);
            GetSettings<Settings>().DoWindowContents(inRect);
        }

        public override string SettingsCategory()
        {
            return "Signs_SettingsCategory".Translate();
        }
    }
}

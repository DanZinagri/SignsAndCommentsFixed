using UnityEngine;
using Verse;

namespace Dark.Signs
{
    // Textures resolved once at startup. ContentFinder.Get walks every running mod's
    // content dictionary on each call, so it must not run per frame.
    [StaticConstructorOnStartup]
    public static class SignTex
    {
        public static readonly Texture2D CommentToggle = ContentFinder<Texture2D>.Get("UI/CommentUI");
        public static readonly Texture2D SignSize = ContentFinder<Texture2D>.Get("UI/SignSize");
        public static readonly Texture2D ColorPicker = ContentFinder<Texture2D>.Get("UI/Gizmo_Colorpicker");
        public static readonly Texture2D Copy = ContentFinder<Texture2D>.Get("UI/Commands/CopySettings");
        public static readonly Texture2D Paste = ContentFinder<Texture2D>.Get("UI/Commands/PasteSettings");
    }
}

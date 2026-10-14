using System;
using System.Collections.Generic;
using UnityEngine;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;

namespace AvatarTools
{
    // Applied non-destructively at upload (and when entering play mode); the avatar's own assets are never modified.
    [DisallowMultipleComponent]
    [DefaultExecutionOrder(-10000)]
    [AddComponentMenu("Avatar Tools/Avatar Presets")]
    public class AvatarPresets : MonoBehaviour, IEditorOnly
    {
        public const int MaxSlots = 8;

        [Range(1, MaxSlots)] public int slotCount = 3;
        public string menuName = "Presets";
        [Tooltip("Menu to add the presets submenu to. Leave empty for the avatar's root menu.")]
        public VRCExpressionsMenu installMenu;
        public string[] slotNames = new string[MaxSlots];
        public List<string> parameters = new List<string>();
        // Toggles added at build time (VRCFury, Modular Avatar) have generated parameter names, so they are stored by menu path
        // and resolved against the built menu.
        public List<string> menuToggles = new List<string>();

#if UNITY_EDITOR
        public static Action<AvatarPresets> applyInPlayMode;

        // Early execution order so the generated layer exists before emulators like Gesture Manager read the descriptor.
        void Awake() => applyInPlayMode?.Invoke(this);
#endif
    }
}

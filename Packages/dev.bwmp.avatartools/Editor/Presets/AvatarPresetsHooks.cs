using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase.Editor.BuildPipeline;

namespace AvatarTools.Editor
{
    // VRCFury and NDMF replace the avatar's menus with clones, so the install menu is recorded by path before they run.
    internal class AvatarPresetsInstallMenuHook : IVRCSDKPreprocessAvatarCallback
    {
        internal static readonly Dictionary<AvatarPresets, List<string>> InstallMenuPaths = new Dictionary<AvatarPresets, List<string>>();

        public int callbackOrder => -20000;

        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            InstallMenuPaths.Clear();
            var avatar = avatarGameObject.GetComponent<VRCAvatarDescriptor>();
            if (avatar == null) return true;
            foreach (var presets in avatarGameObject.GetComponentsInChildren<AvatarPresets>(true))
            {
                if (presets.installMenu == null) continue;
                var path = FindPath(avatar.expressionsMenu, presets.installMenu, new HashSet<VRCExpressionsMenu>());
                if (path != null) InstallMenuPaths[presets] = path;
            }
            return true;
        }

        static List<string> FindPath(VRCExpressionsMenu menu, VRCExpressionsMenu target, HashSet<VRCExpressionsMenu> visited)
        {
            if (menu == null || !visited.Add(menu)) return null;
            if (menu == target) return new List<string>();
            foreach (var control in menu.controls)
            {
                if (control.type != VRCExpressionsMenu.Control.ControlType.SubMenu) continue;
                var path = FindPath(control.subMenu, target, visited);
                if (path == null) continue;
                path.Insert(0, control.name);
                return path;
            }
            return null;
        }

        internal static VRCExpressionsMenu Resolve(VRCExpressionsMenu root, List<string> path)
        {
            var menu = root;
            foreach (var name in path)
            {
                menu = menu?.controls
                    .FirstOrDefault(c => c.type == VRCExpressionsMenu.Control.ControlType.SubMenu && c.name == name && c.subMenu != null)
                    ?.subMenu;
            }
            return menu;
        }
    }

    internal class AvatarPresetsBuildHook : IVRCSDKPreprocessAvatarCallback
    {
        // After VRCFury/NDMF (around -10000) so their generated parameters exist, before the SDK strips IEditorOnly components at -1024.
        public int callbackOrder => -1100;

        public bool OnPreprocessAvatar(GameObject avatarGameObject)
        {
            var all = avatarGameObject.GetComponentsInChildren<AvatarPresets>(true);
            if (all.Length == 0) return true;
            if (all.Length > 1)
            {
                Debug.LogError("Avatar Presets: only one Avatar Presets component is allowed per avatar.", avatarGameObject);
                return false;
            }

            var presets = all[0];
            var avatar = avatarGameObject.GetComponent<VRCAvatarDescriptor>();
            if (avatar != null && AvatarPresetsInstallMenuHook.InstallMenuPaths.TryGetValue(presets, out var installPath))
            {
                // The component is destroyed below, so pointing it at the built menu does not touch the scene.
                var built = AvatarPresetsInstallMenuHook.Resolve(avatar.expressionsMenu, installPath);
                if (built != null) presets.installMenu = built;
            }
            AvatarPresetsInstallMenuHook.InstallMenuPaths.Clear();
            var error = AvatarPresetsBuilder.Validate(presets, avatar, true);
            if (error != null)
            {
                Debug.LogError("Avatar Presets: " + error, avatarGameObject);
                return false;
            }
            AvatarPresetsBuilder.Apply(presets, avatar, true);
            Object.DestroyImmediate(presets);
            return true;
        }
    }

    [InitializeOnLoad]
    internal static class AvatarPresetsPlayMode
    {
        static AvatarPresetsPlayMode()
        {
            AvatarPresets.applyInPlayMode = Apply;
        }

        // Play mode changes are discarded on exit, so the generated assets stay in memory.
        static void Apply(AvatarPresets presets)
        {
            var avatar = presets.GetComponentInParent<VRCAvatarDescriptor>(true);
            var error = AvatarPresetsBuilder.Validate(presets, avatar, true);
            if (error != null)
            {
                Debug.LogError("Avatar Presets: " + error, presets);
                return;
            }
            AvatarPresetsBuilder.Apply(presets, avatar, false);
            Object.Destroy(presets);
        }
    }
}

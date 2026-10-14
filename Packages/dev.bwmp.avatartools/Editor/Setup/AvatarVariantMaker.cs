using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.Core;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace AvatarTools.Editor
{
    public class AvatarVariantMaker : EditorWindow
    {
        VRCAvatarDescriptor avatar;
        string newName = "";
        bool copyClips;
        bool clearBlueprintId = true;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Setup/Avatar Variant Maker")]
        static void Open()
        {
            GetWindow<AvatarVariantMaker>("Variant Maker");
        }

        void OnEnable()
        {
            SetAvatar(AvatarToolsUtil.SelectedAvatar());
        }

        void SetAvatar(VRCAvatarDescriptor value)
        {
            avatar = value;
            if (avatar != null) newName = avatar.gameObject.name + " Variant";
        }

        void OnGUI()
        {
            var picked = AvatarToolsUtil.AvatarField(avatar);
            if (picked != null && EditorUtility.IsPersistent(picked)) picked = null;
            if (picked != avatar) SetAvatar(picked);
            if (avatar == null)
            {
                EditorGUILayout.HelpBox("Pick an avatar in the scene.", MessageType.Info);
                return;
            }
            newName = EditorGUILayout.TextField("New Name", newName);
            copyClips = EditorGUILayout.ToggleLeft("Copy animation clips (controllers point to the copies)", copyClips);
            clearBlueprintId = EditorGUILayout.ToggleLeft("Clear blueprint ID on the copy (upload as a new avatar)", clearBlueprintId);

            var controllers = AvatarControllers(avatar).ToList();
            var menus = AvatarToolsUtil.AllMenus(avatar.expressionsMenu).ToList();
            var clips = controllers.OfType<AnimatorController>().SelectMany(AvatarToolsUtil.AllClips).Distinct().Count();
            var folder = AvatarToolsUtil.GeneratedRoot + "/" + AvatarToolsUtil.SafeName(newName);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Will create", EditorStyles.boldLabel);
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.LabelField("Scene object", string.IsNullOrWhiteSpace(newName) ? "-" : newName.Trim());
                EditorGUILayout.LabelField("Asset folder", folder);
                EditorGUILayout.LabelField("Controllers", controllers.Count == 0 ? "none" : string.Join(", ", controllers.Select(c => c.name)));
                EditorGUILayout.LabelField("Menus", menus.Count.ToString());
                EditorGUILayout.LabelField("Parameters", avatar.expressionParameters != null ? avatar.expressionParameters.name : "none");
                EditorGUILayout.LabelField("Clips", copyClips ? clips.ToString() : "shared with original");
            }
            if (string.IsNullOrWhiteSpace(newName))
            {
                EditorGUILayout.HelpBox("Enter a name for the variant.", MessageType.Warning);
                return;
            }
            if (AssetDatabase.IsValidFolder(folder))
                EditorGUILayout.HelpBox("The asset folder already exists. New assets will get unique names alongside the existing ones.", MessageType.Warning);
            if (PrefabUtility.IsPartOfPrefabInstance(avatar))
                EditorGUILayout.HelpBox("The copy will be an unpacked scene object, not a prefab instance.", MessageType.None);
            if (GUILayout.Button("Create Variant", GUILayout.Height(30)))
            {
                var variant = CreateVariant(avatar, newName.Trim(), copyClips, clearBlueprintId);
                Selection.activeGameObject = variant.gameObject;
                EditorGUIUtility.PingObject(variant.gameObject);
            }
        }

        static IEnumerable<RuntimeAnimatorController> AvatarControllers(VRCAvatarDescriptor avatar)
        {
            return (avatar.baseAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
                .Concat(avatar.specialAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
                .Where(l => !l.isDefault && l.animatorController != null)
                .Select(l => l.animatorController).Distinct();
        }

        public static VRCAvatarDescriptor CreateVariant(VRCAvatarDescriptor source, string newName, bool copyClips, bool clearBlueprintId)
        {
            var folder = AvatarToolsUtil.EnsureFolder(AvatarToolsUtil.GeneratedRoot + "/" + AvatarToolsUtil.SafeName(newName));

            var controllerCopies = new Dictionary<RuntimeAnimatorController, RuntimeAnimatorController>();
            foreach (var controller in AvatarControllers(source))
                controllerCopies[controller] = CopyAsset(controller, folder + "/Controllers");
            if (copyClips)
            {
                var motionCopies = new Dictionary<Motion, Motion>();
                foreach (var controller in controllerCopies.Values.OfType<AnimatorController>())
                    RemapMotions(controller, motionCopies, folder + "/Clips");
            }

            var menuCopies = new Dictionary<VRCExpressionsMenu, VRCExpressionsMenu>();
            foreach (var menu in AvatarToolsUtil.AllMenus(source.expressionsMenu))
                menuCopies[menu] = CopyAsset(menu, folder + "/Menus");
            foreach (var copy in menuCopies.Values)
            {
                foreach (var control in copy.controls)
                    if (control.subMenu != null && menuCopies.TryGetValue(control.subMenu, out var subCopy))
                        control.subMenu = subCopy;
                EditorUtility.SetDirty(copy);
            }
            var parameters = source.expressionParameters != null ? CopyAsset(source.expressionParameters, folder) : null;

            var go = UnityEngine.Object.Instantiate(source.gameObject, source.transform.parent);
            go.name = newName;
            go.transform.SetSiblingIndex(source.transform.GetSiblingIndex() + 1);
            Undo.RegisterCreatedObjectUndo(go, "Create Avatar Variant");

            var descriptor = go.GetComponent<VRCAvatarDescriptor>();
            Undo.RecordObject(descriptor, "Create Avatar Variant");
            descriptor.baseAnimationLayers = RemapLayers(descriptor.baseAnimationLayers, controllerCopies);
            descriptor.specialAnimationLayers = RemapLayers(descriptor.specialAnimationLayers, controllerCopies);
            if (descriptor.expressionsMenu != null) descriptor.expressionsMenu = menuCopies[descriptor.expressionsMenu];
            descriptor.expressionParameters = parameters;
            EditorUtility.SetDirty(descriptor);

            if (clearBlueprintId)
            {
                var pipeline = go.GetComponent<PipelineManager>();
                if (pipeline != null)
                {
                    Undo.RecordObject(pipeline, "Create Avatar Variant");
                    pipeline.blueprintId = "";
                    EditorUtility.SetDirty(pipeline);
                }
            }
            AssetDatabase.SaveAssets();
            return descriptor;
        }

        static VRCAvatarDescriptor.CustomAnimLayer[] RemapLayers(VRCAvatarDescriptor.CustomAnimLayer[] layers,
            Dictionary<RuntimeAnimatorController, RuntimeAnimatorController> copies)
        {
            if (layers == null) return null;
            var result = (VRCAvatarDescriptor.CustomAnimLayer[])layers.Clone();
            for (var i = 0; i < result.Length; i++)
                if (result[i].animatorController != null && copies.TryGetValue(result[i].animatorController, out var copy))
                    result[i].animatorController = copy;
            return result;
        }

        static T CopyAsset<T>(T asset, string folder) where T : UnityEngine.Object
        {
            AvatarToolsUtil.EnsureFolder(folder);
            var sourcePath = AssetDatabase.GetAssetPath(asset);
            if (AssetDatabase.IsMainAsset(asset) && !sourcePath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
            {
                var target = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + Path.GetFileName(sourcePath));
                if (!AssetDatabase.CopyAsset(sourcePath, target))
                    throw new InvalidOperationException($"Could not copy {sourcePath} to {target}.");
                return AssetDatabase.LoadAssetAtPath<T>(target);
            }
            var clone = UnityEngine.Object.Instantiate(asset);
            clone.name = asset.name;
            var extension = asset is AnimationClip ? "anim" : "asset";
            return AvatarToolsUtil.CreateAsset(clone, folder, asset.name, extension);
        }

        // Clips and blend trees embedded in the controller were duplicated by CopyAsset; only external motions need copying.
        // SDK package clips (proxy animations) stay shared.
        static void RemapMotions(AnimatorController controller, Dictionary<Motion, Motion> copies, string folder)
        {
            var controllerPath = AssetDatabase.GetAssetPath(controller);
            var remappedTrees = new HashSet<BlendTree>();
            Motion Remap(Motion motion)
            {
                if (motion == null) return null;
                var path = AssetDatabase.GetAssetPath(motion);
                if (path.StartsWith("Packages/", StringComparison.Ordinal)) return motion;
                if (motion is BlendTree tree)
                {
                    if (path != controllerPath)
                    {
                        if (copies.TryGetValue(tree, out var copied)) return copied;
                        copies[tree] = copied = CopyAsset(tree, folder);
                        tree = (BlendTree)copied;
                    }
                    if (!remappedTrees.Add(tree)) return tree;
                    var children = tree.children;
                    for (var i = 0; i < children.Length; i++) children[i].motion = Remap(children[i].motion);
                    tree.children = children;
                    EditorUtility.SetDirty(tree);
                    return tree;
                }
                if (path == controllerPath) return motion;
                if (!copies.TryGetValue(motion, out var copy)) copies[motion] = copy = CopyAsset(motion, folder);
                return copy;
            }
            foreach (var state in controller.layers.SelectMany(l => AvatarToolsUtil.AllStates(l.stateMachine)))
                state.motion = Remap(state.motion);
            EditorUtility.SetDirty(controller);
        }
    }
}

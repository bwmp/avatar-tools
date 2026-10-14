using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using AnimLayerType = VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.AnimLayerType;

namespace AvatarTools.Editor
{
    public class MaterialSwapGenerator : EditorWindow
    {
        public const int MaxMaterials = 255;

        VRCAvatarDescriptor avatar;
        Renderer renderer;
        int slot;
        readonly List<Material> materials = new List<Material>();
        string swapName = "";
        int defaultIndex;
        bool saved = true;
        string parentMenu = "";
        Vector2 scroll;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Animator/Material Swap Generator")]
        static void Open()
        {
            var window = GetWindow<MaterialSwapGenerator>("Material Swap");
            window.avatar = AvatarToolsUtil.SelectedAvatar();
        }

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            avatar = AvatarToolsUtil.AvatarField(avatar);
            var newRenderer = (Renderer)EditorGUILayout.ObjectField("Renderer", renderer, typeof(Renderer), true);
            if (newRenderer != renderer)
            {
                renderer = newRenderer;
                slot = 0;
                ResetMaterials();
            }
            if (renderer != null && renderer.sharedMaterials.Length > 0)
            {
                var slots = renderer.sharedMaterials;
                var newSlot = EditorGUILayout.Popup("Material Slot", Mathf.Clamp(slot, 0, slots.Length - 1),
                    slots.Select((m, i) => $"{i}: {(m == null ? "None" : m.name)}").ToArray());
                if (newSlot != slot)
                {
                    slot = newSlot;
                    ResetMaterials();
                }
            }
            swapName = EditorGUILayout.TextField("Swap Name", swapName);
            saved = EditorGUILayout.Toggle("Saved", saved);
            parentMenu = EditorGUILayout.TextField("Parent Submenu (optional)", parentMenu);
            EditorGUILayout.Space();
            AnimatorToolsShared.ObjectList("Materials (index = parameter value)", materials, null);
            if (materials.Count > 0)
                defaultIndex = EditorGUILayout.Popup("Default", Mathf.Clamp(defaultIndex, 0, materials.Count - 1),
                    materials.Select((m, i) => $"{i}: {(m == null ? "None" : m.name)}").ToArray());
            EditorGUILayout.Space();
            var error = Validate(avatar, renderer, slot, materials, swapName);
            AnimatorToolsShared.HelpIfAny(error);
            using (new EditorGUI.DisabledScope(error != null))
                if (GUILayout.Button("Generate Material Swap", GUILayout.Height(30)))
                {
                    Generate(avatar, renderer, slot, materials, swapName, defaultIndex, saved, parentMenu);
                    ShowNotification(new GUIContent("Generated " + swapName.Trim()));
                }
            EditorGUILayout.EndScrollView();
        }

        void ResetMaterials()
        {
            materials.Clear();
            defaultIndex = 0;
            if (renderer != null && slot < renderer.sharedMaterials.Length)
                materials.Add(renderer.sharedMaterials[slot]);
        }

        public static string Validate(VRCAvatarDescriptor avatar, Renderer renderer, int slot, IList<Material> materials, string name)
        {
            var error = AnimatorToolsShared.ValidateAvatar(avatar);
            if (error != null) return error;
            if (renderer == null) return "Assign a renderer.";
            if (!renderer.transform.IsChildOf(avatar.transform)) return "The renderer is not under the avatar.";
            if (slot < 0 || slot >= renderer.sharedMaterials.Length) return "The material slot does not exist on the renderer.";
            if (string.IsNullOrWhiteSpace(name)) return "Enter a swap name.";
            if (materials.Count < 2) return "Add at least two materials.";
            if (materials.Count > MaxMaterials) return $"At most {MaxMaterials} materials are supported.";
            if (materials.Any(m => m == null)) return "Every material entry must be assigned.";
            return AnimatorToolsShared.ValidateAnimatorParameter(avatar, name.Trim(), AnimatorControllerParameterType.Int)
                ?? AnimatorToolsShared.ValidateExpressionParameters(avatar, new[] { (name.Trim(), VRCExpressionParameters.ValueType.Int) });
        }

        public static void Generate(VRCAvatarDescriptor avatar, Renderer renderer, int slot, IList<Material> materials, string name,
            int defaultIndex, bool saved, string parentMenu)
        {
            var error = Validate(avatar, renderer, slot, materials, name);
            if (error != null) throw new InvalidOperationException(error);
            name = name.Trim();
            defaultIndex = Mathf.Clamp(defaultIndex, 0, materials.Count - 1);
            var binding = EditorCurveBinding.PPtrCurve(AvatarToolsUtil.GetPath(avatar.transform, renderer.transform),
                renderer.GetType(), $"m_Materials.Array.data[{slot}]");
            var folder = AvatarToolsUtil.AvatarFolder(avatar, "MaterialSwaps");
            var writeDefaults = AvatarToolsUtil.DetectWriteDefaults(avatar);
            var fx = AvatarToolsUtil.GetOrCreateLayerController(avatar, AnimLayerType.FX);
            AnimatorToolsShared.SetAnimatorParameter(fx, name, AnimatorControllerParameterType.Int, defaultIndex);
            var machine = AvatarToolsUtil.ReplaceLayer(fx, name).stateMachine;
            var parameters = AvatarToolsUtil.GetOrCreateParameters(avatar);
            AvatarToolsUtil.AddExpressionParameter(parameters, name, VRCExpressionParameters.ValueType.Int, defaultIndex, saved);
            var menu = AvatarToolsUtil.GetOrCreateSubMenu(AnimatorToolsShared.TargetMenu(avatar, parentMenu), name);
            foreach (var page in AvatarToolsUtil.AllMenus(menu).ToList())
            {
                Undo.RecordObject(page, "Material Swap Menu");
                page.controls.RemoveAll(c => c.type == VRCExpressionsMenu.Control.ControlType.Toggle && c.parameter != null && c.parameter.name == name);
                EditorUtility.SetDirty(page);
            }

            for (var i = 0; i < materials.Count; i++)
            {
                var label = $"{i} {materials[i].name}";
                var clip = new AnimationClip { name = name + " " + label };
                AnimationUtility.SetObjectReferenceCurve(clip, binding, new[]
                {
                    new ObjectReferenceKeyframe { time = 0, value = materials[i] },
                    new ObjectReferenceKeyframe { time = AnimatorToolsShared.FrameTime, value = materials[i] }
                });
                clip = AnimatorToolsShared.SaveOrReplace(clip, folder, $"{name}_{i}_{materials[i].name}", "anim");

                var state = AvatarToolsUtil.AddState(machine, label, clip, new Vector3(300, i * 60), writeDefaults);
                if (i == defaultIndex) machine.defaultState = state;
                var transition = machine.AddAnyStateTransition(state);
                transition.hasExitTime = false;
                transition.duration = 0;
                transition.canTransitionToSelf = false;
                transition.AddCondition(AnimatorConditionMode.Equals, i, name);

                var controlName = materials.Count(m => m.name == materials[i].name) > 1 ? label : materials[i].name;
                AvatarToolsUtil.AddMenuControl(menu,
                    AnimatorToolsShared.Control(controlName, VRCExpressionsMenu.Control.ControlType.Toggle, name, i));
            }
            AnimatorToolsShared.Finish(fx);
        }
    }
}

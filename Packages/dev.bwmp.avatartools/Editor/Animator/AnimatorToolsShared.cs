using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using AnimLayerType = VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.AnimLayerType;
using ValueType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;

namespace AvatarTools.Editor
{
    internal static class AnimatorToolsShared
    {
        public const float FrameTime = 1f / 60f;

        public static string ValidateAvatar(VRCAvatarDescriptor avatar)
        {
            if (avatar == null) return "Assign an avatar with a VRC Avatar Descriptor.";
            foreach (var layer in avatar.baseAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
                if (layer.type == AnimLayerType.FX && !layer.isDefault && layer.animatorController != null && !(layer.animatorController is AnimatorController))
                    return "The FX layer uses an Animator Override Controller; assign a regular Animator Controller first.";
            return null;
        }

        public static string ValidateAnimatorParameter(VRCAvatarDescriptor avatar, string name, AnimatorControllerParameterType type)
        {
            var fx = AvatarToolsUtil.GetLayerController(avatar, AnimLayerType.FX);
            var existing = fx == null ? null : fx.parameters.FirstOrDefault(p => p.name == name);
            return existing == null || existing.type == type ? null
                : $"FX parameter \"{name}\" already exists as {existing.type}, expected {type}.";
        }

        public static string ValidateExpressionParameters(VRCAvatarDescriptor avatar, IEnumerable<(string name, ValueType type)> wanted)
        {
            var parameters = avatar.expressionParameters;
            var current = parameters == null ? Array.Empty<VRCExpressionParameters.Parameter>() : parameters.parameters ?? Array.Empty<VRCExpressionParameters.Parameter>();
            var cost = current.Where(p => p.networkSynced && !string.IsNullOrEmpty(p.name)).Sum(p => VRCExpressionParameters.TypeCost(p.valueType));
            var added = 0;
            foreach (var (name, type) in wanted)
            {
                var existing = current.FirstOrDefault(p => p.name == name);
                if (existing == null) added += VRCExpressionParameters.TypeCost(type);
                else if (existing.networkSynced) added += VRCExpressionParameters.TypeCost(type) - VRCExpressionParameters.TypeCost(existing.valueType);
                else added += VRCExpressionParameters.TypeCost(type);
            }
            return cost + added > VRCExpressionParameters.MAX_PARAMETER_COST
                ? $"Not enough synced parameter memory: {cost} used + {added} needed > {VRCExpressionParameters.MAX_PARAMETER_COST}."
                : null;
        }

        public static void SetAnimatorParameter(AnimatorController controller, string name, AnimatorControllerParameterType type, float defaultValue)
        {
            AvatarToolsUtil.EnsureAnimatorParameter(controller, name, type, defaultValue);
            var parameters = controller.parameters;
            foreach (var p in parameters.Where(p => p.name == name))
            {
                p.defaultBool = defaultValue != 0;
                p.defaultFloat = defaultValue;
                p.defaultInt = (int)defaultValue;
            }
            controller.parameters = parameters;
        }

        public static T SaveOrReplace<T>(T asset, string folder, string name, string extension) where T : UnityEngine.Object
        {
            var path = $"{folder}/{AvatarToolsUtil.SafeName(name)}.{extension}";
            var existing = AssetDatabase.LoadAssetAtPath<T>(path);
            if (existing == null)
            {
                AssetDatabase.CreateAsset(asset, path);
                return asset;
            }
            EditorUtility.CopySerialized(asset, existing);
            UnityEngine.Object.DestroyImmediate(asset);
            EditorUtility.SetDirty(existing);
            return existing;
        }

        public static AnimationClip ConstantClip(string name, IEnumerable<(string path, Type type, string property, float value)> curves)
        {
            var clip = new AnimationClip { name = name };
            foreach (var (path, type, property, value) in curves)
                clip.SetCurve(path, type, property, AnimationCurve.Constant(0, FrameTime, value));
            return clip;
        }

        public static VRCExpressionsMenu TargetMenu(VRCAvatarDescriptor avatar, string subMenu)
        {
            var root = AvatarToolsUtil.GetOrCreateMenu(avatar);
            return string.IsNullOrWhiteSpace(subMenu) ? root : AvatarToolsUtil.GetOrCreateSubMenu(root, subMenu.Trim());
        }

        public static VRCExpressionsMenu.Control Control(string name, VRCExpressionsMenu.Control.ControlType type, string parameter, float value = 1, string subParameter = null)
        {
            return new VRCExpressionsMenu.Control
            {
                name = name, type = type, value = value,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = parameter ?? "" },
                subParameters = subParameter == null
                    ? Array.Empty<VRCExpressionsMenu.Control.Parameter>()
                    : new[] { new VRCExpressionsMenu.Control.Parameter { name = subParameter } },
                labels = Array.Empty<VRCExpressionsMenu.Control.Label>()
            };
        }

        public static void Finish(AnimatorController controller)
        {
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
        }

        public static void HelpIfAny(string message, MessageType type = MessageType.Error)
        {
            if (!string.IsNullOrEmpty(message)) EditorGUILayout.HelpBox(message, type);
        }

        public static void ObjectList<T>(string label, List<T> items, Action addSelected) where T : UnityEngine.Object
        {
            EditorGUILayout.LabelField(label, EditorStyles.boldLabel);
            for (var i = 0; i < items.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    items[i] = (T)EditorGUILayout.ObjectField(items[i], typeof(T), true);
                    if (GUILayout.Button("Remove", GUILayout.Width(70)))
                    {
                        items.RemoveAt(i);
                        GUIUtility.ExitGUI();
                    }
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Add Empty")) items.Add(null);
                if (addSelected != null && GUILayout.Button("Add Selected")) addSelected();
            }
        }
    }
}

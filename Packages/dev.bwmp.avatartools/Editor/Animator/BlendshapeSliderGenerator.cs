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
    public class BlendshapeSliderGenerator : EditorWindow
    {
        VRCAvatarDescriptor avatar;
        SkinnedMeshRenderer renderer;
        readonly HashSet<string> selected = new HashSet<string>();
        string search = "";
        string parameterPrefix = "BS/";
        bool saved = true;
        string subMenu = "";
        Vector2 listScroll;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Animator/Blendshape Slider Generator")]
        static void Open()
        {
            var window = GetWindow<BlendshapeSliderGenerator>("Blendshape Sliders");
            window.avatar = AvatarToolsUtil.SelectedAvatar();
        }

        void OnGUI()
        {
            avatar = AvatarToolsUtil.AvatarField(avatar);
            var newRenderer = (SkinnedMeshRenderer)EditorGUILayout.ObjectField("Mesh", renderer, typeof(SkinnedMeshRenderer), true);
            if (newRenderer != renderer)
            {
                renderer = newRenderer;
                selected.Clear();
            }
            parameterPrefix = EditorGUILayout.TextField("Parameter Prefix", parameterPrefix);
            saved = EditorGUILayout.Toggle("Saved", saved);
            subMenu = EditorGUILayout.TextField("Submenu (optional)", subMenu);
            EditorGUILayout.Space();

            var names = ShapeNames(renderer);
            if (names.Count > 0)
            {
                search = EditorGUILayout.TextField("Search", search);
                var visible = names.Where(n => string.IsNullOrEmpty(search) || n.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0).ToList();
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"{selected.Count} selected", GUILayout.Width(100));
                    if (GUILayout.Button("Select Shown")) selected.UnionWith(visible);
                    if (GUILayout.Button("Clear")) selected.Clear();
                }
                listScroll = EditorGUILayout.BeginScrollView(listScroll, GUILayout.MinHeight(150), GUILayout.ExpandHeight(true));
                foreach (var shape in visible)
                {
                    var on = EditorGUILayout.ToggleLeft(shape, selected.Contains(shape));
                    if (on) selected.Add(shape);
                    else selected.Remove(shape);
                }
                EditorGUILayout.EndScrollView();
            }

            var error = Validate(avatar, renderer, selected.ToList(), parameterPrefix);
            AnimatorToolsShared.HelpIfAny(error);
            using (new EditorGUI.DisabledScope(error != null))
                if (GUILayout.Button($"Generate {selected.Count} Slider(s)", GUILayout.Height(30)))
                {
                    Generate(avatar, renderer, names.Where(selected.Contains).ToList(), parameterPrefix, saved, subMenu);
                    ShowNotification(new GUIContent($"Generated {selected.Count} slider(s)"));
                }
        }

        static List<string> ShapeNames(SkinnedMeshRenderer renderer)
        {
            var mesh = renderer == null ? null : renderer.sharedMesh;
            return mesh == null ? new List<string>() : Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToList();
        }

        public static string Validate(VRCAvatarDescriptor avatar, SkinnedMeshRenderer renderer, IList<string> shapes, string prefix)
        {
            var error = AnimatorToolsShared.ValidateAvatar(avatar);
            if (error != null) return error;
            if (renderer == null) return "Assign a Skinned Mesh Renderer.";
            if (!renderer.transform.IsChildOf(avatar.transform)) return "The mesh is not under the avatar.";
            var names = ShapeNames(renderer);
            if (names.Count == 0) return "The mesh has no blendshapes.";
            if (shapes.Count == 0) return "Select at least one blendshape.";
            var missing = shapes.FirstOrDefault(s => !names.Contains(s));
            if (missing != null) return $"Blendshape \"{missing}\" does not exist on the mesh.";
            foreach (var shape in shapes)
            {
                var paramError = AnimatorToolsShared.ValidateAnimatorParameter(avatar, ParameterName(prefix, shape), AnimatorControllerParameterType.Float);
                if (paramError != null) return paramError;
            }
            return AnimatorToolsShared.ValidateExpressionParameters(avatar,
                shapes.Select(s => (ParameterName(prefix, s), VRCExpressionParameters.ValueType.Float)));
        }

        public static string ParameterName(string prefix, string shape) => (prefix ?? "") + shape;

        public static void Generate(VRCAvatarDescriptor avatar, SkinnedMeshRenderer renderer, IList<string> shapes, string prefix, bool saved, string subMenu)
        {
            var error = Validate(avatar, renderer, shapes, prefix);
            if (error != null) throw new InvalidOperationException(error);
            var path = AvatarToolsUtil.GetPath(avatar.transform, renderer.transform);
            var mesh = renderer.sharedMesh;
            var folder = AvatarToolsUtil.AvatarFolder(avatar, "Blendshapes");
            var writeDefaults = AvatarToolsUtil.DetectWriteDefaults(avatar);
            var fx = AvatarToolsUtil.GetOrCreateLayerController(avatar, AnimLayerType.FX);
            var parameters = AvatarToolsUtil.GetOrCreateParameters(avatar);
            var menu = AnimatorToolsShared.TargetMenu(avatar, subMenu);

            foreach (var shape in shapes)
            {
                var parameter = ParameterName(prefix, shape);
                var current = renderer.GetBlendShapeWeight(mesh.GetBlendShapeIndex(shape)) / 100f;
                var property = "blendShape." + shape;
                var assetName = renderer.name + "_" + shape;
                var zero = AnimatorToolsShared.SaveOrReplace(AnimatorToolsShared.ConstantClip(shape + " 0",
                    new[] { (path, typeof(SkinnedMeshRenderer), property, 0f) }), folder, assetName + "_0", "anim");
                var full = AnimatorToolsShared.SaveOrReplace(AnimatorToolsShared.ConstantClip(shape + " 100",
                    new[] { (path, typeof(SkinnedMeshRenderer), property, 100f) }), folder, assetName + "_100", "anim");

                AnimatorToolsShared.SetAnimatorParameter(fx, parameter, AnimatorControllerParameterType.Float, current);
                var machine = AvatarToolsUtil.ReplaceLayer(fx, parameter).stateMachine;
                var tree = new BlendTree
                {
                    name = shape, blendType = BlendTreeType.Simple1D, blendParameter = parameter,
                    useAutomaticThresholds = false, hideFlags = HideFlags.HideInHierarchy
                };
                AssetDatabase.AddObjectToAsset(tree, fx);
                tree.AddChild(zero, 0);
                tree.AddChild(full, 1);
                machine.defaultState = AvatarToolsUtil.AddState(machine, shape, tree, new Vector3(300, 0), writeDefaults);

                AvatarToolsUtil.AddExpressionParameter(parameters, parameter, VRCExpressionParameters.ValueType.Float, current, saved);
                AvatarToolsUtil.AddMenuControl(menu,
                    AnimatorToolsShared.Control(shape, VRCExpressionsMenu.Control.ControlType.RadialPuppet, null, 1, parameter));
            }
            AnimatorToolsShared.Finish(fx);
        }
    }
}

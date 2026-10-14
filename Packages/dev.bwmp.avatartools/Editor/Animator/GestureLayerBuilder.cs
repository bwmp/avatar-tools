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
    public class GestureLayerBuilder : EditorWindow
    {
        public static readonly string[] Gestures = { "Neutral", "Fist", "HandOpen", "FingerPoint", "Victory", "RockNRoll", "HandGun", "ThumbsUp" };
        public const string DefaultLockParameter = "GestureLock";
        const int FistIndex = 1;

        VRCAvatarDescriptor avatar;
        [SerializeField] AnimationClip[] left = new AnimationClip[8];
        [SerializeField] AnimationClip[] right = new AnimationClip[8];
        float duration = 0.1f;
        bool fistWeight = true;
        bool useLock;
        string lockParameter = DefaultLockParameter;
        Vector2 scroll;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Animator/Gesture Layer Builder")]
        static void Open()
        {
            var window = GetWindow<GestureLayerBuilder>("Gesture Layers");
            window.avatar = AvatarToolsUtil.SelectedAvatar();
        }

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            avatar = AvatarToolsUtil.AvatarField(avatar);
            EditorGUILayout.Space();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Gesture", EditorStyles.boldLabel, GUILayout.Width(100));
                EditorGUILayout.LabelField("Left Hand", EditorStyles.boldLabel);
                EditorGUILayout.LabelField("Right Hand", EditorStyles.boldLabel);
            }
            for (var i = 0; i < Gestures.Length; i++)
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField($"{i} {Gestures[i]}", GUILayout.Width(100));
                    left[i] = (AnimationClip)EditorGUILayout.ObjectField(left[i], typeof(AnimationClip), false);
                    right[i] = (AnimationClip)EditorGUILayout.ObjectField(right[i], typeof(AnimationClip), false);
                }
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.Space(104);
                if (GUILayout.Button("Copy Left to Right")) right = (AnimationClip[])left.Clone();
                if (GUILayout.Button("Copy Right to Left")) left = (AnimationClip[])right.Clone();
            }
            EditorGUILayout.Space();
            duration = Mathf.Max(0, EditorGUILayout.FloatField("Transition Duration (s)", duration));
            fistWeight = EditorGUILayout.Toggle(new GUIContent("Analog Fist", "Blend Neutral to Fist using GestureLeftWeight/GestureRightWeight."), fistWeight);
            useLock = EditorGUILayout.Toggle(new GUIContent("Gesture Lock", "Adds a menu toggle that holds the current expression while on."), useLock);
            if (useLock) lockParameter = EditorGUILayout.TextField("Lock Parameter", lockParameter);
            if (fistWeight && ((left[FistIndex] != null && left[0] == null) || (right[FistIndex] != null && right[0] == null)))
                EditorGUILayout.HelpBox("Analog Fist needs a Neutral clip to blend from; hands without one use the Fist clip directly.", MessageType.Info);
            var error = Validate(avatar, left, right, useLock ? lockParameter : null);
            AnimatorToolsShared.HelpIfAny(error);
            using (new EditorGUI.DisabledScope(error != null))
                if (GUILayout.Button("Build Gesture Layers", GUILayout.Height(30)))
                {
                    Generate(avatar, left, right, duration, fistWeight, useLock ? lockParameter : null);
                    ShowNotification(new GUIContent("Gesture layers built"));
                }
            EditorGUILayout.EndScrollView();
        }

        public static string Validate(VRCAvatarDescriptor avatar, AnimationClip[] left, AnimationClip[] right, string lockParameter)
        {
            var error = AnimatorToolsShared.ValidateAvatar(avatar);
            if (error != null) return error;
            if (left == null || right == null || left.Length != Gestures.Length || right.Length != Gestures.Length)
                return "Each hand needs exactly 8 gesture slots.";
            if (left.All(c => c == null) && right.All(c => c == null)) return "Assign at least one gesture clip.";
            if (lockParameter != null && string.IsNullOrWhiteSpace(lockParameter)) return "Enter a lock parameter name.";
            var checks = new List<(string, AnimatorControllerParameterType)>
            {
                ("GestureLeft", AnimatorControllerParameterType.Int), ("GestureRight", AnimatorControllerParameterType.Int),
                ("GestureLeftWeight", AnimatorControllerParameterType.Float), ("GestureRightWeight", AnimatorControllerParameterType.Float)
            };
            if (lockParameter != null) checks.Add((lockParameter.Trim(), AnimatorControllerParameterType.Bool));
            foreach (var (name, type) in checks)
            {
                var paramError = AnimatorToolsShared.ValidateAnimatorParameter(avatar, name, type);
                if (paramError != null) return paramError;
            }
            return lockParameter == null ? null
                : AnimatorToolsShared.ValidateExpressionParameters(avatar, new[] { (lockParameter.Trim(), VRCExpressionParameters.ValueType.Bool) });
        }

        public static void Generate(VRCAvatarDescriptor avatar, AnimationClip[] left, AnimationClip[] right, float duration,
            bool fistWeight, string lockParameter)
        {
            var error = Validate(avatar, left, right, lockParameter);
            if (error != null) throw new InvalidOperationException(error);
            lockParameter = lockParameter?.Trim();
            var writeDefaults = AvatarToolsUtil.DetectWriteDefaults(avatar);
            var fx = AvatarToolsUtil.GetOrCreateLayerController(avatar, AnimLayerType.FX);
            if (lockParameter != null)
            {
                AvatarToolsUtil.EnsureAnimatorParameter(fx, lockParameter, AnimatorControllerParameterType.Bool);
                AvatarToolsUtil.AddExpressionParameter(AvatarToolsUtil.GetOrCreateParameters(avatar), lockParameter,
                    VRCExpressionParameters.ValueType.Bool, 0, false);
                AvatarToolsUtil.AddMenuControl(AvatarToolsUtil.GetOrCreateMenu(avatar),
                    AnimatorToolsShared.Control("Gesture Lock", VRCExpressionsMenu.Control.ControlType.Toggle, lockParameter));
            }
            if (left.Any(c => c != null))
                BuildHand(fx, "Left Hand", "GestureLeft", left, duration, fistWeight, lockParameter, writeDefaults);
            if (right.Any(c => c != null))
                BuildHand(fx, "Right Hand", "GestureRight", right, duration, fistWeight, lockParameter, writeDefaults);
            AnimatorToolsShared.Finish(fx);
        }

        static void BuildHand(AnimatorController fx, string layerName, string gestureParameter, AnimationClip[] clips, float duration,
            bool fistWeight, string lockParameter, bool writeDefaults)
        {
            var weightParameter = gestureParameter + "Weight";
            AvatarToolsUtil.EnsureAnimatorParameter(fx, gestureParameter, AnimatorControllerParameterType.Int);
            AvatarToolsUtil.EnsureAnimatorParameter(fx, weightParameter, AnimatorControllerParameterType.Float);
            var machine = AvatarToolsUtil.ReplaceLayer(fx, layerName).stateMachine;
            var neutral = clips[0];
            for (var i = 0; i < Gestures.Length; i++)
            {
                Motion motion = clips[i] != null ? clips[i] : neutral;
                if (i == FistIndex && fistWeight && clips[i] != null && neutral != null)
                {
                    var tree = new BlendTree
                    {
                        name = layerName + " Fist", blendType = BlendTreeType.Simple1D, blendParameter = weightParameter,
                        useAutomaticThresholds = false, hideFlags = HideFlags.HideInHierarchy
                    };
                    AssetDatabase.AddObjectToAsset(tree, fx);
                    tree.AddChild(neutral, 0);
                    tree.AddChild(clips[i], 1);
                    motion = tree;
                }
                var state = AvatarToolsUtil.AddState(machine, Gestures[i], motion, new Vector3(300, i * 60), writeDefaults);
                if (i == 0) machine.defaultState = state;
                var transition = machine.AddAnyStateTransition(state);
                transition.hasExitTime = false;
                transition.hasFixedDuration = true;
                transition.duration = duration;
                transition.canTransitionToSelf = false;
                transition.AddCondition(AnimatorConditionMode.Equals, i, gestureParameter);
                if (lockParameter != null) transition.AddCondition(AnimatorConditionMode.IfNot, 0, lockParameter);
            }
        }
    }
}

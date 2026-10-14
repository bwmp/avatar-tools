using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDKBase;
using AnimLayerType = VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.AnimLayerType;
using TrackingType = VRC.SDKBase.VRC_AnimatorTrackingControl.TrackingType;

namespace AvatarTools.Editor
{
    public class TrackingControlHelper : EditorWindow
    {
        static readonly HashSet<string> GestureParameters = new HashSet<string> { "GestureLeft", "GestureRight", "GestureLeftWeight", "GestureRightWeight" };

        public struct BulkItem
        {
            public AnimatorController controller;
            public AnimatorState state;
            public string label;
            public TrackingType target;
        }

        VRCAvatarDescriptor avatar;
        int layerIndex;
        string search = "";
        AnimatorState selected;
        string selectedPath;
        List<BulkItem> bulkPreview;
        Vector2 scroll;
        Vector2 stateScroll;
        Vector2 bulkScroll;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Animator/Tracking & Layer Control Helper")]
        static void Open()
        {
            var window = GetWindow<TrackingControlHelper>("Tracking Control");
            window.avatar = AvatarToolsUtil.SelectedAvatar();
        }

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            var newAvatar = AvatarToolsUtil.AvatarField(avatar);
            if (newAvatar != avatar)
            {
                avatar = newAvatar;
                selected = null;
                bulkPreview = null;
            }
            if (avatar == null)
            {
                EditorGUILayout.HelpBox("Assign an avatar with a VRC Avatar Descriptor.", MessageType.Error);
                EditorGUILayout.EndScrollView();
                return;
            }
            var layers = AnalysisUtil.PlayableLayers(avatar);
            if (layers.Count == 0)
            {
                EditorGUILayout.HelpBox("The avatar has no custom playable layer controllers.", MessageType.Warning);
                EditorGUILayout.EndScrollView();
                return;
            }

            var newLayer = EditorGUILayout.Popup("Playable Layer", Mathf.Clamp(layerIndex, 0, layers.Count - 1),
                layers.Select(l => $"{l.type}: {l.controller.name}").ToArray());
            if (newLayer != layerIndex)
            {
                layerIndex = newLayer;
                selected = null;
            }
            var controller = layers[Mathf.Clamp(layerIndex, 0, layers.Count - 1)].controller;

            DrawStatePicker(controller);
            EditorGUILayout.Space();
            if (selected != null) DrawState(controller, selected);
            else EditorGUILayout.HelpBox("Select a state to add or edit its behaviours.", MessageType.Info);

            EditorGUILayout.Space(12);
            DrawBulk();
            EditorGUILayout.EndScrollView();
        }

        void DrawStatePicker(AnimatorController controller)
        {
            search = EditorGUILayout.TextField("Search States", search);
            var states = controller.layers
                .SelectMany(l => AnalysisUtil.StatesWithPaths(l.stateMachine, l.name + "/"))
                .Where(s => string.IsNullOrEmpty(search) || s.path.IndexOf(search, StringComparison.OrdinalIgnoreCase) >= 0)
                .ToList();
            stateScroll = EditorGUILayout.BeginScrollView(stateScroll, EditorStyles.helpBox, GUILayout.Height(160));
            if (states.Count == 0) EditorGUILayout.LabelField("No matching states.", EditorStyles.miniLabel);
            foreach (var (state, path) in states)
            {
                var label = state.behaviours.Length > 0 ? $"{path}  ({state.behaviours.Length})" : path;
                if (GUILayout.Toggle(state == selected, label, EditorStyles.label) && state != selected)
                {
                    selected = state;
                    selectedPath = path;
                }
            }
            EditorGUILayout.EndScrollView();
        }

        void DrawState(AnimatorController controller, AnimatorState state)
        {
            EditorGUILayout.LabelField(selectedPath, EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Face Expression")) ApplyFaceExpression(controller, state);
                if (GUILayout.Button("Restore Tracking")) RestoreTracking(controller, state);
                if (GUILayout.Button("Disable Locomotion")) SetLocomotion(controller, state, true);
            }
            EditorGUILayout.LabelField("Face Expression sets eyes and mouth to Animation. Restore Tracking sets every body part to Tracking.", EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Behaviours", EditorStyles.boldLabel);
            if (state.behaviours.Length == 0) EditorGUILayout.LabelField("None", EditorStyles.miniLabel);
            foreach (var behaviour in state.behaviours)
            {
                if (behaviour == null) continue;
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        EditorGUILayout.LabelField(behaviour.GetType().Name, EditorStyles.boldLabel);
                        if (GUILayout.Button("Remove", GUILayout.Width(70)))
                        {
                            RemoveBehaviour(controller, state, behaviour);
                            GUIUtility.ExitGUI();
                        }
                    }
                    if (behaviour is VRC_AnimatorTrackingControl tracking) DrawTracking(tracking);
                    else if (behaviour is VRC_AnimatorLayerControl layerControl) DrawLayerControl(layerControl);
                    else if (behaviour is VRC_AnimatorLocomotionControl locomotion) DrawLocomotion(locomotion);
                }
            }
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(state.behaviours.OfType<VRC_AnimatorTrackingControl>().Any()))
                    if (GUILayout.Button("Add Tracking Control")) GetOrAddBehaviour<VRCAnimatorTrackingControl>(controller, state);
                if (GUILayout.Button("Add Layer Control")) AddBehaviour<VRCAnimatorLayerControl>(controller, state);
                using (new EditorGUI.DisabledScope(state.behaviours.OfType<VRC_AnimatorLocomotionControl>().Any()))
                    if (GUILayout.Button("Add Locomotion Control")) GetOrAddBehaviour<VRCAnimatorLocomotionControl>(controller, state);
            }
        }

        static void DrawTracking(VRC_AnimatorTrackingControl t)
        {
            EditorGUI.BeginChangeCheck();
            var head = (TrackingType)EditorGUILayout.EnumPopup("Head", t.trackingHead);
            var leftHand = (TrackingType)EditorGUILayout.EnumPopup("Left Hand", t.trackingLeftHand);
            var rightHand = (TrackingType)EditorGUILayout.EnumPopup("Right Hand", t.trackingRightHand);
            var hip = (TrackingType)EditorGUILayout.EnumPopup("Hip", t.trackingHip);
            var leftFoot = (TrackingType)EditorGUILayout.EnumPopup("Left Foot", t.trackingLeftFoot);
            var rightFoot = (TrackingType)EditorGUILayout.EnumPopup("Right Foot", t.trackingRightFoot);
            var leftFingers = (TrackingType)EditorGUILayout.EnumPopup("Left Fingers", t.trackingLeftFingers);
            var rightFingers = (TrackingType)EditorGUILayout.EnumPopup("Right Fingers", t.trackingRightFingers);
            var eyes = (TrackingType)EditorGUILayout.EnumPopup("Eyes & Eyelids", t.trackingEyes);
            var mouth = (TrackingType)EditorGUILayout.EnumPopup("Mouth & Jaw", t.trackingMouth);
            if (!EditorGUI.EndChangeCheck()) return;
            Undo.RecordObject(t, "Edit Tracking Control");
            t.trackingHead = head;
            t.trackingLeftHand = leftHand;
            t.trackingRightHand = rightHand;
            t.trackingHip = hip;
            t.trackingLeftFoot = leftFoot;
            t.trackingRightFoot = rightFoot;
            t.trackingLeftFingers = leftFingers;
            t.trackingRightFingers = rightFingers;
            t.trackingEyes = eyes;
            t.trackingMouth = mouth;
            EditorUtility.SetDirty(t);
        }

        void DrawLayerControl(VRC_AnimatorLayerControl c)
        {
            EditorGUI.BeginChangeCheck();
            var playable = (VRC_AnimatorLayerControl.BlendableLayer)EditorGUILayout.EnumPopup("Playable", c.playable);
            var target = AvatarToolsUtil.GetLayerController(avatar, (AnimLayerType)Enum.Parse(typeof(AnimLayerType), playable.ToString()));
            int layer;
            if (target != null && target.layers.Length > 0)
                layer = EditorGUILayout.Popup("Layer", Mathf.Clamp(c.layer, 0, target.layers.Length - 1),
                    target.layers.Select((l, i) => $"{i}: {l.name}").ToArray());
            else
                layer = EditorGUILayout.IntField("Layer Index", c.layer);
            var goalWeight = EditorGUILayout.Slider("Goal Weight", c.goalWeight, 0, 1);
            var blendDuration = Mathf.Max(0, EditorGUILayout.FloatField("Blend Duration", c.blendDuration));
            if (!EditorGUI.EndChangeCheck()) return;
            Undo.RecordObject(c, "Edit Layer Control");
            c.playable = playable;
            c.layer = Mathf.Max(0, layer);
            c.goalWeight = goalWeight;
            c.blendDuration = blendDuration;
            EditorUtility.SetDirty(c);
        }

        static void DrawLocomotion(VRC_AnimatorLocomotionControl c)
        {
            EditorGUI.BeginChangeCheck();
            var disable = EditorGUILayout.Toggle("Disable Locomotion", c.disableLocomotion);
            if (!EditorGUI.EndChangeCheck()) return;
            Undo.RecordObject(c, "Edit Locomotion Control");
            c.disableLocomotion = disable;
            EditorUtility.SetDirty(c);
        }

        void DrawBulk()
        {
            EditorGUILayout.LabelField("Bulk: Gesture Face Tracking", EditorStyles.boldLabel);
            EditorGUILayout.LabelField("For layers in the Gesture and FX controllers that use GestureLeft/GestureRight: states whose motion animates blendshapes get eyes and mouth set to Animation; states without blendshape animation get Tracking.", EditorStyles.wordWrappedMiniLabel);
            if (GUILayout.Button("Preview")) bulkPreview = BulkPreview(avatar);
            if (bulkPreview == null) return;
            if (bulkPreview.Count == 0)
            {
                EditorGUILayout.HelpBox("Every gesture state is already configured.", MessageType.Info);
                return;
            }
            bulkScroll = EditorGUILayout.BeginScrollView(bulkScroll, EditorStyles.helpBox, GUILayout.MaxHeight(200));
            foreach (var item in bulkPreview)
                EditorGUILayout.LabelField($"{item.label}  ->  {item.target}", EditorStyles.miniLabel);
            EditorGUILayout.EndScrollView();
            if (GUILayout.Button($"Apply to {bulkPreview.Count} States", GUILayout.Height(30)) &&
                EditorUtility.DisplayDialog("Apply Tracking Controls", $"Set eye and mouth tracking on {bulkPreview.Count} states?", "Apply", "Cancel"))
            {
                BulkApply(bulkPreview);
                bulkPreview = null;
                ShowNotification(new GUIContent("Tracking controls updated"));
            }
        }

        public static T GetOrAddBehaviour<T>(AnimatorController controller, AnimatorState state) where T : StateMachineBehaviour
        {
            return state.behaviours.OfType<T>().FirstOrDefault() ?? AddBehaviour<T>(controller, state);
        }

        public static T AddBehaviour<T>(AnimatorController controller, AnimatorState state) where T : StateMachineBehaviour
        {
            Undo.RecordObject(state, "Add " + typeof(T).Name);
            var behaviour = state.AddStateMachineBehaviour<T>();
            Undo.RegisterCreatedObjectUndo(behaviour, "Add " + typeof(T).Name);
            EditorUtility.SetDirty(state);
            AnimatorToolsShared.Finish(controller);
            return behaviour;
        }

        public static void RemoveBehaviour(AnimatorController controller, AnimatorState state, StateMachineBehaviour behaviour)
        {
            Undo.RecordObject(state, "Remove " + behaviour.GetType().Name);
            state.behaviours = state.behaviours.Where(b => b != behaviour).ToArray();
            Undo.DestroyObjectImmediate(behaviour);
            EditorUtility.SetDirty(state);
            AnimatorToolsShared.Finish(controller);
        }

        public static void ApplyFaceExpression(AnimatorController controller, AnimatorState state)
        {
            SetFaceTracking(controller, state, TrackingType.Animation);
        }

        public static void SetFaceTracking(AnimatorController controller, AnimatorState state, TrackingType type)
        {
            var t = GetOrAddBehaviour<VRCAnimatorTrackingControl>(controller, state);
            Undo.RecordObject(t, "Set Face Tracking");
            t.trackingEyes = type;
            t.trackingMouth = type;
            EditorUtility.SetDirty(t);
            AnimatorToolsShared.Finish(controller);
        }

        public static void RestoreTracking(AnimatorController controller, AnimatorState state)
        {
            var t = GetOrAddBehaviour<VRCAnimatorTrackingControl>(controller, state);
            Undo.RecordObject(t, "Restore Tracking");
            t.trackingHead = t.trackingLeftHand = t.trackingRightHand = t.trackingHip = t.trackingLeftFoot = t.trackingRightFoot =
                t.trackingLeftFingers = t.trackingRightFingers = t.trackingEyes = t.trackingMouth = TrackingType.Tracking;
            EditorUtility.SetDirty(t);
            AnimatorToolsShared.Finish(controller);
        }

        public static void SetLocomotion(AnimatorController controller, AnimatorState state, bool disable)
        {
            var c = GetOrAddBehaviour<VRCAnimatorLocomotionControl>(controller, state);
            Undo.RecordObject(c, "Set Locomotion Control");
            c.disableLocomotion = disable;
            EditorUtility.SetDirty(c);
            AnimatorToolsShared.Finish(controller);
        }

        public static List<BulkItem> BulkPreview(VRCAvatarDescriptor avatar)
        {
            var items = new List<BulkItem>();
            foreach (var (type, controller) in AnalysisUtil.PlayableLayers(avatar))
            {
                if (type != AnimLayerType.Gesture && type != AnimLayerType.FX) continue;
                foreach (var layer in controller.layers.Where(l => l.stateMachine != null && UsesGestures(l.stateMachine)))
                foreach (var (state, path) in AnalysisUtil.StatesWithPaths(layer.stateMachine, ""))
                {
                    var target = AnimatesBlendShapes(state.motion) ? TrackingType.Animation : TrackingType.Tracking;
                    var existing = state.behaviours.OfType<VRC_AnimatorTrackingControl>().FirstOrDefault();
                    if (existing != null && existing.trackingEyes == target && existing.trackingMouth == target) continue;
                    items.Add(new BulkItem { controller = controller, state = state, label = $"{type}/{layer.name}/{path}", target = target });
                }
            }
            return items;
        }

        public static void BulkApply(IEnumerable<BulkItem> items)
        {
            Undo.SetCurrentGroupName("Bulk Tracking Controls");
            var group = Undo.GetCurrentGroup();
            foreach (var item in items)
                if (item.state != null) SetFaceTracking(item.controller, item.state, item.target);
            Undo.CollapseUndoOperations(group);
        }

        static bool UsesGestures(AnimatorStateMachine machine)
        {
            return AnalysisUtil.AllMachines(machine).Any(m =>
                AnalysisUtil.MachineTransitions(m).Concat(m.states.SelectMany(s => s.state.transitions))
                    .Any(t => t != null && t.conditions.Any(c => GestureParameters.Contains(c.parameter))));
        }

        static bool AnimatesBlendShapes(Motion motion)
        {
            return AvatarToolsUtil.ClipsInMotion(motion).Any(clip =>
                AnimationUtility.GetCurveBindings(clip).Any(b => b.type == typeof(SkinnedMeshRenderer) && b.propertyName.StartsWith("blendShape.")));
        }
    }
}

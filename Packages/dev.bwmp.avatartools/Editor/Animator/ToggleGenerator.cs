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
    public class ToggleGenerator : EditorWindow
    {
        VRCAvatarDescriptor avatar;
        readonly List<GameObject> objects = new List<GameObject>();
        readonly List<GameObject> inverseObjects = new List<GameObject>();
        string toggleName = "";
        bool defaultOn;
        bool saved = true;
        string subMenu = "";
        Vector2 scroll;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Animator/Toggle Generator")]
        static void Open()
        {
            var window = GetWindow<ToggleGenerator>("Toggle Generator");
            window.avatar = AvatarToolsUtil.SelectedAvatar();
        }

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            avatar = AvatarToolsUtil.AvatarField(avatar);
            toggleName = EditorGUILayout.TextField("Toggle Name", toggleName);
            defaultOn = EditorGUILayout.Toggle("Default On", defaultOn);
            saved = EditorGUILayout.Toggle("Saved", saved);
            subMenu = EditorGUILayout.TextField("Submenu (optional)", subMenu);
            EditorGUILayout.Space();
            AnimatorToolsShared.ObjectList("Objects (on when toggle is on)", objects, () => AddSelected(objects));
            EditorGUILayout.Space();
            AnimatorToolsShared.ObjectList("Inverse Objects (on when toggle is off)", inverseObjects, () => AddSelected(inverseObjects));
            EditorGUILayout.Space();
            var error = Validate(avatar, toggleName, objects, inverseObjects);
            AnimatorToolsShared.HelpIfAny(error);
            using (new EditorGUI.DisabledScope(error != null))
                if (GUILayout.Button("Generate Toggle", GUILayout.Height(30)))
                {
                    Generate(avatar, toggleName, objects, inverseObjects, defaultOn, saved, subMenu);
                    ShowNotification(new GUIContent("Generated " + toggleName.Trim()));
                }
            EditorGUILayout.EndScrollView();
        }

        void AddSelected(List<GameObject> list)
        {
            foreach (var go in Selection.gameObjects)
                if (!list.Contains(go)) list.Add(go);
        }

        public static string Validate(VRCAvatarDescriptor avatar, string name, IList<GameObject> objects, IList<GameObject> inverse)
        {
            var error = AnimatorToolsShared.ValidateAvatar(avatar);
            if (error != null) return error;
            if (string.IsNullOrWhiteSpace(name)) return "Enter a toggle name.";
            var all = objects.Concat(inverse ?? Array.Empty<GameObject>()).Where(o => o != null).ToList();
            if (all.Count == 0) return "Add at least one object.";
            foreach (var go in all)
            {
                if (go.transform == avatar.transform) return "The avatar root cannot be toggled.";
                if (!go.transform.IsChildOf(avatar.transform)) return $"\"{go.name}\" is not under the avatar.";
            }
            if (all.Count != all.Distinct().Count()) return "An object is listed more than once.";
            return AnimatorToolsShared.ValidateAnimatorParameter(avatar, name.Trim(), AnimatorControllerParameterType.Bool)
                ?? AnimatorToolsShared.ValidateExpressionParameters(avatar, new[] { (name.Trim(), VRCExpressionParameters.ValueType.Bool) });
        }

        public static void Generate(VRCAvatarDescriptor avatar, string name, IList<GameObject> objects, IList<GameObject> inverse,
            bool defaultOn, bool saved, string subMenu)
        {
            var error = Validate(avatar, name, objects, inverse);
            if (error != null) throw new InvalidOperationException(error);
            name = name.Trim();
            var root = avatar.transform;
            var paths = objects.Where(o => o != null).Select(o => AvatarToolsUtil.GetPath(root, o.transform)).ToList();
            var inversePaths = (inverse ?? Array.Empty<GameObject>()).Where(o => o != null).Select(o => AvatarToolsUtil.GetPath(root, o.transform)).ToList();
            var folder = AvatarToolsUtil.AvatarFolder(avatar, "Toggles");
            AnimationClip MakeClip(bool on) => AnimatorToolsShared.SaveOrReplace(AnimatorToolsShared.ConstantClip(name + (on ? " On" : " Off"),
                paths.Select(p => (p, typeof(GameObject), "m_IsActive", on ? 1f : 0f))
                    .Concat(inversePaths.Select(p => (p, typeof(GameObject), "m_IsActive", on ? 0f : 1f)))), folder, name + "_" + (on ? "On" : "Off"), "anim");
            var onClip = MakeClip(true);
            var offClip = MakeClip(false);

            var writeDefaults = AvatarToolsUtil.DetectWriteDefaults(avatar);
            var fx = AvatarToolsUtil.GetOrCreateLayerController(avatar, AnimLayerType.FX);
            AnimatorToolsShared.SetAnimatorParameter(fx, name, AnimatorControllerParameterType.Bool, defaultOn ? 1 : 0);
            var machine = AvatarToolsUtil.ReplaceLayer(fx, name).stateMachine;
            var offState = AvatarToolsUtil.AddState(machine, "Off", offClip, new Vector3(300, 0), writeDefaults);
            var onState = AvatarToolsUtil.AddState(machine, "On", onClip, new Vector3(300, 100), writeDefaults);
            machine.defaultState = defaultOn ? onState : offState;
            AvatarToolsUtil.AddInstantTransition(offState, onState).AddCondition(AnimatorConditionMode.If, 0, name);
            AvatarToolsUtil.AddInstantTransition(onState, offState).AddCondition(AnimatorConditionMode.IfNot, 0, name);

            AvatarToolsUtil.AddExpressionParameter(AvatarToolsUtil.GetOrCreateParameters(avatar), name,
                VRCExpressionParameters.ValueType.Bool, defaultOn ? 1 : 0, saved);
            AvatarToolsUtil.AddMenuControl(AnimatorToolsShared.TargetMenu(avatar, subMenu),
                AnimatorToolsShared.Control(name, VRCExpressionsMenu.Control.ControlType.Toggle, name));
            AnimatorToolsShared.Finish(fx);
        }
    }
}

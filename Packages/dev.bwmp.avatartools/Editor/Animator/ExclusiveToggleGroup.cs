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
    public class ExclusiveToggleGroup : EditorWindow
    {
        public const int MaxOptions = 256;

        [Serializable]
        public class Option
        {
            public string name = "";
            public List<GameObject> objects = new List<GameObject>();
        }

        VRCAvatarDescriptor avatar;
        string groupName = "";
        List<Option> options = new List<Option> { new Option { name = "None" }, new Option() };
        int defaultIndex;
        bool saved = true;
        string parentMenu = "";
        Vector2 scroll;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Animator/Exclusive Toggle Group")]
        static void Open()
        {
            var window = GetWindow<ExclusiveToggleGroup>("Exclusive Toggles");
            window.avatar = AvatarToolsUtil.SelectedAvatar();
        }

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            avatar = AvatarToolsUtil.AvatarField(avatar);
            groupName = EditorGUILayout.TextField("Group Name", groupName);
            saved = EditorGUILayout.Toggle("Saved", saved);
            parentMenu = EditorGUILayout.TextField("Parent Submenu (optional)", parentMenu);
            EditorGUILayout.HelpBox("Each option enables its objects and disables every object in the other options. An option with no objects turns everything off.", MessageType.Info);

            for (var i = 0; i < options.Count; i++)
            {
                var option = options[i];
                EditorGUILayout.Space();
                using (new EditorGUILayout.HorizontalScope())
                {
                    option.name = EditorGUILayout.TextField($"Option {i}", option.name);
                    using (new EditorGUI.DisabledScope(i == 0))
                        if (GUILayout.Button("Up", GUILayout.Width(40)))
                        {
                            (options[i - 1], options[i]) = (options[i], options[i - 1]);
                            GUIUtility.ExitGUI();
                        }
                    if (GUILayout.Button("Remove", GUILayout.Width(70)))
                    {
                        options.RemoveAt(i);
                        GUIUtility.ExitGUI();
                    }
                }
                using (new EditorGUI.IndentLevelScope())
                    AnimatorToolsShared.ObjectList("Objects", option.objects, () => AddSelected(option.objects));
            }
            EditorGUILayout.Space();
            if (GUILayout.Button("Add Option")) options.Add(new Option());
            if (options.Count > 0)
                defaultIndex = EditorGUILayout.Popup("Default", Mathf.Clamp(defaultIndex, 0, options.Count - 1),
                    options.Select((o, i) => $"{i}: {o.name}").ToArray());

            EditorGUILayout.Space();
            var error = Validate(avatar, groupName, options);
            AnimatorToolsShared.HelpIfAny(error);
            using (new EditorGUI.DisabledScope(error != null))
                if (GUILayout.Button("Generate Exclusive Toggles", GUILayout.Height(30)))
                {
                    Generate(avatar, groupName, options, defaultIndex, saved, parentMenu);
                    ShowNotification(new GUIContent("Generated " + groupName.Trim()));
                }
            EditorGUILayout.EndScrollView();
        }

        static void AddSelected(List<GameObject> list)
        {
            foreach (var go in Selection.gameObjects)
                if (!list.Contains(go)) list.Add(go);
        }

        public static string Validate(VRCAvatarDescriptor avatar, string name, IList<Option> options)
        {
            var error = AnimatorToolsShared.ValidateAvatar(avatar);
            if (error != null) return error;
            if (string.IsNullOrWhiteSpace(name)) return "Enter a group name.";
            if (options.Count < 2) return "Add at least two options.";
            if (options.Count > MaxOptions) return $"At most {MaxOptions} options are supported.";
            if (options.Any(o => string.IsNullOrWhiteSpace(o.name))) return "Every option needs a name.";
            var duplicateName = options.GroupBy(o => o.name.Trim()).FirstOrDefault(g => g.Count() > 1);
            if (duplicateName != null) return $"Option name \"{duplicateName.Key}\" is used more than once.";
            var owner = new Dictionary<GameObject, int>();
            for (var i = 0; i < options.Count; i++)
            {
                foreach (var go in options[i].objects.Where(o => o != null))
                {
                    if (go.transform == avatar.transform) return "The avatar root cannot be toggled.";
                    if (!go.transform.IsChildOf(avatar.transform)) return $"\"{go.name}\" is not under the avatar.";
                    if (owner.TryGetValue(go, out var other))
                        return other == i
                            ? $"\"{go.name}\" is listed more than once in option \"{options[i].name}\"."
                            : $"\"{go.name}\" is in both \"{options[other].name}\" and \"{options[i].name}\".";
                    owner[go] = i;
                }
            }
            if (owner.Count == 0) return "Add at least one object.";
            return AnimatorToolsShared.ValidateAnimatorParameter(avatar, name.Trim(), AnimatorControllerParameterType.Int)
                ?? AnimatorToolsShared.ValidateExpressionParameters(avatar, new[] { (name.Trim(), VRCExpressionParameters.ValueType.Int) });
        }

        public static void Generate(VRCAvatarDescriptor avatar, string name, IList<Option> options, int defaultIndex, bool saved, string parentMenu)
        {
            var error = Validate(avatar, name, options);
            if (error != null) throw new InvalidOperationException(error);
            name = name.Trim();
            defaultIndex = Mathf.Clamp(defaultIndex, 0, options.Count - 1);
            var root = avatar.transform;
            var optionPaths = options.Select(o => o.objects.Where(go => go != null)
                .Select(go => AvatarToolsUtil.GetPath(root, go.transform)).ToList()).ToList();
            var allPaths = optionPaths.SelectMany(p => p).ToList();
            var folder = AvatarToolsUtil.AvatarFolder(avatar, "ExclusiveToggles");

            var writeDefaults = AvatarToolsUtil.DetectWriteDefaults(avatar);
            var fx = AvatarToolsUtil.GetOrCreateLayerController(avatar, AnimLayerType.FX);
            AnimatorToolsShared.SetAnimatorParameter(fx, name, AnimatorControllerParameterType.Int, defaultIndex);
            var machine = AvatarToolsUtil.ReplaceLayer(fx, name).stateMachine;
            AvatarToolsUtil.AddExpressionParameter(AvatarToolsUtil.GetOrCreateParameters(avatar), name,
                VRCExpressionParameters.ValueType.Int, defaultIndex, saved);
            var menu = AvatarToolsUtil.GetOrCreateSubMenu(AnimatorToolsShared.TargetMenu(avatar, parentMenu), name);
            foreach (var page in AvatarToolsUtil.AllMenus(menu).ToList())
            {
                Undo.RecordObject(page, "Exclusive Toggle Menu");
                page.controls.RemoveAll(c => c.type == VRCExpressionsMenu.Control.ControlType.Toggle && c.parameter != null && c.parameter.name == name);
                EditorUtility.SetDirty(page);
            }

            for (var i = 0; i < options.Count; i++)
            {
                var optionName = options[i].name.Trim();
                var enabled = new HashSet<string>(optionPaths[i]);
                var clip = AnimatorToolsShared.SaveOrReplace(AnimatorToolsShared.ConstantClip($"{name} {optionName}",
                        allPaths.Select(p => (p, typeof(GameObject), "m_IsActive", enabled.Contains(p) ? 1f : 0f))),
                    folder, $"{name}_{i}_{optionName}", "anim");

                var state = AvatarToolsUtil.AddState(machine, $"{i} {optionName}", clip, new Vector3(300, i * 60), writeDefaults);
                if (i == defaultIndex) machine.defaultState = state;
                var transition = machine.AddAnyStateTransition(state);
                transition.hasExitTime = false;
                transition.duration = 0;
                transition.canTransitionToSelf = false;
                transition.AddCondition(AnimatorConditionMode.Equals, i, name);

                AvatarToolsUtil.AddMenuControl(menu,
                    AnimatorToolsShared.Control(optionName, VRCExpressionsMenu.Control.ControlType.Toggle, name, i));
            }
            AnimatorToolsShared.Finish(fx);
        }
    }
}

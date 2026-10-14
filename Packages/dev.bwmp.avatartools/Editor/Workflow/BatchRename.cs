using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;

namespace AvatarTools.Editor
{
    public class BatchRename : EditorWindow
    {
        const string NumberToken = "{n}";
        const string ParameterUndoName = "Rename Avatar Parameter";

        public enum CaseMode { Keep, Upper, Lower, Title }

        [Serializable]
        public class NameRule
        {
            public bool Trim = true;
            public string Find = "";
            public string Replace = "";
            public bool Regex;
            public CaseMode Case;
            public string Prefix = "";
            public string Suffix = "";
            public int NumberStart = 1;
            public int NumberPadding = 2;
        }

        public class ParameterReference
        {
            public UnityEngine.Object Owner;
            public string Location;
            public string OldName;
            public string NewName;
            public bool ReadOnly;
        }

        static readonly string[] PhysBoneSuffixes =
        {
            VRCPhysBoneBase.PARAM_ISGRABBED, VRCPhysBoneBase.PARAM_ISPOSED, VRCPhysBoneBase.PARAM_ANGLE,
            VRCPhysBoneBase.PARAM_STRETCH, VRCPhysBoneBase.PARAM_SQUISH
        };

        int tab;
        [SerializeField] NameRule rule = new NameRule();
        VRCAvatarDescriptor parameterAvatar;
        string oldParameter = "";
        string newParameter = "";
        Vector2 scroll;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Workflow/Batch Rename")]
        static void Open()
        {
            GetWindow<BatchRename>("Batch Rename");
        }

        void OnEnable()
        {
            if (parameterAvatar == null) parameterAvatar = AvatarToolsUtil.SelectedAvatar();
        }

        void OnSelectionChange()
        {
            Repaint();
        }

        void OnHierarchyChange()
        {
            Repaint();
        }

        public static string RenameOne(string name, NameRule rule, int index)
        {
            var result = rule.Trim ? name.Trim() : name;
            if (!string.IsNullOrEmpty(rule.Find))
            {
                var replace = ExpandNumber(rule.Replace ?? "", rule, index);
                result = rule.Regex ? System.Text.RegularExpressions.Regex.Replace(result, rule.Find, replace) : result.Replace(rule.Find, replace);
            }
            switch (rule.Case)
            {
                case CaseMode.Upper: result = result.ToUpperInvariant(); break;
                case CaseMode.Lower: result = result.ToLowerInvariant(); break;
                case CaseMode.Title: result = System.Globalization.CultureInfo.InvariantCulture.TextInfo.ToTitleCase(result.ToLowerInvariant()); break;
            }
            result = ExpandNumber(rule.Prefix ?? "", rule, index) + result + ExpandNumber(rule.Suffix ?? "", rule, index);
            return rule.Trim ? result.Trim() : result;
        }

        static string ExpandNumber(string text, NameRule rule, int index)
        {
            return text.Replace(NumberToken, (rule.NumberStart + index).ToString().PadLeft(Mathf.Max(0, rule.NumberPadding), '0'));
        }

        static string HierarchyKey(Transform t)
        {
            var parts = new List<string>();
            for (var p = t; p != null; p = p.parent) parts.Add(p.GetSiblingIndex().ToString("D6"));
            parts.Add(t.gameObject.scene.handle.ToString("D10"));
            parts.Reverse();
            return string.Join("/", parts);
        }

        public static List<Transform> OrderedTargets(IEnumerable<Transform> targets)
        {
            return targets.Where(t => t != null && !EditorUtility.IsPersistent(t)).Distinct().OrderBy(HierarchyKey, StringComparer.Ordinal).ToList();
        }

        public static List<BoneNameRemapper.Rename> PreviewObjects(IList<Transform> targets, NameRule rule)
        {
            var newNames = new Dictionary<Transform, string>();
            for (var i = 0; i < targets.Count; i++) newNames[targets[i]] = RenameOne(targets[i].name, rule, i);
            string FinalName(Transform t) => newNames.TryGetValue(t, out var n) ? n : t.name;

            var result = new List<BoneNameRemapper.Rename>();
            foreach (var t in targets)
            {
                var siblings = t.parent != null ? t.parent.Cast<Transform>() : t.gameObject.scene.GetRootGameObjects().Select(g => g.transform);
                var newName = newNames[t];
                result.Add(new BoneNameRemapper.Rename
                {
                    Target = t, OldName = t.name, NewName = newName,
                    Collides = siblings.Any(s => s != t && FinalName(s) == newName)
                });
            }
            return result;
        }

        public static bool IsValidObjectName(string name)
        {
            return !string.IsNullOrWhiteSpace(name) && !name.Contains("/");
        }

        public static Dictionary<string, string> BuildParameterMap(VRCAvatarDescriptor avatar, string oldName, string newName)
        {
            var map = new Dictionary<string, string> { [oldName] = newName };
            if (avatar.GetComponentsInChildren<VRCPhysBoneBase>(true).Any(b => b.parameter == oldName))
                foreach (var suffix in PhysBoneSuffixes)
                    map[oldName + suffix] = newName + suffix;
            return map;
        }

        public static HashSet<string> ExistingParameterNames(VRCAvatarDescriptor avatar)
        {
            var names = new HashSet<string>(AnalysisUtil.BuiltInParameters);
            if (avatar.expressionParameters != null && avatar.expressionParameters.parameters != null)
                names.UnionWith(avatar.expressionParameters.parameters.Where(p => p != null).Select(p => p.name));
            foreach (var (_, controller) in AnalysisUtil.PlayableLayers(avatar))
            {
                names.UnionWith(controller.parameters.Select(p => p.name));
                names.UnionWith(AnalysisUtil.ControllerReferences(controller));
            }
            foreach (var menu in AvatarToolsUtil.AllMenus(avatar.expressionsMenu))
            foreach (var control in menu.controls)
            {
                if (control.parameter != null) names.Add(control.parameter.name);
                if (control.subParameters != null) names.UnionWith(control.subParameters.Where(p => p != null).Select(p => p.name));
            }
            names.UnionWith(AnalysisUtil.ComponentParameters(avatar));
            foreach (var bone in avatar.GetComponentsInChildren<VRCPhysBoneBase>(true)) names.Add(bone.parameter);
            names.Remove("");
            names.Remove(null);
            return names;
        }

        public static string ValidateParameterRename(VRCAvatarDescriptor avatar, string oldName, string newName)
        {
            if (string.IsNullOrEmpty(oldName)) return "Enter the parameter to rename.";
            if (string.IsNullOrWhiteSpace(newName)) return "Enter the new name.";
            if (oldName == newName) return "The new name is the same as the old one.";
            if (AnalysisUtil.BuiltInParameters.Contains(oldName)) return $"\"{oldName}\" is a built-in VRChat parameter and cannot be renamed.";
            var existing = ExistingParameterNames(avatar);
            var taken = BuildParameterMap(avatar, oldName, newName).Values.Where(existing.Contains).ToList();
            return taken.Count > 0 ? "Already used on this avatar: " + string.Join(", ", taken) : null;
        }

        class ParameterWalker
        {
            readonly Dictionary<string, string> map;
            readonly bool apply;
            readonly HashSet<UnityEngine.Object> visited = new HashSet<UnityEngine.Object>();
            public readonly List<ParameterReference> References = new List<ParameterReference>();

            public ParameterWalker(Dictionary<string, string> map, bool apply)
            {
                this.map = map;
                this.apply = apply;
            }

            static bool IsReadOnly(UnityEngine.Object owner)
            {
                return EditorUtility.IsPersistent(owner) && AssetDatabase.GetAssetPath(owner).StartsWith("Packages/", StringComparison.Ordinal);
            }

            bool Rename(UnityEngine.Object owner, string location, string current, Action<string> set)
            {
                if (current == null || !map.TryGetValue(current, out var renamed)) return false;
                var readOnly = IsReadOnly(owner);
                References.Add(new ParameterReference { Owner = owner, Location = location, OldName = current, NewName = renamed, ReadOnly = readOnly });
                if (!apply || readOnly) return false;
                Undo.RecordObject(owner, ParameterUndoName);
                set(renamed);
                EditorUtility.SetDirty(owner);
                return true;
            }

            public void Walk(VRCAvatarDescriptor avatar)
            {
                var parameters = avatar.expressionParameters;
                if (parameters != null && parameters.parameters != null)
                    foreach (var p in parameters.parameters.Where(p => p != null))
                        Rename(parameters, $"Expression Parameters {parameters.name}", p.name, v => p.name = v);

                foreach (var menu in AvatarToolsUtil.AllMenus(avatar.expressionsMenu))
                foreach (var control in menu.controls)
                {
                    if (control.parameter != null)
                        Rename(menu, $"Menu {menu.name} / {control.name}: parameter", control.parameter.name, v => control.parameter.name = v);
                    if (control.subParameters == null) continue;
                    for (var i = 0; i < control.subParameters.Length; i++)
                    {
                        var sub = control.subParameters[i];
                        if (sub != null) Rename(menu, $"Menu {menu.name} / {control.name}: sub-parameter {i + 1}", sub.name, v => sub.name = v);
                    }
                }

                foreach (var (type, controller) in AnalysisUtil.PlayableLayers(avatar))
                    WalkController(type + " " + controller.name, controller);

                foreach (var bone in avatar.GetComponentsInChildren<VRCPhysBoneBase>(true))
                    Rename(bone, $"PhysBone on {AvatarToolsUtil.GetPath(avatar.transform, bone.transform)}", bone.parameter, v => bone.parameter = v);
                foreach (var receiver in avatar.GetComponentsInChildren<ContactReceiver>(true))
                    Rename(receiver, $"Contact Receiver on {AvatarToolsUtil.GetPath(avatar.transform, receiver.transform)}", receiver.parameter, v => receiver.parameter = v);
            }

            void WalkController(string label, AnimatorController controller)
            {
                var controllerParameters = controller.parameters;
                var changed = false;
                foreach (var p in controllerParameters)
                    changed |= Rename(controller, $"{label}: parameter list", p.name, v => p.name = v);
                if (changed) controller.parameters = controllerParameters;

                foreach (var layer in controller.layers)
                {
                    if (layer.stateMachine == null) continue;
                    var layerLabel = $"{label} / {layer.name}";
                    foreach (var machine in AnalysisUtil.AllMachines(layer.stateMachine))
                    {
                        foreach (var t in AnalysisUtil.MachineTransitions(machine)) WalkTransition($"{layerLabel} / {machine.name}", t);
                        foreach (var b in machine.behaviours) WalkBehaviour($"{layerLabel} / {machine.name}", b);
                        foreach (var child in machine.states)
                        {
                            var state = child.state;
                            var stateLabel = $"{layerLabel} / {state.name}";
                            foreach (var t in state.transitions) WalkTransition(stateLabel, t);
                            foreach (var b in state.behaviours) WalkBehaviour(stateLabel, b);
                            Rename(state, $"{stateLabel}: speed parameter", state.speedParameter, v => state.speedParameter = v);
                            Rename(state, $"{stateLabel}: time parameter", state.timeParameter, v => state.timeParameter = v);
                            Rename(state, $"{stateLabel}: mirror parameter", state.mirrorParameter, v => state.mirrorParameter = v);
                            Rename(state, $"{stateLabel}: cycle offset parameter", state.cycleOffsetParameter, v => state.cycleOffsetParameter = v);
                            WalkMotion(stateLabel, state.motion);
                            if (layer.syncedLayerIndex >= 0) WalkMotion(stateLabel + " (synced override)", layer.GetOverrideMotion(state));
                        }
                    }
                }
            }

            void WalkTransition(string label, AnimatorTransitionBase transition)
            {
                if (transition == null || !visited.Add(transition)) return;
                var conditions = transition.conditions;
                var changed = false;
                for (var i = 0; i < conditions.Length; i++)
                {
                    var index = i;
                    var target = transition.destinationState != null ? transition.destinationState.name
                        : transition.destinationStateMachine != null ? transition.destinationStateMachine.name : transition.isExit ? "Exit" : "?";
                    changed |= Rename(transition, $"{label} -> {target}: condition", conditions[i].parameter, v => conditions[index].parameter = v);
                }
                if (changed) transition.conditions = conditions;
            }

            void WalkBehaviour(string label, StateMachineBehaviour behaviour)
            {
                if (behaviour is VRC_AvatarParameterDriver driver)
                {
                    foreach (var p in driver.parameters)
                    {
                        Rename(driver, $"{label}: Parameter Driver destination", p.name, v => p.name = v);
                        Rename(driver, $"{label}: Parameter Driver source", p.source, v => p.source = v);
                    }
                }
                else if (behaviour is VRC_AnimatorPlayAudio audio)
                    Rename(audio, $"{label}: Play Audio parameter", audio.ParameterName, v => audio.ParameterName = v);
            }

            void WalkMotion(string label, Motion motion)
            {
                if (!(motion is BlendTree tree) || !visited.Add(tree)) return;
                var treeLabel = $"{label} / blend tree {tree.name}";
                Rename(tree, $"{treeLabel}: parameter", tree.blendParameter, v => tree.blendParameter = v);
                Rename(tree, $"{treeLabel}: parameter Y", tree.blendParameterY, v => tree.blendParameterY = v);
                var children = tree.children;
                var changed = false;
                for (var i = 0; i < children.Length; i++)
                {
                    var index = i;
                    changed |= Rename(tree, $"{treeLabel}: direct parameter {i + 1}", children[i].directBlendParameter, v => children[index].directBlendParameter = v);
                }
                if (changed) tree.children = children;
                foreach (var child in children) WalkMotion(treeLabel, child.motion);
            }
        }

        public static List<ParameterReference> FindParameterReferences(VRCAvatarDescriptor avatar, Dictionary<string, string> map)
        {
            var walker = new ParameterWalker(map, false);
            walker.Walk(avatar);
            return walker.References;
        }

        public static List<ParameterReference> RenameParameter(VRCAvatarDescriptor avatar, Dictionary<string, string> map)
        {
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName(ParameterUndoName);
            var walker = new ParameterWalker(map, true);
            walker.Walk(avatar);
            Undo.CollapseUndoOperations(group);
            AssetDatabase.SaveAssets();
            return walker.References;
        }

        void OnGUI()
        {
            tab = GUILayout.Toolbar(tab, new[] { "Objects", "Parameters" });
            EditorGUILayout.Space();
            if (tab == 0) ObjectsGUI();
            else ParametersGUI();
        }

        void ObjectsGUI()
        {
            rule.Trim = EditorGUILayout.ToggleLeft("Trim whitespace", rule.Trim);
            using (new EditorGUILayout.HorizontalScope())
            {
                rule.Find = EditorGUILayout.TextField("Find", rule.Find);
                rule.Regex = EditorGUILayout.ToggleLeft("Regex", rule.Regex, GUILayout.Width(60));
            }
            rule.Replace = EditorGUILayout.TextField("Replace", rule.Replace);
            rule.Case = (CaseMode)EditorGUILayout.EnumPopup("Case", rule.Case);
            rule.Prefix = EditorGUILayout.TextField("Prefix", rule.Prefix);
            rule.Suffix = EditorGUILayout.TextField("Suffix", rule.Suffix);
            using (new EditorGUILayout.HorizontalScope())
            {
                rule.NumberStart = EditorGUILayout.IntField(new GUIContent("Number Start", "{n} in Replace, Prefix or Suffix becomes the object's index in hierarchy order."), rule.NumberStart);
                rule.NumberPadding = Mathf.Clamp(EditorGUILayout.IntField("Padding", rule.NumberPadding), 0, 8);
            }

            var targets = OrderedTargets(Selection.gameObjects.Select(g => g.transform));
            if (targets.Count == 0)
            {
                EditorGUILayout.HelpBox("Select objects under one avatar in the Hierarchy.", MessageType.Info);
                return;
            }
            var avatars = targets.Select(t => AvatarToolsUtil.FindDescriptor(t.gameObject)).Distinct().ToList();
            if (avatars.Count != 1 || avatars[0] == null)
            {
                EditorGUILayout.HelpBox("All selected objects must belong to the same avatar.", MessageType.Warning);
                return;
            }
            var avatar = avatars[0];

            List<BoneNameRemapper.Rename> preview;
            try
            {
                preview = PreviewObjects(targets, rule);
            }
            catch (ArgumentException e)
            {
                EditorGUILayout.HelpBox("Invalid regex: " + e.Message, MessageType.Error);
                return;
            }
            var renames = preview.Where(r => r.NewName != r.OldName && IsValidObjectName(r.NewName)).ToList();
            var invalid = preview.Count(r => !IsValidObjectName(r.NewName));
            var collisions = renames.Count(r => r.Collides);

            EditorGUILayout.LabelField($"{targets.Count} selected on {avatar.name}, {renames.Count} to rename" +
                                       (collisions > 0 ? $", {collisions} sibling collision(s)" : "") + (invalid > 0 ? $", {invalid} invalid" : ""), EditorStyles.miniLabel);
            if (invalid > 0) EditorGUILayout.HelpBox("Empty names or names containing '/' are skipped.", MessageType.Warning);
            var humanAnimator = avatar.GetComponent<Animator>();
            if (humanAnimator != null && humanAnimator.isHuman && renames.Any(r => Enumerable.Range(0, (int)HumanBodyBones.LastBone)
                    .Any(b => humanAnimator.GetBoneTransform((HumanBodyBones)b) == r.Target)))
                EditorGUILayout.HelpBox("Some selected objects are humanoid bones. The rig maps bones by name and must be reconfigured after renaming them.", MessageType.Warning);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            var collisionColor = new Color(1f, 0.5f, 0.45f);
            foreach (var r in preview)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var previous = GUI.color;
                    if (r.Collides || !IsValidObjectName(r.NewName)) GUI.color = collisionColor;
                    EditorGUILayout.LabelField(r.OldName, GUILayout.MinWidth(100));
                    EditorGUILayout.LabelField("->", GUILayout.Width(20));
                    EditorGUILayout.LabelField(r.NewName == r.OldName ? "(unchanged)" : r.NewName, GUILayout.MinWidth(100));
                    GUI.color = previous;
                    if (GUILayout.Button("Ping", EditorStyles.miniButton, GUILayout.Width(40))) EditorGUIUtility.PingObject(r.Target);
                }
            }
            EditorGUILayout.EndScrollView();

            EditorGUILayout.HelpBox("Animation paths in the avatar's controllers are updated. Expression menu control names and parameters are not renamed.", MessageType.None);
            using (new EditorGUI.DisabledScope(renames.Count == 0))
            {
                if (!GUILayout.Button($"Rename {renames.Count} Object(s)", GUILayout.Height(30))) return;
                if (collisions > 0 && !EditorUtility.DisplayDialog("Name collisions",
                        $"{collisions} object(s) will share a name with a sibling, which makes animation paths ambiguous. Rename anyway?", "Rename", "Cancel"))
                    return;
                var result = BoneNameRemapper.Apply(renames, avatar);
                var message = $"Renamed {renames.Count} object(s), updated {result.updatedClips} clip(s).";
                if (result.skippedClips > 0) message += $" Skipped {result.skippedClips} read-only clip(s) (inside models or packages).";
                EditorUtility.DisplayDialog("Batch Rename", message + "\n\nExpression menu controls and parameters were not renamed.", "OK");
                GUIUtility.ExitGUI();
            }
        }

        void ParametersGUI()
        {
            var picked = AvatarToolsUtil.AvatarField(parameterAvatar);
            parameterAvatar = picked != null && EditorUtility.IsPersistent(picked) ? null : picked;
            if (parameterAvatar == null)
            {
                EditorGUILayout.HelpBox("Pick an avatar in the scene.", MessageType.Info);
                return;
            }

            var known = ExistingParameterNames(parameterAvatar).Where(n => !AnalysisUtil.BuiltInParameters.Contains(n)).OrderBy(n => n).ToArray();
            using (new EditorGUILayout.HorizontalScope())
            {
                oldParameter = EditorGUILayout.TextField("Parameter", oldParameter);
                var chosen = EditorGUILayout.Popup(Array.IndexOf(known, oldParameter), known, GUILayout.Width(150));
                if (chosen >= 0 && known[chosen] != oldParameter)
                {
                    oldParameter = known[chosen];
                    GUI.FocusControl(null);
                }
            }
            newParameter = EditorGUILayout.TextField("New Name", newParameter);

            var error = ValidateParameterRename(parameterAvatar, oldParameter, newParameter);
            if (error != null)
            {
                EditorGUILayout.HelpBox(error, MessageType.Warning);
                return;
            }
            var map = BuildParameterMap(parameterAvatar, oldParameter, newParameter);
            var references = FindParameterReferences(parameterAvatar, map);
            if (map.Count > 1)
                EditorGUILayout.HelpBox("A PhysBone uses this parameter, so its derived parameters are renamed too: " +
                                        string.Join(", ", map.Keys.Where(k => k != oldParameter)), MessageType.Info);
            if (references.Count == 0)
            {
                EditorGUILayout.HelpBox($"No references to \"{oldParameter}\" on this avatar.", MessageType.Info);
                return;
            }
            var readOnly = references.Count(r => r.ReadOnly);
            if (readOnly > 0)
                EditorGUILayout.HelpBox($"{readOnly} reference(s) are in package assets and will not be changed.", MessageType.Warning);

            EditorGUILayout.LabelField($"{references.Count} reference(s)", EditorStyles.miniLabel);
            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var reference in references)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(new GUIContent(reference.Location, reference.Location), GUILayout.MinWidth(200));
                    EditorGUILayout.LabelField($"{reference.OldName} -> {reference.NewName}" + (reference.ReadOnly ? " (read-only)" : ""), GUILayout.Width(220));
                    if (GUILayout.Button("Ping", EditorStyles.miniButton, GUILayout.Width(40))) EditorGUIUtility.PingObject(reference.Owner);
                }
            }
            EditorGUILayout.EndScrollView();

            if (!GUILayout.Button($"Rename in {references.Count - readOnly} Place(s)", GUILayout.Height(30))) return;
            if (!EditorUtility.DisplayDialog("Rename Parameter", $"Rename \"{oldParameter}\" to \"{newParameter}\" in {references.Count - readOnly} place(s)?", "Rename", "Cancel"))
                return;
            RenameParameter(parameterAvatar, map);
            oldParameter = newParameter;
            newParameter = "";
            GUIUtility.ExitGUI();
        }
    }
}

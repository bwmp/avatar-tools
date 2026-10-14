using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using AnimLayerType = VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.AnimLayerType;

namespace AvatarTools.Editor
{
    public static class AvatarToolsUtil
    {
        public const string MenuRoot = "Tools/Avatar Tools/";
        public const string GeneratedRoot = "Assets/AvatarTools/Generated";

        static readonly AnimLayerType[] BaseLayerOrder =
        {
            AnimLayerType.Base, AnimLayerType.Additive, AnimLayerType.Gesture, AnimLayerType.Action, AnimLayerType.FX
        };

        public static VRCAvatarDescriptor FindDescriptor(GameObject go)
        {
            return go == null ? null : go.GetComponentInParent<VRCAvatarDescriptor>(true);
        }

        public static VRCAvatarDescriptor SelectedAvatar()
        {
            var fromSelection = FindDescriptor(Selection.activeGameObject);
            if (fromSelection != null) return fromSelection;
            var all = UnityEngine.Object.FindObjectsOfType<VRCAvatarDescriptor>(true);
            return all.Length == 1 ? all[0] : null;
        }

        public static VRCAvatarDescriptor AvatarField(VRCAvatarDescriptor current)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                current = (VRCAvatarDescriptor)EditorGUILayout.ObjectField("Avatar", current, typeof(VRCAvatarDescriptor), true);
                if (GUILayout.Button("Use Selected", GUILayout.Width(100)))
                    current = SelectedAvatar() ?? current;
            }
            return current;
        }

        public static string GetPath(Transform root, Transform target)
        {
            if (target == root) return "";
            var parts = new List<string>();
            for (var t = target; t != null && t != root; t = t.parent)
                parts.Add(t.name);
            parts.Reverse();
            return string.Join("/", parts);
        }

        public static string SafeName(string name)
        {
            var invalid = Path.GetInvalidFileNameChars();
            var cleaned = new string(name.Select(c => invalid.Contains(c) || c == '/' ? '_' : c).ToArray()).Trim();
            return string.IsNullOrEmpty(cleaned) ? "Unnamed" : cleaned;
        }

        public static string EnsureFolder(string path)
        {
            path = path.Replace('\\', '/').TrimEnd('/');
            if (AssetDatabase.IsValidFolder(path)) return path;
            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
            return path;
        }

        public static string AvatarFolder(VRCAvatarDescriptor avatar, string sub = null)
        {
            var folder = GeneratedRoot + "/" + SafeName(avatar.gameObject.name);
            if (!string.IsNullOrEmpty(sub)) folder += "/" + sub;
            return EnsureFolder(folder);
        }

        public static T CreateAsset<T>(T asset, string folder, string name, string extension) where T : UnityEngine.Object
        {
            EnsureFolder(folder);
            var path = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{SafeName(name)}.{extension}");
            AssetDatabase.CreateAsset(asset, path);
            return asset;
        }

        public static void EnsureBaseLayers(VRCAvatarDescriptor avatar)
        {
            var layers = avatar.baseAnimationLayers;
            if (layers != null && BaseLayerOrder.All(type => layers.Any(l => l.type == type))) return;
            var existing = layers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>();
            Undo.RecordObject(avatar, "Initialize Playable Layers");
            avatar.baseAnimationLayers = BaseLayerOrder.Select(type =>
            {
                var found = existing.FirstOrDefault(l => l.type == type);
                return found.type == type ? found : new VRCAvatarDescriptor.CustomAnimLayer { type = type, isDefault = true, isEnabled = true };
            }).ToArray();
            EditorUtility.SetDirty(avatar);
        }

        public static AnimatorController GetLayerController(VRCAvatarDescriptor avatar, AnimLayerType type)
        {
            var layers = (avatar.baseAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
                .Concat(avatar.specialAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>());
            foreach (var layer in layers)
                if (layer.type == type && !layer.isDefault)
                    return layer.animatorController as AnimatorController;
            return null;
        }

        public static AnimatorController GetOrCreateLayerController(VRCAvatarDescriptor avatar, AnimLayerType type)
        {
            var controller = GetLayerController(avatar, type);
            if (controller != null) return controller;
            EnsureBaseLayers(avatar);
            var path = AssetDatabase.GenerateUniqueAssetPath($"{AvatarFolder(avatar)}/{SafeName(avatar.gameObject.name)}_{type}.controller");
            controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            Undo.RecordObject(avatar, "Create " + type + " Controller");
            avatar.customizeAnimationLayers = true;
            var layers = avatar.baseAnimationLayers;
            for (var i = 0; i < layers.Length; i++)
            {
                if (layers[i].type != type) continue;
                layers[i].isDefault = false;
                layers[i].isEnabled = true;
                layers[i].animatorController = controller;
            }
            avatar.baseAnimationLayers = layers;
            EditorUtility.SetDirty(avatar);
            return controller;
        }

        public static IEnumerable<AnimatorController> AllControllers(VRCAvatarDescriptor avatar)
        {
            var layers = (avatar.baseAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>())
                .Concat(avatar.specialAnimationLayers ?? Array.Empty<VRCAvatarDescriptor.CustomAnimLayer>());
            return layers.Where(l => !l.isDefault).Select(l => l.animatorController as AnimatorController)
                .Where(c => c != null).Distinct();
        }

        public static IEnumerable<AnimatorState> AllStates(AnimatorStateMachine machine)
        {
            foreach (var child in machine.states) yield return child.state;
            foreach (var sub in machine.stateMachines)
            foreach (var state in AllStates(sub.stateMachine))
                yield return state;
        }

        public static IEnumerable<AnimationClip> ClipsInMotion(Motion motion)
        {
            if (motion is AnimationClip clip) yield return clip;
            else if (motion is BlendTree tree)
                foreach (var child in tree.children)
                foreach (var nested in ClipsInMotion(child.motion))
                    yield return nested;
        }

        public static IEnumerable<AnimationClip> AllClips(AnimatorController controller)
        {
            return controller.layers.SelectMany(l => AllStates(l.stateMachine))
                .SelectMany(s => ClipsInMotion(s.motion)).Distinct();
        }

        public static void EnsureAnimatorParameter(AnimatorController controller, string name, AnimatorControllerParameterType type, float defaultValue = 0)
        {
            if (controller.parameters.Any(p => p.name == name)) return;
            controller.AddParameter(new AnimatorControllerParameter
            {
                name = name, type = type,
                defaultBool = defaultValue != 0, defaultFloat = defaultValue, defaultInt = (int)defaultValue
            });
        }

        public static AnimatorControllerLayer ReplaceLayer(AnimatorController controller, string name)
        {
            var layers = controller.layers.ToList();
            var existing = layers.FindIndex(l => l.name == name);
            if (existing >= 0)
            {
                RemoveStateMachineAssets(controller, layers[existing].stateMachine);
                layers.RemoveAt(existing);
            }
            var machine = new AnimatorStateMachine { name = name, hideFlags = HideFlags.HideInHierarchy };
            AssetDatabase.AddObjectToAsset(machine, controller);
            var layer = new AnimatorControllerLayer { name = name, defaultWeight = 1f, stateMachine = machine };
            if (existing >= 0) layers.Insert(existing, layer);
            else layers.Add(layer);
            controller.layers = layers.ToArray();
            return layer;
        }

        public static void RemoveStateMachineAssets(AnimatorController controller, AnimatorStateMachine machine)
        {
            if (machine == null) return;
            foreach (var sub in machine.stateMachines) RemoveStateMachineAssets(controller, sub.stateMachine);
            foreach (var child in machine.states)
            {
                foreach (var t in child.state.transitions) RemoveSubAsset(controller, t);
                foreach (var b in child.state.behaviours) RemoveSubAsset(controller, b);
                if (child.state.motion is BlendTree tree && AssetDatabase.GetAssetPath(tree) == AssetDatabase.GetAssetPath(controller))
                    RemoveBlendTree(controller, tree);
                RemoveSubAsset(controller, child.state);
            }
            foreach (var t in machine.anyStateTransitions) RemoveSubAsset(controller, t);
            foreach (var t in machine.entryTransitions) RemoveSubAsset(controller, t);
            foreach (var b in machine.behaviours) RemoveSubAsset(controller, b);
            RemoveSubAsset(controller, machine);
        }

        static void RemoveBlendTree(AnimatorController controller, BlendTree tree)
        {
            foreach (var child in tree.children)
                if (child.motion is BlendTree nested && AssetDatabase.GetAssetPath(nested) == AssetDatabase.GetAssetPath(controller))
                    RemoveBlendTree(controller, nested);
            RemoveSubAsset(controller, tree);
        }

        static void RemoveSubAsset(AnimatorController controller, UnityEngine.Object obj)
        {
            if (obj != null && AssetDatabase.GetAssetPath(obj) == AssetDatabase.GetAssetPath(controller))
                AssetDatabase.RemoveObjectFromAsset(obj);
        }

        public static AnimatorState AddState(AnimatorStateMachine machine, string name, Motion motion, Vector3 position, bool writeDefaults)
        {
            // Unity rejects '.' in state names; blendshape and material names often contain it.
            var state = machine.AddState(name.Replace('.', '_'), position);
            state.motion = motion;
            state.writeDefaultValues = writeDefaults;
            return state;
        }

        public static AnimatorStateTransition AddInstantTransition(AnimatorState from, AnimatorState to)
        {
            var transition = from.AddTransition(to);
            transition.hasExitTime = false;
            transition.duration = 0;
            transition.exitTime = 0;
            return transition;
        }

        public static bool DetectWriteDefaults(VRCAvatarDescriptor avatar)
        {
            var states = AllControllers(avatar).SelectMany(c => c.layers.SelectMany(l => AllStates(l.stateMachine))).ToList();
            if (states.Count == 0) return false;
            return states.Count(s => s.writeDefaultValues) * 2 >= states.Count;
        }

        public static VRCExpressionParameters GetOrCreateParameters(VRCAvatarDescriptor avatar)
        {
            if (avatar.expressionParameters != null) return avatar.expressionParameters;
            var parameters = ScriptableObject.CreateInstance<VRCExpressionParameters>();
            parameters.parameters = new[]
            {
                new VRCExpressionParameters.Parameter { name = "VRCEmote", valueType = VRCExpressionParameters.ValueType.Int, saved = false, networkSynced = true },
                new VRCExpressionParameters.Parameter { name = "VRCFaceBlendH", valueType = VRCExpressionParameters.ValueType.Float, saved = false, networkSynced = true },
                new VRCExpressionParameters.Parameter { name = "VRCFaceBlendV", valueType = VRCExpressionParameters.ValueType.Float, saved = false, networkSynced = true },
            };
            CreateAsset(parameters, AvatarFolder(avatar), avatar.gameObject.name + "_Parameters", "asset");
            Undo.RecordObject(avatar, "Create Expression Parameters");
            avatar.customExpressions = true;
            avatar.expressionParameters = parameters;
            EditorUtility.SetDirty(avatar);
            return parameters;
        }

        public static VRCExpressionsMenu GetOrCreateMenu(VRCAvatarDescriptor avatar)
        {
            if (avatar.expressionsMenu != null) return avatar.expressionsMenu;
            var menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            CreateAsset(menu, AvatarFolder(avatar), avatar.gameObject.name + "_Menu", "asset");
            Undo.RecordObject(avatar, "Create Expressions Menu");
            avatar.customExpressions = true;
            avatar.expressionsMenu = menu;
            EditorUtility.SetDirty(avatar);
            return menu;
        }

        public static void AddExpressionParameter(VRCExpressionParameters parameters, string name, VRCExpressionParameters.ValueType type, float defaultValue, bool saved, bool synced = true)
        {
            Undo.RecordObject(parameters, "Add Expression Parameter");
            var list = (parameters.parameters ?? Array.Empty<VRCExpressionParameters.Parameter>()).ToList();
            var existing = list.FirstOrDefault(p => p.name == name);
            if (existing == null)
            {
                existing = new VRCExpressionParameters.Parameter { name = name };
                list.Add(existing);
            }
            existing.valueType = type;
            existing.defaultValue = defaultValue;
            existing.saved = saved;
            existing.networkSynced = synced;
            parameters.parameters = list.ToArray();
            EditorUtility.SetDirty(parameters);
        }

        public static int ParameterCost(VRCExpressionParameters parameters)
        {
            return parameters == null ? 0 : parameters.CalcTotalCost();
        }

        // VRChat menus hold at most MAX_CONTROLS entries; overflow goes into a chained "More" submenu.
        public static void AddMenuControl(VRCExpressionsMenu menu, VRCExpressionsMenu.Control control)
        {
            Undo.RecordObject(menu, "Add Menu Control");
            menu.controls.RemoveAll(c => c.name == control.name && c.type == control.type);
            if (menu.controls.Count < VRCExpressionsMenu.MAX_CONTROLS)
            {
                menu.controls.Add(control);
                EditorUtility.SetDirty(menu);
                return;
            }
            var more = menu.controls.LastOrDefault(c => c.type == VRCExpressionsMenu.Control.ControlType.SubMenu && c.name == "More" && c.subMenu != null);
            if (more == null)
            {
                var last = menu.controls[menu.controls.Count - 1];
                var subMenu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
                CreateAsset(subMenu, Path.GetDirectoryName(AssetDatabase.GetAssetPath(menu)).Replace('\\', '/'), menu.name + "_More", "asset");
                subMenu.controls.Add(last);
                menu.controls[menu.controls.Count - 1] = more = new VRCExpressionsMenu.Control
                {
                    name = "More", type = VRCExpressionsMenu.Control.ControlType.SubMenu, subMenu = subMenu
                };
                EditorUtility.SetDirty(subMenu);
            }
            EditorUtility.SetDirty(menu);
            AddMenuControl(more.subMenu, control);
        }

        public static VRCExpressionsMenu GetOrCreateSubMenu(VRCExpressionsMenu parent, string name)
        {
            var existing = FindSubMenu(parent, name);
            if (existing != null) return existing;
            var subMenu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            CreateAsset(subMenu, Path.GetDirectoryName(AssetDatabase.GetAssetPath(parent)).Replace('\\', '/'), parent.name + "_" + name, "asset");
            AddMenuControl(parent, new VRCExpressionsMenu.Control
            {
                name = name, type = VRCExpressionsMenu.Control.ControlType.SubMenu, subMenu = subMenu
            });
            return subMenu;
        }

        static VRCExpressionsMenu FindSubMenu(VRCExpressionsMenu menu, string name)
        {
            foreach (var control in menu.controls)
            {
                if (control.type != VRCExpressionsMenu.Control.ControlType.SubMenu || control.subMenu == null) continue;
                if (control.name == name) return control.subMenu;
                if (control.name == "More")
                {
                    var nested = FindSubMenu(control.subMenu, name);
                    if (nested != null) return nested;
                }
            }
            return null;
        }

        public static IEnumerable<VRCExpressionsMenu> AllMenus(VRCExpressionsMenu root)
        {
            var visited = new HashSet<VRCExpressionsMenu>();
            var stack = new Stack<VRCExpressionsMenu>();
            if (root != null) stack.Push(root);
            while (stack.Count > 0)
            {
                var menu = stack.Pop();
                if (!visited.Add(menu)) continue;
                yield return menu;
                foreach (var control in menu.controls)
                    if (control.subMenu != null) stack.Push(control.subMenu);
            }
        }

        public static void SaveAll()
        {
            AssetDatabase.SaveAssets();
        }
    }
}

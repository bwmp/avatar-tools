using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase;
using AnimLayerType = VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.AnimLayerType;
using ValueType = VRC.SDK3.Avatars.ScriptableObjects.VRCExpressionParameters.ValueType;
using Object = UnityEngine.Object;

namespace AvatarTools.Editor
{
    internal static class AvatarPresetsBuilder
    {
        public const string Prefix = "Presets/";
        public const string CommandParameter = Prefix + "Cmd";
        public const string LayerName = "Avatar Presets";
        // Menu toggles are stored under their path so saved data survives the generated parameter name changing between builds.
        const string MenuKeyPrefix = "Menu/";
        // Command values: 1..8 load a slot, 11..18 save it.
        const int SaveOffset = 10;
        const string BuildFolder = AvatarToolsUtil.GeneratedRoot + "/_PresetsBuild";

        static readonly string[] BuiltInParameters = { "VRCEmote", "VRCFaceBlendH", "VRCFaceBlendV" };

        struct Source
        {
            public string Key;
            public VRCExpressionParameters.Parameter Parameter;
        }

        public static string StorageName(int slot, string key) => $"{Prefix}{slot}/{key}";
        public static string HasDataName(int slot) => $"{Prefix}{slot}/HasData";

        public static string SlotLabel(AvatarPresets presets, int slot)
        {
            var custom = presets.slotNames != null && slot - 1 < presets.slotNames.Length ? presets.slotNames[slot - 1] : null;
            return string.IsNullOrWhiteSpace(custom) ? "Slot " + slot : custom.Trim();
        }

        public static bool IsSelectable(VRCExpressionParameters.Parameter parameter)
        {
            return !string.IsNullOrEmpty(parameter.name)
                && !parameter.name.StartsWith(Prefix)
                && !BuiltInParameters.Contains(parameter.name);
        }

        // Menu toggles only exist once VRCFury and NDMF have run, so they can only be resolved while building.
        public static string Validate(AvatarPresets presets, VRCAvatarDescriptor avatar, bool resolveMenuToggles)
        {
            if (avatar == null) return "Avatar Presets must be on (or under) a VRC Avatar Descriptor.";
            var fxError = AnimatorToolsShared.ValidateAvatar(avatar);
            if (fxError != null) return fxError;
            if (presets.slotCount < 1 || presets.slotCount > AvatarPresets.MaxSlots)
                return $"Slot count must be between 1 and {AvatarPresets.MaxSlots}.";
            if (string.IsNullOrWhiteSpace(presets.menuName)) return "Enter a menu name.";
            if (presets.parameters.Count + presets.menuToggles.Count == 0) return "Select at least one parameter to save.";
            if (presets.parameters.Distinct().Count() != presets.parameters.Count
                || presets.menuToggles.Distinct().Count() != presets.menuToggles.Count)
                return "A parameter is listed more than once.";

            var available = avatar.expressionParameters == null
                ? Array.Empty<VRCExpressionParameters.Parameter>()
                : avatar.expressionParameters.parameters ?? Array.Empty<VRCExpressionParameters.Parameter>();
            if (available.Any(p => p.name != null && p.name.StartsWith(Prefix)))
                return $"The avatar already has parameters starting with \"{Prefix}\"; remove them first.";
            foreach (var name in presets.parameters)
            {
                var parameter = available.FirstOrDefault(p => p.name == name);
                if (parameter == null) return $"\"{name}\" is not in the avatar's expression parameters.";
                if (!IsSelectable(parameter)) return $"\"{name}\" cannot be saved in a preset.";
            }
            if (resolveMenuToggles)
            {
                var resolveError = ResolveSources(presets, avatar, out _);
                if (resolveError != null) return resolveError;
            }

            var root = avatar.expressionsMenu;
            if (presets.installMenu != null && (root == null || !AvatarToolsUtil.AllMenus(root).Contains(presets.installMenu)))
                return $"Install menu \"{presets.installMenu.name}\" is not part of the avatar's menu.";
            return null;
        }

        // persist is required for uploads because the SDK saves the avatar as a prefab, which drops references to in-memory assets.
        public static void Apply(AvatarPresets presets, VRCAvatarDescriptor avatar, bool persist)
        {
            var error = Validate(presets, avatar, true);
            if (error != null) throw new InvalidOperationException(error);

            var folder = persist ? PrepareBuildFolder(avatar) : null;
            ResolveSources(presets, avatar, out var sources);

            avatar.expressionParameters = BuildParameters(presets, avatar.expressionParameters, sources, folder);
            avatar.expressionsMenu = BuildMenu(presets, avatar.expressionsMenu, folder);
            avatar.customExpressions = true;
            SetFxController(avatar, BuildController(presets, avatar, sources, folder));
        }

        static string ResolveSources(AvatarPresets presets, VRCAvatarDescriptor avatar, out List<Source> sources)
        {
            var available = avatar.expressionParameters == null
                ? Array.Empty<VRCExpressionParameters.Parameter>()
                : avatar.expressionParameters.parameters ?? Array.Empty<VRCExpressionParameters.Parameter>();
            sources = presets.parameters
                .Select(name => new Source { Key = name, Parameter = available.First(p => p.name == name) })
                .ToList();

            var toggles = MenuToggles(avatar.expressionsMenu);
            foreach (var path in presets.menuToggles)
            {
                var toggle = toggles.FirstOrDefault(t => t.path == path);
                if (toggle.path == null) return $"Menu toggle \"{path}\" is not in the built avatar menu.";
                var parameter = available.FirstOrDefault(p => p.name == toggle.parameter);
                if (parameter == null || !IsSelectable(parameter))
                    return $"Menu toggle \"{path}\" uses \"{toggle.parameter}\", which cannot be saved in a preset.";
                // Exclusive toggles share one int parameter, which only needs saving once.
                if (sources.Any(s => s.Parameter.name == parameter.name)) continue;
                sources.Add(new Source { Key = MenuKeyPrefix + path, Parameter = parameter });
            }
            return null;
        }

        public static List<(string path, string parameter)> MenuToggles(VRCExpressionsMenu root)
        {
            var result = new List<(string path, string parameter)>();
            CollectMenuToggles(root, "", new HashSet<VRCExpressionsMenu>(), result);
            return result;
        }

        static void CollectMenuToggles(VRCExpressionsMenu menu, string prefix, HashSet<VRCExpressionsMenu> visited,
            List<(string path, string parameter)> result)
        {
            if (menu == null || !visited.Add(menu)) return;
            foreach (var control in menu.controls)
            {
                var path = prefix + control.name;
                if (control.type == VRCExpressionsMenu.Control.ControlType.SubMenu)
                {
                    CollectMenuToggles(control.subMenu, path + "/", visited, result);
                    continue;
                }
                var parameter = ControlParameter(control);
                if (!string.IsNullOrEmpty(parameter)) result.Add((path, parameter));
            }
        }

        static string ControlParameter(VRCExpressionsMenu.Control control)
        {
            switch (control.type)
            {
                case VRCExpressionsMenu.Control.ControlType.Toggle:
                case VRCExpressionsMenu.Control.ControlType.Button:
                    return control.parameter?.name;
                case VRCExpressionsMenu.Control.ControlType.RadialPuppet:
                    return control.subParameters != null && control.subParameters.Length > 0 ? control.subParameters[0]?.name : null;
                default:
                    return null;
            }
        }

        static string PrepareBuildFolder(VRCAvatarDescriptor avatar)
        {
            // Previous build output is only needed while that build runs, so each build starts clean.
            AssetDatabase.DeleteAsset(BuildFolder);
            return AvatarToolsUtil.EnsureFolder(BuildFolder + "/" + AvatarToolsUtil.SafeName(avatar.gameObject.name));
        }

        static VRCExpressionParameters BuildParameters(AvatarPresets presets, VRCExpressionParameters original,
            List<Source> sources, string folder)
        {
            var copy = Object.Instantiate(original);
            copy.name = original.name;
            var list = copy.parameters.ToList();
            list.Add(new VRCExpressionParameters.Parameter { name = CommandParameter, valueType = ValueType.Int, saved = false, networkSynced = false });
            for (var slot = 1; slot <= presets.slotCount; slot++)
            {
                list.Add(new VRCExpressionParameters.Parameter { name = HasDataName(slot), valueType = ValueType.Bool, saved = true, networkSynced = false });
                foreach (var source in sources)
                    list.Add(new VRCExpressionParameters.Parameter
                    {
                        name = StorageName(slot, source.Key), valueType = source.Parameter.valueType,
                        defaultValue = source.Parameter.defaultValue, saved = true, networkSynced = false
                    });
            }
            copy.parameters = list.ToArray();
            if (folder != null) AssetDatabase.CreateAsset(copy, folder + "/Parameters.asset");
            return copy;
        }

        static VRCExpressionsMenu BuildMenu(AvatarPresets presets, VRCExpressionsMenu originalRoot, string folder)
        {
            var clones = new Dictionary<VRCExpressionsMenu, VRCExpressionsMenu>();
            var root = originalRoot != null ? CloneMenuTree(originalRoot, clones) : NewMenu("Menu");
            var addedPages = new List<VRCExpressionsMenu>();
            var target = PageWithRoom(presets.installMenu != null ? clones[presets.installMenu] : root, addedPages);

            var loadMenu = NewMenu("Load");
            var saveMenu = NewMenu("Save");
            for (var slot = 1; slot <= presets.slotCount; slot++)
            {
                var label = SlotLabel(presets, slot);
                loadMenu.controls.Add(AnimatorToolsShared.Control(label, VRCExpressionsMenu.Control.ControlType.Button, CommandParameter, slot));
                saveMenu.controls.Add(AnimatorToolsShared.Control("Save " + label, VRCExpressionsMenu.Control.ControlType.Button, CommandParameter, SaveOffset + slot));
            }
            var presetsMenu = NewMenu(presets.menuName.Trim());
            presetsMenu.controls.Add(SubMenuControl("Load", loadMenu));
            presetsMenu.controls.Add(SubMenuControl("Save", saveMenu));
            target.controls.Add(SubMenuControl(presets.menuName.Trim(), presetsMenu));

            if (folder != null)
            {
                AssetDatabase.CreateAsset(root, folder + "/Menu.asset");
                foreach (var menu in clones.Values.Concat(addedPages).Concat(new[] { presetsMenu, loadMenu, saveMenu }).Where(m => m != root))
                    AssetDatabase.AddObjectToAsset(menu, root);
            }
            return root;
        }

        // Matches VRCFury's pagination: a full menu continues in a trailing "Next" submenu.
        static VRCExpressionsMenu PageWithRoom(VRCExpressionsMenu menu, List<VRCExpressionsMenu> addedPages)
        {
            var page = menu;
            while (page.controls.Count >= VRCExpressionsMenu.MAX_CONTROLS)
            {
                var last = page.controls[page.controls.Count - 1];
                if (last.type == VRCExpressionsMenu.Control.ControlType.SubMenu && last.name == "Next" && last.subMenu != null)
                {
                    page = last.subMenu;
                    continue;
                }
                var next = NewMenu($"{menu.name} (Page {addedPages.Count + 2})");
                next.controls.Add(last);
                page.controls.RemoveAt(page.controls.Count - 1);
                page.controls.Add(SubMenuControl("Next", next));
                addedPages.Add(next);
                page = next;
            }
            return page;
        }

        static VRCExpressionsMenu CloneMenuTree(VRCExpressionsMenu menu, Dictionary<VRCExpressionsMenu, VRCExpressionsMenu> clones)
        {
            if (clones.TryGetValue(menu, out var existing)) return existing;
            var copy = Object.Instantiate(menu);
            copy.name = menu.name;
            clones[menu] = copy;
            foreach (var control in copy.controls)
                if (control.subMenu != null) control.subMenu = CloneMenuTree(control.subMenu, clones);
            return copy;
        }

        static VRCExpressionsMenu NewMenu(string name)
        {
            var menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            menu.name = name;
            return menu;
        }

        static VRCExpressionsMenu.Control SubMenuControl(string name, VRCExpressionsMenu subMenu)
        {
            var control = AnimatorToolsShared.Control(name, VRCExpressionsMenu.Control.ControlType.SubMenu, null);
            control.subMenu = subMenu;
            return control;
        }

        static AnimatorController BuildController(AvatarPresets presets, VRCAvatarDescriptor avatar,
            List<Source> sources, string folder)
        {
            var original = AvatarToolsUtil.GetLayerController(avatar, AnimLayerType.FX);
            // A shallow copy is enough: existing layers keep pointing at the original state machines, which are never modified.
            var fx = original != null ? Object.Instantiate(original) : new AnimatorController();
            fx.name = original != null ? original.name : "FX";
            if (folder != null) AssetDatabase.CreateAsset(fx, folder + "/FX.controller");

            AvatarToolsUtil.EnsureAnimatorParameter(fx, CommandParameter, AnimatorControllerParameterType.Int);
            // Storage mirrors the FX parameter's type, which may differ from the expression type (e.g. a Bool toggle driving a Float for blend trees).
            var storageTypes = new Dictionary<string, AnimatorControllerParameterType>();
            foreach (var source in sources)
            {
                var parameter = source.Parameter;
                AvatarToolsUtil.EnsureAnimatorParameter(fx, parameter.name, AnimatorType(parameter.valueType), parameter.defaultValue);
                storageTypes[source.Key] = fx.parameters.First(p => p.name == parameter.name).type;
            }
            for (var slot = 1; slot <= presets.slotCount; slot++)
            {
                AvatarToolsUtil.EnsureAnimatorParameter(fx, HasDataName(slot), AnimatorControllerParameterType.Bool);
                foreach (var source in sources)
                    AvatarToolsUtil.EnsureAnimatorParameter(fx, StorageName(slot, source.Key), storageTypes[source.Key], source.Parameter.defaultValue);
            }

            var machine = new AnimatorStateMachine { name = LayerName, hideFlags = HideFlags.HideInHierarchy };
            if (folder != null) AssetDatabase.AddObjectToAsset(machine, fx);
            fx.AddLayer(new AnimatorControllerLayer { name = LayerName, defaultWeight = 1f, stateMachine = machine });

            // States with no motion can behave as write-defaults-on regardless of their setting, so they get a do-nothing clip.
            var empty = AnimatorToolsShared.ConstantClip("Presets_Empty", new[] { ("__AvatarPresets_Empty", typeof(GameObject), "m_IsActive", 0f) });
            if (folder != null) AssetDatabase.AddObjectToAsset(empty, fx);
            var writeDefaults = AvatarToolsUtil.DetectWriteDefaults(avatar);

            var idle = AvatarToolsUtil.AddState(machine, "Idle", empty, new Vector3(0, 0), writeDefaults);
            machine.defaultState = idle;
            for (var slot = 1; slot <= presets.slotCount; slot++)
            {
                var y = (slot - 1) * 60;
                var load = AddCommandState(machine, idle, "Load " + slot, new Vector3(300, y), slot, empty, writeDefaults, HasDataName(slot));
                AddDriver(load, sources.Select(s => Copy(StorageName(slot, s.Key), s.Parameter.name)));

                var save = AddCommandState(machine, idle, "Save " + slot, new Vector3(600, y), SaveOffset + slot, empty, writeDefaults);
                AddDriver(save, sources.Select(s => Copy(s.Parameter.name, StorageName(slot, s.Key)))
                    .Append(new VRC_AvatarParameterDriver.Parameter { type = VRC_AvatarParameterDriver.ChangeType.Set, name = HasDataName(slot), value = 1 }));
            }

            if (folder != null)
            {
                EditorUtility.SetDirty(fx);
                AssetDatabase.SaveAssets();
            }
            return fx;
        }

        static AnimatorState AddCommandState(AnimatorStateMachine machine, AnimatorState idle, string name, Vector3 position,
            int command, Motion motion, bool writeDefaults, string requiredBool = null)
        {
            var state = AvatarToolsUtil.AddState(machine, name, motion, position, writeDefaults);
            var enter = AvatarToolsUtil.AddInstantTransition(idle, state);
            enter.AddCondition(AnimatorConditionMode.Equals, command, CommandParameter);
            if (requiredBool != null) enter.AddCondition(AnimatorConditionMode.If, 0, requiredBool);
            AvatarToolsUtil.AddInstantTransition(state, idle).AddCondition(AnimatorConditionMode.NotEqual, command, CommandParameter);
            return state;
        }

        static void AddDriver(AnimatorState state, IEnumerable<VRC_AvatarParameterDriver.Parameter> parameters)
        {
            var driver = state.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
            // Only the wearer runs the copy; the resulting toggle changes reach others through the toggles' own sync.
            driver.localOnly = true;
            driver.parameters = parameters.ToList();
        }

        static VRC_AvatarParameterDriver.Parameter Copy(string source, string destination)
        {
            return new VRC_AvatarParameterDriver.Parameter { type = VRC_AvatarParameterDriver.ChangeType.Copy, source = source, name = destination };
        }

        static void SetFxController(VRCAvatarDescriptor avatar, AnimatorController fx)
        {
            AvatarToolsUtil.EnsureBaseLayers(avatar);
            avatar.customizeAnimationLayers = true;
            var layers = avatar.baseAnimationLayers;
            for (var i = 0; i < layers.Length; i++)
            {
                if (layers[i].type != AnimLayerType.FX) continue;
                layers[i].isDefault = false;
                layers[i].isEnabled = true;
                layers[i].animatorController = fx;
            }
            avatar.baseAnimationLayers = layers;
        }

        static AnimatorControllerParameterType AnimatorType(ValueType type)
        {
            switch (type)
            {
                case ValueType.Int: return AnimatorControllerParameterType.Int;
                case ValueType.Float: return AnimatorControllerParameterType.Float;
                default: return AnimatorControllerParameterType.Bool;
            }
        }
    }
}

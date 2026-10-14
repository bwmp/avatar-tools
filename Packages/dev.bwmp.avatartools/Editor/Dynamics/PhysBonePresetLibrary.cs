using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Dynamics.PhysBone.Components;
using AdvancedBool = VRC.Dynamics.VRCPhysBoneBase.AdvancedBool;
using LimitType = VRC.Dynamics.VRCPhysBoneBase.LimitType;

namespace AvatarTools.Editor
{
    public sealed class PhysBonePresetLibrary : EditorWindow
    {
        public const string PresetFolder = "Assets/AvatarTools/Presets/PhysBone";

        string presetName = "New Preset";
        Vector2 scroll;
        PhysBonePreset clipboard;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Dynamics/PhysBone Preset Library")]
        static void Open()
        {
            GetWindow<PhysBonePresetLibrary>("PhysBone Presets");
        }

        void OnSelectionChange()
        {
            Repaint();
        }

        void OnDisable()
        {
            if (clipboard != null) DestroyImmediate(clipboard);
        }

        public static List<VRCPhysBoneBase> SelectedPhysBones()
        {
            return Selection.gameObjects.SelectMany(go => go.GetComponents<VRCPhysBoneBase>()).Distinct().ToList();
        }

        public static List<PhysBonePreset> LoadPresets()
        {
            if (!AssetDatabase.IsValidFolder(PresetFolder)) return new List<PhysBonePreset>();
            return AssetDatabase.FindAssets("t:" + nameof(PhysBonePreset), new[] { PresetFolder })
                .Select(guid => AssetDatabase.LoadAssetAtPath<PhysBonePreset>(AssetDatabase.GUIDToAssetPath(guid)))
                .Where(p => p != null).OrderBy(p => p.name).ToList();
        }

        public static PhysBonePreset SavePreset(VRCPhysBoneBase source, string name)
        {
            var preset = CreateInstance<PhysBonePreset>();
            preset.Capture(source);
            AvatarToolsUtil.CreateAsset(preset, PresetFolder, name, "asset");
            AssetDatabase.SaveAssets();
            return preset;
        }

        public static void Apply(PhysBonePreset preset, IReadOnlyCollection<VRCPhysBoneBase> targets)
        {
            if (targets.Count == 0) return;
            Undo.RecordObjects(targets.Cast<UnityEngine.Object>().ToArray(), "Apply PhysBone Preset " + preset.name);
            foreach (var target in targets)
            {
                preset.ApplyTo(target);
                PrefabUtility.RecordPrefabInstancePropertyModifications(target);
                EditorUtility.SetDirty(target);
            }
        }

        public static List<PhysBonePreset> CreateDefaultPresets()
        {
            var existing = new HashSet<string>(LoadPresets().Select(p => p.name));
            var created = new List<PhysBonePreset>();
            foreach (var entry in DefaultPresets())
            {
                if (existing.Contains(entry.Key)) continue;
                var preset = CreateDefaultPreset();
                entry.Value(preset);
                AvatarToolsUtil.CreateAsset(preset, PresetFolder, entry.Key, "asset");
                created.Add(preset);
            }
            AssetDatabase.SaveAssets();
            return created;
        }

        static PhysBonePreset CreateDefaultPreset()
        {
            var temp = EditorUtility.CreateGameObjectWithHideFlags("PhysBone Defaults", HideFlags.HideAndDontSave);
            try
            {
                var preset = CreateInstance<PhysBonePreset>();
                preset.Capture(temp.AddComponent<VRCPhysBone>());
                preset.integrationType = VRCPhysBoneBase.IntegrationType.Simplified;
                preset.multiChildType = VRCPhysBoneBase.MultiChildType.Ignore;
                return preset;
            }
            finally
            {
                DestroyImmediate(temp);
            }
        }

        static IEnumerable<KeyValuePair<string, Action<PhysBonePreset>>> DefaultPresets()
        {
            yield return Entry("Hair", p =>
            {
                Forces(p, pull: 0.2f, spring: 0.4f, stiffness: 0.2f, gravity: 0.15f, falloff: 0.5f, immobile: 0.2f);
                Limits(p, LimitType.Angle, 60, 0, radius: 0.02f);
                Grab(p, AdvancedBool.True, AdvancedBool.True, maxStretch: 0.2f);
            });
            yield return Entry("Long Hair", p =>
            {
                Forces(p, pull: 0.15f, spring: 0.5f, stiffness: 0.1f, gravity: 0.25f, falloff: 0.6f, immobile: 0.1f);
                Limits(p, LimitType.Angle, 90, 0, radius: 0.025f);
                Grab(p, AdvancedBool.True, AdvancedBool.True, maxStretch: 0.1f);
            });
            yield return Entry("Tail", p =>
            {
                Forces(p, pull: 0.3f, spring: 0.6f, stiffness: 0.2f, gravity: 0.1f, falloff: 0.4f, immobile: 0);
                Limits(p, LimitType.Angle, 80, 0, radius: 0.04f);
                Grab(p, AdvancedBool.True, AdvancedBool.True, maxStretch: 0.15f);
            });
            yield return Entry("Ears", p =>
            {
                Forces(p, pull: 0.4f, spring: 0.3f, stiffness: 0.5f, gravity: 0, falloff: 0, immobile: 0.3f);
                Limits(p, LimitType.Angle, 30, 0, radius: 0.03f);
                Grab(p, AdvancedBool.True, AdvancedBool.False, maxStretch: 0);
            });
            yield return Entry("Skirt", p =>
            {
                Forces(p, pull: 0.2f, spring: 0.2f, stiffness: 0.1f, gravity: 0.3f, falloff: 0.7f, immobile: 0.1f);
                Limits(p, LimitType.Polar, 45, 20, radius: 0.03f);
                Grab(p, AdvancedBool.False, AdvancedBool.False, maxStretch: 0);
            });
            yield return Entry("Breast", p =>
            {
                Forces(p, pull: 0.25f, spring: 0.85f, stiffness: 0.1f, gravity: 0.05f, falloff: 0.5f, immobile: 0.4f);
                Limits(p, LimitType.Angle, 30, 0, radius: 0.05f);
                Grab(p, AdvancedBool.False, AdvancedBool.False, maxStretch: 0.1f);
                p.maxSquish = 0.2f;
            });
            yield return Entry("Accessory", p =>
            {
                Forces(p, pull: 0.3f, spring: 0.4f, stiffness: 0.3f, gravity: 0.2f, falloff: 0.5f, immobile: 0.2f);
                Limits(p, LimitType.Angle, 45, 0, radius: 0.015f);
                Grab(p, AdvancedBool.True, AdvancedBool.False, maxStretch: 0);
            });
        }

        static KeyValuePair<string, Action<PhysBonePreset>> Entry(string name, Action<PhysBonePreset> setup)
        {
            return new KeyValuePair<string, Action<PhysBonePreset>>(name, setup);
        }

        static void Forces(PhysBonePreset p, float pull, float spring, float stiffness, float gravity, float falloff, float immobile)
        {
            p.pull = pull;
            p.spring = spring;
            p.stiffness = stiffness;
            p.gravity = gravity;
            p.gravityFalloff = falloff;
            p.immobileType = VRCPhysBoneBase.ImmobileType.AllMotion;
            p.immobile = immobile;
        }

        static void Limits(PhysBonePreset p, LimitType type, float maxAngleX, float maxAngleZ, float radius)
        {
            p.limitType = type;
            p.maxAngleX = maxAngleX;
            p.maxAngleZ = maxAngleZ;
            p.allowCollision = AdvancedBool.True;
            p.radius = radius;
        }

        static void Grab(PhysBonePreset p, AdvancedBool grabbing, AdvancedBool posing, float maxStretch)
        {
            p.allowGrabbing = grabbing;
            p.allowPosing = posing;
            p.grabMovement = 0.5f;
            p.maxStretch = maxStretch;
        }

        void OnGUI()
        {
            var targets = SelectedPhysBones();
            EditorGUILayout.LabelField("Selection", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(targets.Count == 0
                ? "Select GameObjects with PhysBone components in the Hierarchy."
                : $"{targets.Count} PhysBone component(s) selected.", targets.Count == 0 ? MessageType.Info : MessageType.None);

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Copy / Paste", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUI.DisabledScope(targets.Count != 1))
                {
                    if (GUILayout.Button("Copy Settings"))
                    {
                        if (clipboard == null) clipboard = CreateInstance<PhysBonePreset>();
                        clipboard.hideFlags = HideFlags.HideAndDontSave;
                        clipboard.name = targets[0].name;
                        clipboard.Capture(targets[0]);
                    }
                }
                using (new EditorGUI.DisabledScope(clipboard == null || targets.Count == 0))
                    if (GUILayout.Button(clipboard == null ? "Paste Settings" : $"Paste '{clipboard.name}' Settings"))
                        Apply(clipboard, targets);
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Save Preset", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                presetName = EditorGUILayout.TextField(presetName);
                using (new EditorGUI.DisabledScope(targets.Count != 1 || string.IsNullOrWhiteSpace(presetName)))
                    if (GUILayout.Button("Save From Selected", GUILayout.Width(140)))
                        EditorGUIUtility.PingObject(SavePreset(targets[0], presetName.Trim()));
            }
            if (targets.Count > 1)
                EditorGUILayout.HelpBox("Select a single PhysBone to copy or save its settings.", MessageType.None);

            EditorGUILayout.Space();
            var presets = LoadPresets();
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Presets", EditorStyles.boldLabel);
                if (GUILayout.Button("Create Default Presets", GUILayout.Width(160))) CreateDefaultPresets();
            }
            if (presets.Count == 0)
                EditorGUILayout.HelpBox($"No presets in {PresetFolder}.", MessageType.Info);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            foreach (var preset in presets)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(preset.name, EditorStyles.label)) EditorGUIUtility.PingObject(preset);
                    GUILayout.Label($"{preset.integrationType}, {preset.limitType}", EditorStyles.miniLabel, GUILayout.Width(130));
                    using (new EditorGUI.DisabledScope(targets.Count == 0))
                        if (GUILayout.Button("Apply to Selected", GUILayout.Width(130)))
                            Apply(preset, targets);
                }
            }
            EditorGUILayout.EndScrollView();
        }
    }
}

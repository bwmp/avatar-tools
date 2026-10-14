using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDKBase.Editor.BuildPipeline;

namespace AvatarTools.Editor
{
    [CustomEditor(typeof(AvatarPresets))]
    internal class AvatarPresetsEditor : UnityEditor.Editor
    {
        bool showSlotNames;
        List<(string path, string parameter)> scannedToggles;
        bool scanFailed;

        public override void OnInspectorGUI()
        {
            var presets = (AvatarPresets)target;
            var avatar = presets.GetComponentInParent<VRCAvatarDescriptor>(true);

            serializedObject.Update();
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarPresets.slotCount)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarPresets.menuName)));
            EditorGUILayout.PropertyField(serializedObject.FindProperty(nameof(AvatarPresets.installMenu)));
            serializedObject.ApplyModifiedProperties();

            showSlotNames = EditorGUILayout.Foldout(showSlotNames, "Slot Names", true);
            if (showSlotNames) DrawSlotNames(presets);

            EditorGUILayout.Space();
            DrawParameters(presets, avatar);

            EditorGUILayout.Space();
            DrawMenuToggles(presets, avatar);

            EditorGUILayout.Space();
            var count = presets.parameters.Count + presets.menuToggles.Count;
            EditorGUILayout.HelpBox(
                $"{count} parameter(s) x {presets.slotCount} slot(s) = {count * presets.slotCount + presets.slotCount} saved local parameters. " +
                "Uses 0 synced bits. Added to the avatar only at upload and in play mode.", MessageType.Info);
            AnimatorToolsShared.HelpIfAny(AvatarPresetsBuilder.Validate(presets, avatar, false));
        }

        void DrawSlotNames(AvatarPresets presets)
        {
            if (presets.slotNames == null || presets.slotNames.Length != AvatarPresets.MaxSlots)
            {
                Undo.RecordObject(presets, "Resize Slot Names");
                var names = presets.slotNames ?? Array.Empty<string>();
                Array.Resize(ref names, AvatarPresets.MaxSlots);
                presets.slotNames = names;
                EditorUtility.SetDirty(presets);
            }
            using (new EditorGUI.IndentLevelScope())
            {
                for (var i = 0; i < presets.slotCount; i++)
                {
                    EditorGUI.BeginChangeCheck();
                    var value = EditorGUILayout.TextField("Slot " + (i + 1), presets.slotNames[i]);
                    if (!EditorGUI.EndChangeCheck()) continue;
                    Undo.RecordObject(presets, "Rename Preset Slot");
                    presets.slotNames[i] = value;
                    EditorUtility.SetDirty(presets);
                }
            }
        }

        void DrawParameters(AvatarPresets presets, VRCAvatarDescriptor avatar)
        {
            EditorGUILayout.LabelField("Parameters to Save", EditorStyles.boldLabel);
            if (avatar == null || avatar.expressionParameters == null)
            {
                EditorGUILayout.HelpBox("Place this on an avatar that has Expression Parameters to pick from.", MessageType.Warning);
                return;
            }

            var available = (avatar.expressionParameters.parameters ?? Array.Empty<VRCExpressionParameters.Parameter>())
                .Where(AvatarPresetsBuilder.IsSelectable).ToList();
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Select All")) SetParameters(presets, available.Select(p => p.name));
                if (GUILayout.Button("Select None")) SetParameters(presets, Enumerable.Empty<string>());
            }

            foreach (var parameter in available)
            {
                var selected = presets.parameters.Contains(parameter.name);
                var label = $"{parameter.name}  ({parameter.valueType}{(parameter.saved ? "" : ", not saved")})";
                if (EditorGUILayout.ToggleLeft(label, selected) == selected) continue;
                Undo.RecordObject(presets, "Toggle Preset Parameter");
                if (selected) presets.parameters.Remove(parameter.name);
                else presets.parameters.Add(parameter.name);
                EditorUtility.SetDirty(presets);
            }

            foreach (var missing in presets.parameters.Where(n => available.All(p => p.name != n)).ToList())
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(missing + "  (not on avatar)", EditorStyles.miniLabel);
                    if (!GUILayout.Button("Remove", GUILayout.Width(70))) continue;
                    Undo.RecordObject(presets, "Remove Preset Parameter");
                    presets.parameters.Remove(missing);
                    EditorUtility.SetDirty(presets);
                }
            }
        }

        void DrawMenuToggles(AvatarPresets presets, VRCAvatarDescriptor avatar)
        {
            EditorGUILayout.LabelField("Build-Time Toggles (VRCFury, Modular Avatar)", EditorStyles.boldLabel);
            if (avatar == null) return;

            using (new EditorGUI.DisabledScope(EditorApplication.isPlayingOrWillChangePlaymode))
            {
                // Deferred because the build can show progress bars and dialogs, which break the current GUI layout pass.
                if (GUILayout.Button(scannedToggles == null ? "Scan Avatar" : "Rescan Avatar"))
                    EditorApplication.delayCall += () =>
                    {
                        if (avatar == null) return;
                        scannedToggles = ScanBuiltMenu(avatar);
                        scanFailed = scannedToggles == null;
                        Repaint();
                    };
            }
            if (scanFailed)
                EditorGUILayout.HelpBox("The test build failed; check the Console.", MessageType.Error);
            else if (scannedToggles == null)
                EditorGUILayout.HelpBox("Runs a test build on a temporary copy of the avatar to find menu toggles added by VRCFury and Modular Avatar.", MessageType.Info);

            // Toggles whose parameter is already in the descriptor's own asset are picked from the list above instead.
            var ownParameters = avatar.expressionParameters == null || avatar.expressionParameters.parameters == null
                ? new HashSet<string>()
                : new HashSet<string>(avatar.expressionParameters.parameters.Select(p => p.name));
            var toggles = (scannedToggles ?? new List<(string path, string parameter)>())
                .Where(t => !ownParameters.Contains(t.parameter) && !t.parameter.StartsWith(AvatarPresetsBuilder.Prefix))
                .ToList();

            foreach (var toggle in toggles)
            {
                var selected = presets.menuToggles.Contains(toggle.path);
                if (EditorGUILayout.ToggleLeft($"{toggle.path}  ({toggle.parameter})", selected) == selected) continue;
                Undo.RecordObject(presets, "Toggle Preset Menu Toggle");
                if (selected) presets.menuToggles.Remove(toggle.path);
                else presets.menuToggles.Add(toggle.path);
                EditorUtility.SetDirty(presets);
            }

            foreach (var unlisted in presets.menuToggles.Where(p => toggles.All(t => t.path != p)).ToList())
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var status = scannedToggles == null ? "" : "  (not found in scan)";
                    EditorGUILayout.LabelField(unlisted + status, EditorStyles.miniLabel);
                    if (!GUILayout.Button("Remove", GUILayout.Width(70))) continue;
                    Undo.RecordObject(presets, "Remove Preset Menu Toggle");
                    presets.menuToggles.Remove(unlisted);
                    EditorUtility.SetDirty(presets);
                }
            }
        }

        // Runs the same preprocessors as an upload on a throwaway copy, so the menu matches what the build hook will see.
        static List<(string path, string parameter)> ScanBuiltMenu(VRCAvatarDescriptor avatar)
        {
            var clone = Instantiate(avatar.gameObject);
            try
            {
                foreach (var presets in clone.GetComponentsInChildren<AvatarPresets>(true)) DestroyImmediate(presets);
                if (!VRCBuildPipelineCallbacks.OnPreprocessAvatar(clone)) return null;
                return AvatarPresetsBuilder.MenuToggles(clone.GetComponent<VRCAvatarDescriptor>().expressionsMenu);
            }
            finally
            {
                DestroyImmediate(clone);
            }
        }

        static void SetParameters(AvatarPresets presets, IEnumerable<string> names)
        {
            Undo.RecordObject(presets, "Set Preset Parameters");
            presets.parameters = names.ToList();
            EditorUtility.SetDirty(presets);
        }
    }
}

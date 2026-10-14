using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using LipSyncStyle = VRC.SDKBase.VRC_AvatarDescriptor.LipSyncStyle;

namespace AvatarTools.Editor
{
    public class AvatarDescriptorQuickSetup : EditorWindow
    {
        public static readonly string[] VisemeNames = { "sil", "PP", "FF", "TH", "DD", "kk", "CH", "SS", "nn", "RR", "aa", "E", "ih", "oh", "ou" };
        static readonly string[] VisemePrefixes = { "vrc.v_", "vrc_v_", "v_", "viseme_", "vrc.viseme_" };
        static readonly string[] BlinkNames = { "vrc.blink", "blink", "blink_both", "eyes_closed", "eye_close", "eyeclose", "vrc.blink_both" };
        static readonly string[] LookUpNames = { "vrc.lowerlid_up", "lookup", "look_up", "eyes_up", "eye_lookup" };
        static readonly string[] LookDownNames = { "vrc.lowerlid_down", "lookdown", "look_down", "eyes_down", "eye_lookdown" };
        static readonly string[] LeftEyeNames = { "lefteye", "eyeleft", "eyel", "leye" };
        static readonly string[] RightEyeNames = { "righteye", "eyeright", "eyer", "reye" };
        const float ViewForwardOffset = 0.04f;
        static readonly Vector3 HeadViewOffset = new Vector3(0, 0.08f, 0.08f);

        public class Detection
        {
            public Vector3? ViewPosition;
            public string ViewSource = "Not found";
            public SkinnedMeshRenderer VisemeMesh;
            public string[] Visemes = new string[VisemeNames.Length];
            public Transform LeftEye, RightEye;
            public SkinnedMeshRenderer EyelidMesh;
            public int[] EyelidShapes = { -1, -1, -1 };
        }

        GameObject target;
        Detection detection;
        bool applyView = true, applyLipSync = true, applyEyes = true;
        Vector2 scroll;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Setup/Avatar Descriptor Quick Setup")]
        static void Open()
        {
            GetWindow<AvatarDescriptorQuickSetup>("Descriptor Setup");
        }

        void OnEnable()
        {
            SetTarget(SelectedRoot());
        }

        static GameObject SelectedRoot()
        {
            var avatar = AvatarToolsUtil.SelectedAvatar();
            if (avatar != null) return avatar.gameObject;
            var selected = Selection.activeGameObject;
            if (selected == null || EditorUtility.IsPersistent(selected)) return null;
            var animator = selected.GetComponentInParent<Animator>(true);
            return animator != null ? animator.gameObject : null;
        }

        void SetTarget(GameObject go)
        {
            target = go;
            detection = go != null ? Detect(go) : null;
        }

        void OnGUI()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                var picked = (GameObject)EditorGUILayout.ObjectField("Avatar Root", target, typeof(GameObject), true);
                if (GUILayout.Button("Use Selected", GUILayout.Width(100))) picked = SelectedRoot() ?? picked;
                if (picked != target) SetTarget(picked != null && !EditorUtility.IsPersistent(picked) ? picked : null);
            }
            if (target == null)
            {
                EditorGUILayout.HelpBox("Pick an avatar in the scene (an object with a VRC Avatar Descriptor or a humanoid Animator).", MessageType.Info);
                return;
            }
            var descriptor = target.GetComponent<VRCAvatarDescriptor>();
            var animator = target.GetComponent<Animator>();
            if (descriptor == null)
                EditorGUILayout.HelpBox("This object has no VRC Avatar Descriptor. One will be added when you apply.", MessageType.Warning);
            if (animator == null || !animator.isHuman)
                EditorGUILayout.HelpBox("No humanoid Animator on this object. Detection falls back to bone names.", MessageType.Warning);
            if (GUILayout.Button("Re-detect")) detection = Detect(target);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            DrawView();
            DrawLipSync();
            DrawEyes();
            EditorGUILayout.EndScrollView();

            var anything = (applyView && detection.ViewPosition.HasValue) || (applyLipSync && detection.VisemeMesh != null)
                || (applyEyes && (detection.LeftEye != null || detection.RightEye != null));
            using (new EditorGUI.DisabledScope(!anything))
            {
                if (GUILayout.Button(descriptor == null ? "Add Descriptor and Apply" : "Apply", GUILayout.Height(30)))
                {
                    descriptor = Apply(target, detection, applyView, applyLipSync, applyEyes);
                    Selection.activeGameObject = descriptor.gameObject;
                }
            }
        }

        void DrawView()
        {
            EditorGUILayout.Space();
            applyView = EditorGUILayout.ToggleLeft("View Position", applyView, EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(!applyView))
            using (new EditorGUI.IndentLevelScope())
            {
                EditorGUILayout.LabelField("Source", detection.ViewSource);
                if (detection.ViewPosition.HasValue)
                    detection.ViewPosition = EditorGUILayout.Vector3Field("Position", detection.ViewPosition.Value);
            }
        }

        void DrawLipSync()
        {
            EditorGUILayout.Space();
            applyLipSync = EditorGUILayout.ToggleLeft("Lip Sync (Viseme Blend Shapes)", applyLipSync, EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(!applyLipSync))
            using (new EditorGUI.IndentLevelScope())
            {
                var mesh = (SkinnedMeshRenderer)EditorGUILayout.ObjectField("Mesh", detection.VisemeMesh, typeof(SkinnedMeshRenderer), true);
                if (mesh != detection.VisemeMesh)
                {
                    detection.VisemeMesh = mesh;
                    detection.Visemes = mesh != null ? MatchVisemes(mesh) : new string[VisemeNames.Length];
                }
                if (detection.VisemeMesh == null)
                {
                    EditorGUILayout.HelpBox("No mesh with viseme blend shapes found.", MessageType.Warning);
                    return;
                }
                for (var i = 0; i < VisemeNames.Length; i++)
                {
                    var found = detection.Visemes[i];
                    EditorGUILayout.LabelField(VisemeNames[i], string.IsNullOrEmpty(found) ? "(missing)" : found,
                        string.IsNullOrEmpty(found) ? WarningLabel : EditorStyles.label);
                }
            }
        }

        void DrawEyes()
        {
            EditorGUILayout.Space();
            applyEyes = EditorGUILayout.ToggleLeft("Eye Look", applyEyes, EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(!applyEyes))
            using (new EditorGUI.IndentLevelScope())
            {
                detection.LeftEye = (Transform)EditorGUILayout.ObjectField("Left Eye", detection.LeftEye, typeof(Transform), true);
                detection.RightEye = (Transform)EditorGUILayout.ObjectField("Right Eye", detection.RightEye, typeof(Transform), true);
                var mesh = (SkinnedMeshRenderer)EditorGUILayout.ObjectField("Eyelid Mesh", detection.EyelidMesh, typeof(SkinnedMeshRenderer), true);
                if (mesh != detection.EyelidMesh)
                {
                    detection.EyelidMesh = mesh;
                    detection.EyelidShapes = mesh != null ? MatchEyelids(mesh.sharedMesh) : new[] { -1, -1, -1 };
                }
                if (detection.EyelidMesh == null || detection.EyelidShapes[0] < 0)
                {
                    EditorGUILayout.LabelField("Eyelids", "None (no blink blend shape found)");
                    return;
                }
                var sharedMesh = detection.EyelidMesh.sharedMesh;
                string ShapeName(int index) => index >= 0 ? sharedMesh.GetBlendShapeName(index) : "(none)";
                EditorGUILayout.LabelField("Blink", ShapeName(detection.EyelidShapes[0]));
                EditorGUILayout.LabelField("Looking Up", ShapeName(detection.EyelidShapes[1]));
                EditorGUILayout.LabelField("Looking Down", ShapeName(detection.EyelidShapes[2]));
            }
        }

        static GUIStyle warningLabel;
        static GUIStyle WarningLabel => warningLabel ?? (warningLabel = new GUIStyle(EditorStyles.label) { normal = { textColor = new Color(0.9f, 0.55f, 0.1f) } });

        public static Detection Detect(GameObject root)
        {
            var result = new Detection();
            var animator = root.GetComponent<Animator>();
            var human = animator != null && animator.isHuman ? animator : null;
            var transforms = root.GetComponentsInChildren<Transform>(true);

            result.LeftEye = human != null ? human.GetBoneTransform(HumanBodyBones.LeftEye) : null;
            result.RightEye = human != null ? human.GetBoneTransform(HumanBodyBones.RightEye) : null;
            if (result.LeftEye == null) result.LeftEye = transforms.FirstOrDefault(t => LeftEyeNames.Contains(Squash(t.name)));
            if (result.RightEye == null) result.RightEye = transforms.FirstOrDefault(t => RightEyeNames.Contains(Squash(t.name)));

            if (result.LeftEye != null && result.RightEye != null)
            {
                var mid = (result.LeftEye.position + result.RightEye.position) * 0.5f;
                result.ViewPosition = root.transform.InverseTransformPoint(mid) + Vector3.forward * ViewForwardOffset;
                result.ViewSource = "Between eye bones";
            }
            else
            {
                var head = human != null ? human.GetBoneTransform(HumanBodyBones.Head) : null;
                if (head == null) head = transforms.FirstOrDefault(t => Squash(t.name) == "head");
                if (head != null)
                {
                    result.ViewPosition = root.transform.InverseTransformPoint(head.position) + HeadViewOffset;
                    result.ViewSource = "Head bone + offset";
                }
            }

            var renderers = root.GetComponentsInChildren<SkinnedMeshRenderer>(true).Where(r => r.sharedMesh != null).ToList();
            var best = renderers
                .Select(r => (renderer: r, visemes: MatchVisemes(r)))
                .Select(x => (x.renderer, x.visemes, count: x.visemes.Count(v => !string.IsNullOrEmpty(v))))
                .Where(x => x.count > 0)
                .OrderByDescending(x => x.count)
                .ThenByDescending(x => string.Equals(x.renderer.name, "Body", StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            if (best.renderer != null)
            {
                result.VisemeMesh = best.renderer;
                result.Visemes = best.visemes;
            }

            var eyelidCandidates = new List<SkinnedMeshRenderer>();
            if (result.VisemeMesh != null) eyelidCandidates.Add(result.VisemeMesh);
            eyelidCandidates.AddRange(renderers.OrderByDescending(r => string.Equals(r.name, "Body", StringComparison.OrdinalIgnoreCase)));
            foreach (var renderer in eyelidCandidates)
            {
                var shapes = MatchEyelids(renderer.sharedMesh);
                if (shapes[0] < 0) continue;
                result.EyelidMesh = renderer;
                result.EyelidShapes = shapes;
                break;
            }
            return result;
        }

        static string Squash(string name)
        {
            return new string(name.Where(char.IsLetterOrDigit).ToArray()).ToLowerInvariant();
        }

        public static string[] MatchVisemes(SkinnedMeshRenderer renderer)
        {
            var mesh = renderer.sharedMesh;
            var names = mesh == null ? new string[0] : Enumerable.Range(0, mesh.blendShapeCount).Select(mesh.GetBlendShapeName).ToArray();
            return VisemeNames.Select(viseme =>
            {
                var aliases = viseme == "E" ? new[] { "e", "ee" } : new[] { viseme };
                foreach (var alias in aliases)
                foreach (var prefix in VisemePrefixes)
                {
                    var match = names.FirstOrDefault(n => string.Equals(n, prefix + alias, StringComparison.OrdinalIgnoreCase));
                    if (match != null) return match;
                }
                return null;
            }).ToArray();
        }

        public static int[] MatchEyelids(Mesh mesh)
        {
            int Find(string[] candidates)
            {
                if (mesh == null) return -1;
                foreach (var candidate in candidates)
                    for (var i = 0; i < mesh.blendShapeCount; i++)
                        if (string.Equals(mesh.GetBlendShapeName(i), candidate, StringComparison.OrdinalIgnoreCase))
                            return i;
                return -1;
            }
            return new[] { Find(BlinkNames), Find(LookUpNames), Find(LookDownNames) };
        }

        public static VRCAvatarDescriptor Apply(GameObject root, Detection detection, bool view, bool lipSync, bool eyes)
        {
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Avatar Descriptor Quick Setup");
            var descriptor = root.GetComponent<VRCAvatarDescriptor>();
            if (descriptor == null) descriptor = Undo.AddComponent<VRCAvatarDescriptor>(root);
            Undo.RecordObject(descriptor, "Avatar Descriptor Quick Setup");

            if (view && detection.ViewPosition.HasValue)
                descriptor.ViewPosition = detection.ViewPosition.Value;

            if (lipSync && detection.VisemeMesh != null)
            {
                descriptor.lipSync = LipSyncStyle.VisemeBlendShape;
                descriptor.VisemeSkinnedMesh = detection.VisemeMesh;
                descriptor.VisemeBlendShapes = detection.Visemes.Select(v => v ?? "").ToArray();
            }

            if (eyes && (detection.LeftEye != null || detection.RightEye != null))
            {
                descriptor.enableEyeLook = true;
                var settings = descriptor.customEyeLookSettings;
                settings.leftEye = detection.LeftEye;
                settings.rightEye = detection.RightEye;
                if (!EyeRotationsConfigured(settings))
                {
                    var rootTransform = root.transform;
                    settings.eyesLookingStraight = EyeRotation(rootTransform, detection, Vector3.zero);
                    settings.eyesLookingUp = EyeRotation(rootTransform, detection, new Vector3(-12, 0, 0));
                    settings.eyesLookingDown = EyeRotation(rootTransform, detection, new Vector3(12, 0, 0));
                    settings.eyesLookingLeft = EyeRotation(rootTransform, detection, new Vector3(0, -15, 0));
                    settings.eyesLookingRight = EyeRotation(rootTransform, detection, new Vector3(0, 15, 0));
                }
                if (detection.EyelidMesh != null && detection.EyelidShapes[0] >= 0)
                {
                    settings.eyelidType = VRCAvatarDescriptor.EyelidType.Blendshapes;
                    settings.eyelidsSkinnedMesh = detection.EyelidMesh;
                    settings.eyelidsBlendshapes = (int[])detection.EyelidShapes.Clone();
                }
                descriptor.customEyeLookSettings = settings;
            }

            EditorUtility.SetDirty(descriptor);
            Undo.CollapseUndoOperations(group);
            return descriptor;
        }

        // A fresh descriptor serializes every direction with the same (often zero) rotation; differing directions mean the user tuned them.
        static bool EyeRotationsConfigured(VRCAvatarDescriptor.CustomEyeLookSettings settings)
        {
            var all = new[] { settings.eyesLookingStraight, settings.eyesLookingUp, settings.eyesLookingDown, settings.eyesLookingLeft, settings.eyesLookingRight };
            if (all.Any(r => r == null)) return false;
            return all.Any(r => !r.left.Equals(all[0].left) || !r.right.Equals(all[0].right));
        }

        // VRChat stores eye directions as eye-bone local rotations; offsets are applied around the avatar's axes so they read as up/down/left/right regardless of bone orientation.
        static VRCAvatarDescriptor.CustomEyeLookSettings.EyeRotations EyeRotation(Transform root, Detection detection, Vector3 euler)
        {
            Quaternion Rotate(Transform eye)
            {
                if (eye == null) return Quaternion.identity;
                var world = Quaternion.AngleAxis(euler.x, root.right) * Quaternion.AngleAxis(euler.y, root.up) * eye.rotation;
                return eye.parent != null ? Quaternion.Inverse(eye.parent.rotation) * world : world;
            }
            var left = Rotate(detection.LeftEye);
            var right = Rotate(detection.RightEye);
            return new VRCAvatarDescriptor.CustomEyeLookSettings.EyeRotations
            {
                linked = Quaternion.Angle(left, right) < 0.01f, left = left, right = right
            };
        }
    }
}

using System.Linq;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace AvatarTools.Editor
{
    public class PrefabAttachHelper : EditorWindow
    {
        public enum OffsetSpace
        {
            BoneLocal,
            AvatarAligned
        }

        public enum OffsetScaling
        {
            None,
            BoneLength,
            AvatarHeight
        }

        public const string LibraryFolder = "Assets/AvatarTools/Presets/Attach";

        static readonly string[] SpaceLabels = { "Bone local (same rig)", "Avatar aligned (other rigs)" };
        static readonly string[] ScalingLabels = { "None", "Relative bone length", "Relative avatar height" };

        VRCAvatarDescriptor avatar;
        GameObject accessory;
        bool useCustomBone;
        HumanBodyBones humanBone = HumanBodyBones.Head;
        Transform customBone;
        OffsetSpace space;
        OffsetScaling scaling;
        bool useSavedOffset = true;
        bool createToggle;
        string toggleName = "";
        GameObject instance;
        bool reattachUseOffset;
        Vector2 scroll;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Setup/Prefab Attach Helper")]
        static void Open()
        {
            GetWindow<PrefabAttachHelper>("Prefab Attach Helper");
        }

        void OnEnable()
        {
            avatar = AvatarToolsUtil.SelectedAvatar();
        }

        void OnGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            avatar = AvatarToolsUtil.AvatarField(avatar);
            accessory = (GameObject)EditorGUILayout.ObjectField(new GUIContent("Accessory", "Prefab asset (instantiated with its prefab link) or a scene object (moved)."),
                accessory, typeof(GameObject), true);

            useCustomBone = EditorGUILayout.Toggle("Custom target transform", useCustomBone);
            if (useCustomBone) customBone = (Transform)EditorGUILayout.ObjectField("Target", customBone, typeof(Transform), true);
            else humanBone = (HumanBodyBones)EditorGUILayout.EnumPopup("Target bone", humanBone);

            space = (OffsetSpace)EditorGUILayout.Popup(new GUIContent("Offset space",
                "Bone local reproduces the pose exactly on the same rig. Avatar aligned ignores bone axis orientation, which differs between rigs."), (int)space, SpaceLabels);
            scaling = (OffsetScaling)EditorGUILayout.Popup("Offset scaling", (int)scaling, ScalingLabels);

            var bone = TargetBone();
            var key = accessory == null ? null : AccessoryKey(accessory);
            var saved = key == null || bone == null ? null : FindAttachPoint(key, BoneKey(avatar, bone));
            if (avatar == null)
            {
                EditorGUILayout.HelpBox("Pick an avatar.", MessageType.Info);
                EditorGUILayout.EndScrollView();
                return;
            }
            if (bone == null)
                EditorGUILayout.HelpBox(useCustomBone ? "Pick a target transform under the avatar." : $"The avatar has no {humanBone} bone (requires a humanoid rig).", MessageType.Warning);

            DrawAttach(bone, saved);
            EditorGUILayout.Space();
            DrawInstance(bone, saved);
            EditorGUILayout.EndScrollView();
        }

        void DrawAttach(Transform bone, AttachPoint saved)
        {
            EditorGUILayout.LabelField("Attach", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(saved == null))
                useSavedOffset = EditorGUILayout.ToggleLeft("Apply saved offset", useSavedOffset && saved != null);
            if (accessory != null && bone != null)
                EditorGUILayout.LabelField(saved != null
                    ? $"Saved offset found for \"{saved.accessoryName}\" on {saved.bone}."
                    : "No saved offset for this accessory and bone; the accessory's own transform is kept.", EditorStyles.miniLabel);

            createToggle = EditorGUILayout.ToggleLeft("Create toggle", createToggle);
            if (createToggle)
                toggleName = EditorGUILayout.TextField("Toggle name", string.IsNullOrEmpty(toggleName) && accessory != null ? accessory.name : toggleName);

            var error = ValidateAttach(avatar, accessory, bone);
            AnimatorToolsShared.HelpIfAny(error, MessageType.Info);
            using (new EditorGUI.DisabledScope(error != null))
                if (GUILayout.Button("Attach", GUILayout.Height(30)))
                {
                    var created = Attach(avatar, accessory, bone, useSavedOffset ? saved : null, space, scaling);
                    instance = created;
                    Selection.activeGameObject = created;
                    if (createToggle) CreateToggle(created);
                    GUIUtility.ExitGUI();
                }
        }

        void CreateToggle(GameObject target)
        {
            var name = string.IsNullOrWhiteSpace(toggleName) ? target.name : toggleName;
            var objects = new[] { target };
            var toggleError = ToggleGenerator.Validate(avatar, name, objects, null);
            if (toggleError != null)
            {
                EditorUtility.DisplayDialog("Create Toggle", "Attached, but the toggle was not created:\n\n" + toggleError, "OK");
                return;
            }
            ToggleGenerator.Generate(avatar, name, objects, null, true, true, "");
            ShowNotification(new GUIContent("Attached with toggle " + name.Trim()));
        }

        void DrawInstance(Transform bone, AttachPoint saved)
        {
            EditorGUILayout.LabelField("Attached instance", EditorStyles.boldLabel);
            instance = (GameObject)EditorGUILayout.ObjectField("Instance", instance, typeof(GameObject), true);
            if (instance != null && EditorUtility.IsPersistent(instance)) instance = null;
            if (instance == null)
            {
                EditorGUILayout.LabelField("Attach something or pick an accessory already in the scene to reattach it or save its offset.", EditorStyles.wordWrappedMiniLabel);
                return;
            }

            var instanceKey = AccessoryKey(instance);
            var instanceSaved = bone == null ? null : FindAttachPoint(instanceKey, BoneKey(avatar, bone));
            var error = ValidateAttach(avatar, instance, bone);
            AnimatorToolsShared.HelpIfAny(error, MessageType.Info);
            using (new EditorGUI.DisabledScope(error != null))
            {
                using (new EditorGUI.DisabledScope(instanceSaved == null))
                    reattachUseOffset = EditorGUILayout.ToggleLeft("Reattach using saved offset (otherwise keep world pose)", reattachUseOffset && instanceSaved != null);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Reattach to " + (bone == null ? "target" : bone.name)))
                        Reattach(avatar, instance, bone, reattachUseOffset ? instanceSaved : null, space, scaling);
                    if (GUILayout.Button("Save offset"))
                    {
                        var point = SaveOffset(avatar, instance, bone);
                        ShowNotification(new GUIContent("Saved " + point.name));
                    }
                }
            }
            if (instanceSaved != null && instance.transform.parent != bone)
                EditorGUILayout.LabelField("Save offset uses the selected target bone, not the instance's current parent.", EditorStyles.miniLabel);
        }

        Transform TargetBone()
        {
            if (avatar == null) return null;
            if (useCustomBone)
                return customBone != null && customBone.IsChildOf(avatar.transform) ? customBone : null;
            var animator = avatar.GetComponent<Animator>();
            return animator != null && animator.isHuman && humanBone != HumanBodyBones.LastBone ? animator.GetBoneTransform(humanBone) : null;
        }

        public static string ValidateAttach(VRCAvatarDescriptor avatar, GameObject accessory, Transform bone)
        {
            if (avatar == null) return "Pick an avatar.";
            if (accessory == null) return "Pick an accessory prefab or scene object.";
            if (bone == null || !bone.IsChildOf(avatar.transform)) return "Pick a target bone under the avatar.";
            if (EditorUtility.IsPersistent(accessory))
                return PrefabUtility.IsPartOfPrefabAsset(accessory) ? null : "The accessory asset is not a prefab or model.";
            if (accessory.transform == avatar.transform || avatar.transform.IsChildOf(accessory.transform)) return "The accessory cannot be the avatar or contain it.";
            if (bone.IsChildOf(accessory.transform)) return "The target bone is inside the accessory.";
            if (accessory.GetComponent<VRCAvatarDescriptor>() != null) return "The accessory has its own VRC Avatar Descriptor.";
            if (PrefabUtility.IsPartOfPrefabInstance(accessory) && !PrefabUtility.IsOutermostPrefabInstanceRoot(accessory))
                return "The accessory is inside another prefab instance and cannot be moved. Unpack it or pick the prefab root.";
            return null;
        }

        public static string AccessoryKey(GameObject accessory)
        {
            var path = EditorUtility.IsPersistent(accessory)
                ? AssetDatabase.GetAssetPath(accessory)
                : PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(accessory);
            var guid = string.IsNullOrEmpty(path) ? null : AssetDatabase.AssetPathToGUID(path);
            return string.IsNullOrEmpty(guid) ? "name:" + accessory.name : guid;
        }

        public static string BoneKey(VRCAvatarDescriptor avatar, Transform bone)
        {
            var animator = avatar == null ? null : avatar.GetComponent<Animator>();
            if (animator != null && animator.isHuman)
                for (var b = HumanBodyBones.Hips; b < HumanBodyBones.LastBone; b++)
                    if (animator.GetBoneTransform(b) == bone)
                        return b.ToString();
            return "custom:" + bone.name;
        }

        public static AttachPoint FindAttachPoint(string accessoryKey, string boneKey)
        {
            if (!AssetDatabase.IsValidFolder(LibraryFolder)) return null;
            return AssetDatabase.FindAssets("t:" + nameof(AttachPoint), new[] { LibraryFolder })
                .Select(g => AssetDatabase.LoadAssetAtPath<AttachPoint>(AssetDatabase.GUIDToAssetPath(g)))
                .FirstOrDefault(p => p != null && p.accessoryKey == accessoryKey && p.bone == boneKey);
        }

        public static float BoneLength(VRCAvatarDescriptor avatar, Transform bone)
        {
            var animator = avatar.GetComponent<Animator>();
            var length = 0f;
            if (animator != null && animator.isHuman)
                for (var b = HumanBodyBones.Hips; b < HumanBodyBones.LastBone; b++)
                {
                    var t = animator.GetBoneTransform(b);
                    if (t != null && t.parent == bone) length = Mathf.Max(length, Vector3.Distance(t.position, bone.position));
                }
            if (length <= 0 && bone.parent != null) length = Vector3.Distance(bone.position, bone.parent.position);
            return length / RootScale(avatar);
        }

        static float RootScale(VRCAvatarDescriptor avatar)
        {
            var s = avatar.transform.lossyScale.x;
            return Mathf.Approximately(s, 0) ? 1 : s;
        }

        public static float ScaleFactor(VRCAvatarDescriptor avatar, Transform bone, AttachPoint point, OffsetScaling scaling)
        {
            switch (scaling)
            {
                case OffsetScaling.BoneLength:
                    var length = BoneLength(avatar, bone);
                    return point.boneLength > 0 && length > 0 ? length / point.boneLength : 1;
                case OffsetScaling.AvatarHeight:
                    var height = avatar.ViewPosition.y;
                    return point.avatarHeight > 0 && height > 0 ? height / point.avatarHeight : 1;
                default:
                    return 1;
            }
        }

        public static AttachPoint SaveOffset(VRCAvatarDescriptor avatar, GameObject accessory, Transform bone)
        {
            var key = AccessoryKey(accessory);
            var boneKey = BoneKey(avatar, bone);
            var point = FindAttachPoint(key, boneKey);
            var isNew = point == null;
            if (isNew) point = CreateInstance<AttachPoint>();
            else Undo.RecordObject(point, "Save Attach Offset");

            var root = avatar.transform;
            var t = accessory.transform;
            var s = RootScale(avatar);
            point.accessoryKey = key;
            point.accessoryName = accessory.name;
            point.bone = boneKey;
            point.localPosition = bone.InverseTransformPoint(t.position);
            point.localRotation = Quaternion.Inverse(bone.rotation) * t.rotation;
            point.localScale = Divide(t.lossyScale, bone.lossyScale);
            point.alignedPosition = Quaternion.Inverse(root.rotation) * (t.position - bone.position) / s;
            point.alignedRotation = Quaternion.Inverse(root.rotation) * t.rotation;
            point.alignedScale = t.lossyScale / s;
            point.boneLength = BoneLength(avatar, bone);
            point.avatarHeight = avatar.ViewPosition.y;

            if (isNew) AvatarToolsUtil.CreateAsset(point, LibraryFolder, accessory.name + "_" + boneKey.Replace(':', '_'), "asset");
            EditorUtility.SetDirty(point);
            AssetDatabase.SaveAssets();
            return point;
        }

        public static void ApplyOffset(VRCAvatarDescriptor avatar, Transform accessory, Transform bone, AttachPoint point, OffsetSpace space, OffsetScaling scaling)
        {
            var factor = ScaleFactor(avatar, bone, point, scaling);
            var root = avatar.transform;
            var s = RootScale(avatar);
            Undo.RecordObject(accessory, "Apply Attach Offset");
            Vector3 worldScale;
            if (space == OffsetSpace.BoneLocal)
            {
                accessory.position = bone.TransformPoint(point.localPosition * factor);
                accessory.rotation = bone.rotation * point.localRotation;
                worldScale = Vector3.Scale(point.localScale * factor, bone.lossyScale);
            }
            else
            {
                accessory.position = bone.position + root.rotation * (point.alignedPosition * s * factor);
                accessory.rotation = root.rotation * point.alignedRotation;
                worldScale = point.alignedScale * s * factor;
            }
            accessory.localScale = accessory.parent == null ? worldScale : Divide(worldScale, accessory.parent.lossyScale);
        }

        static Vector3 Divide(Vector3 a, Vector3 b)
        {
            return new Vector3(SafeDivide(a.x, b.x), SafeDivide(a.y, b.y), SafeDivide(a.z, b.z));
        }

        static float SafeDivide(float a, float b)
        {
            return Mathf.Approximately(b, 0) ? a : a / b;
        }

        public static GameObject Attach(VRCAvatarDescriptor avatar, GameObject accessory, Transform bone, AttachPoint point, OffsetSpace space, OffsetScaling scaling)
        {
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Attach " + accessory.name);
            GameObject result;
            if (EditorUtility.IsPersistent(accessory))
            {
                var name = GameObjectUtility.GetUniqueNameForSibling(bone, accessory.name);
                result = (GameObject)PrefabUtility.InstantiatePrefab(accessory, bone);
                Undo.RegisterCreatedObjectUndo(result, "Attach " + accessory.name);
                result.name = name;
            }
            else
            {
                result = accessory;
                Undo.SetTransformParent(result.transform, bone, "Attach " + accessory.name);
            }
            if (point != null) ApplyOffset(avatar, result.transform, bone, point, space, scaling);
            Undo.CollapseUndoOperations(group);
            return result;
        }

        public static void Reattach(VRCAvatarDescriptor avatar, GameObject instance, Transform bone, AttachPoint point, OffsetSpace space, OffsetScaling scaling)
        {
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Reattach " + instance.name);
            if (instance.transform.parent != bone) Undo.SetTransformParent(instance.transform, bone, "Reattach " + instance.name);
            if (point != null) ApplyOffset(avatar, instance.transform, bone, point, space, scaling);
            Undo.CollapseUndoOperations(group);
        }
    }
}

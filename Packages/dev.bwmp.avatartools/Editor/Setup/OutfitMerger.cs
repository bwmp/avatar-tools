using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using VRC.SDK3.Avatars.Components;

namespace AvatarTools.Editor
{
    public class OutfitMerger : EditorWindow
    {
        public class BoneRow
        {
            public Transform OutfitBone;
            public Transform AvatarBone;
            public int Depth;
        }

        VRCAvatarDescriptor avatar;
        GameObject outfit;
        bool mergeBones;
        List<BoneRow> rows = new List<BoneRow>();
        Vector2 scroll;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Setup/Outfit Merger")]
        static void Open()
        {
            GetWindow<OutfitMerger>("Outfit Merger");
        }

        void OnEnable()
        {
            avatar = AvatarToolsUtil.SelectedAvatar();
        }

        void OnGUI()
        {
            var newAvatar = AvatarToolsUtil.AvatarField(avatar);
            var newOutfit = (GameObject)EditorGUILayout.ObjectField("Outfit", outfit, typeof(GameObject), true);
            if (newOutfit != null && EditorUtility.IsPersistent(newOutfit)) newOutfit = null;
            if (newAvatar != avatar || newOutfit != outfit)
            {
                avatar = newAvatar;
                outfit = newOutfit;
                Rebuild();
            }
            mergeBones = EditorGUILayout.ToggleLeft(new GUIContent("Merge bones into avatar armature",
                "Rebind skinned meshes to the avatar's bones and delete redundant outfit bones. Otherwise outfit bones are parented under the matching avatar bones."), mergeBones);

            var error = Validate(avatar, outfit);
            if (error != null)
            {
                EditorGUILayout.HelpBox(error, MessageType.Info);
                return;
            }
            if (GUILayout.Button("Re-match Bones")) Rebuild();
            if (rows.Count == 0)
            {
                EditorGUILayout.HelpBox("No outfit armature found: the outfit has no skinned meshes with bones.", MessageType.Warning);
                return;
            }

            var matched = rows.Count(r => r.AvatarBone != null);
            EditorGUILayout.LabelField($"{matched} of {rows.Count} outfit bones matched. Unmatched bones stay attached to their outfit parent.", EditorStyles.miniLabel);
            if (mergeBones)
                EditorGUILayout.HelpBox("Merge mode assumes the outfit was made for this avatar's rest pose. Outfit bones with extra children or components are kept and parented instead of deleted.", MessageType.None);

            scroll = EditorGUILayout.BeginScrollView(scroll);
            var unmatchedColor = new Color(1f, 0.75f, 0.4f);
            foreach (var row in rows)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    var previous = GUI.color;
                    if (row.AvatarBone == null) GUI.color = unmatchedColor;
                    EditorGUILayout.LabelField(new string(' ', row.Depth * 3) + row.OutfitBone.name, GUILayout.MinWidth(120));
                    GUI.color = previous;
                    row.AvatarBone = (Transform)EditorGUILayout.ObjectField(row.AvatarBone, typeof(Transform), true, GUILayout.MinWidth(120));
                    if (row.AvatarBone != null && (row.AvatarBone.IsChildOf(outfit.transform) || !row.AvatarBone.IsChildOf(avatar.transform)))
                        row.AvatarBone = null;
                }
            }
            EditorGUILayout.EndScrollView();

            if (GUILayout.Button(mergeBones ? "Merge Outfit" : "Attach Outfit", GUILayout.Height(30)))
            {
                Apply(avatar, outfit, rows.ToDictionary(r => r.OutfitBone, r => r.AvatarBone), mergeBones);
                rows.Clear();
                outfit = null;
            }
        }

        void Rebuild()
        {
            rows = Validate(avatar, outfit) == null ? BuildMapping(avatar, outfit) : new List<BoneRow>();
        }

        public static string Validate(VRCAvatarDescriptor avatar, GameObject outfit)
        {
            if (avatar == null) return "Pick an avatar.";
            if (outfit == null) return "Pick an outfit object in the scene that has its own armature.";
            if (outfit == avatar.gameObject || avatar.transform.IsChildOf(outfit.transform)) return "The outfit cannot be the avatar or contain it.";
            if (outfit.GetComponent<VRCAvatarDescriptor>() != null) return "The outfit has its own VRC Avatar Descriptor. Remove it first.";
            return null;
        }

        public static Transform FindOutfitArmature(GameObject outfit)
        {
            var bones = outfit.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .SelectMany(r => r.bones.Append(r.rootBone))
                .Where(b => b != null && b != outfit.transform && b.IsChildOf(outfit.transform))
                .ToList();
            if (bones.Count == 0) return null;
            var tops = bones.Select(b =>
            {
                var t = b;
                while (t.parent != outfit.transform) t = t.parent;
                return t;
            });
            return tops.GroupBy(t => t).OrderByDescending(g => g.Count()).First().Key;
        }

        public static List<BoneRow> BuildMapping(VRCAvatarDescriptor avatar, GameObject outfit)
        {
            var rows = new List<BoneRow>();
            var armature = FindOutfitArmature(outfit);
            if (armature == null) return rows;

            var avatarLookup = new Dictionary<string, Transform>();
            foreach (var t in avatar.GetComponentsInChildren<Transform>(true))
            {
                if (t == avatar.transform || t.IsChildOf(outfit.transform)) continue;
                var key = Normalize(t.name, null);
                if (!avatarLookup.ContainsKey(key)) avatarLookup[key] = t;
            }

            var humanMap = new Dictionary<Transform, Transform>();
            var avatarAnimator = avatar.GetComponent<Animator>();
            var outfitAnimator = outfit.GetComponent<Animator>();
            if (avatarAnimator != null && avatarAnimator.isHuman && outfitAnimator != null && outfitAnimator.isHuman)
            {
                for (var bone = HumanBodyBones.Hips; bone < HumanBodyBones.LastBone; bone++)
                {
                    var from = outfitAnimator.GetBoneTransform(bone);
                    var to = avatarAnimator.GetBoneTransform(bone);
                    if (from != null && to != null) humanMap[from] = to;
                }
            }

            void Visit(Transform t, int depth)
            {
                if (!humanMap.TryGetValue(t, out var match)) avatarLookup.TryGetValue(Normalize(t.name, outfit.name), out match);
                rows.Add(new BoneRow { OutfitBone = t, AvatarBone = match, Depth = depth });
                foreach (Transform child in t) Visit(child, depth + 1);
            }
            Visit(armature, 0);
            return rows;
        }

        public static string Normalize(string name, string outfitName)
        {
            var s = name;
            var separator = Mathf.Max(s.LastIndexOf(':'), s.LastIndexOf('|'));
            if (separator >= 0 && separator < s.Length - 1) s = s.Substring(separator + 1);
            if (!string.IsNullOrEmpty(outfitName))
            {
                var stripped = Regex.Replace(s, Regex.Escape(outfitName), "", RegexOptions.IgnoreCase);
                if (stripped.Trim(' ', '_', '.', '-').Length > 0) s = stripped;
            }
            s = s.ToLowerInvariant().Trim(' ', '_', '.', '-');

            var side = "";
            var match = Regex.Match(s, @"^(.+?)[\s._-]+(l|r|left|right)$");
            if (match.Success)
            {
                side = match.Groups[2].Value.Substring(0, 1);
                s = match.Groups[1].Value;
            }
            else if ((match = Regex.Match(s, @"^(l|r)[\s._-]+(.+)$")).Success)
            {
                side = match.Groups[1].Value;
                s = match.Groups[2].Value;
            }
            else if ((match = Regex.Match(s, "left|right")).Success)
            {
                side = match.Value.Substring(0, 1);
                s = s.Remove(match.Index, match.Length);
            }
            s = Regex.Replace(s, @"[\s._-]", "");
            if (s.StartsWith("armature") && s.Length > "armature".Length) s = s.Substring("armature".Length);
            return side + ":" + s;
        }

        public static void Apply(VRCAvatarDescriptor avatar, GameObject outfit, Dictionary<Transform, Transform> mapping, bool merge)
        {
            Undo.IncrementCurrentGroup();
            var group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Merge Outfit");
            var outfitName = outfit.name;
            var matched = mapping.Where(p => p.Key != null && p.Value != null).ToDictionary(p => p.Key, p => p.Value);

            if (outfit.transform.parent != avatar.transform)
                Undo.SetTransformParent(outfit.transform, avatar.transform, "Merge Outfit");
            var animator = outfit.GetComponent<Animator>();
            if (animator != null) Undo.DestroyObjectImmediate(animator);

            var deletable = new HashSet<Transform>();
            if (merge)
            {
                foreach (var renderer in outfit.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                {
                    Undo.RecordObject(renderer, "Merge Outfit");
                    renderer.bones = renderer.bones.Select(b => b != null && matched.TryGetValue(b, out var to) ? to : b).ToArray();
                    if (renderer.rootBone != null && matched.TryGetValue(renderer.rootBone, out var root)) renderer.rootBone = root;
                }
                bool CanDelete(Transform t) =>
                    matched.ContainsKey(t) && t.GetComponents<Component>().Length == 1 && t.Cast<Transform>().All(CanDelete);
                foreach (var bone in matched.Keys)
                    if (CanDelete(bone)) deletable.Add(bone);
            }

            foreach (var pair in matched)
            {
                if (deletable.Contains(pair.Key)) continue;
                Undo.SetTransformParent(pair.Key, pair.Value, "Merge Outfit");
                Undo.RecordObject(pair.Key.gameObject, "Merge Outfit");
                pair.Key.name = GameObjectUtility.GetUniqueNameForSibling(pair.Value, pair.Value.name + "_" + outfitName);
            }
            foreach (var bone in deletable.Where(b => b.parent == null || !deletable.Contains(b.parent)).ToList())
                Undo.DestroyObjectImmediate(bone.gameObject);

            Undo.CollapseUndoOperations(group);
        }
    }
}

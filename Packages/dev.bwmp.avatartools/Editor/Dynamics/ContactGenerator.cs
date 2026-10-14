using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.Dynamics;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Contact.Components;
using AnimLayerType = VRC.SDK3.Avatars.Components.VRCAvatarDescriptor.AnimLayerType;
using ReceiverType = VRC.Dynamics.ContactReceiver.ReceiverType;
using ShapeType = VRC.Dynamics.ContactBase.ShapeType;

namespace AvatarTools.Editor
{
    public enum ContactPreset { Headpat, Boop, HighFive, Custom }

    public sealed class ContactSpec
    {
        public string name;
        public bool sender;
        public HumanBodyBones bone;
        public Vector3 offset;
        public ShapeType shape;
        public float radius;
        public float height;
        public List<string> tags = new List<string>();
        public ReceiverType receiverType;
        public bool allowSelf;
        public bool allowOthers = true;
        public string parameter;
    }

    public sealed class ContactGenerator : EditorWindow
    {
        static readonly string[] BuiltInTags =
        {
            "Head", "Torso", "Hand", "HandL", "HandR", "Foot", "FootL", "FootR", "Finger", "FingerL", "FingerR",
            "FingerIndex", "FingerIndexL", "FingerIndexR", "FingerMiddle", "FingerMiddleL", "FingerMiddleR",
            "FingerRing", "FingerRingL", "FingerRingR", "FingerLittle", "FingerLittleL", "FingerLittleR",
        };

        static readonly Color ReceiverColor = new Color(0.3f, 0.8f, 1f);
        static readonly Color SenderColor = new Color(1f, 0.6f, 0.2f);

        VRCAvatarDescriptor avatar;
        ContactPreset preset;
        bool sender;
        HumanBodyBones bone = HumanBodyBones.Head;
        Vector3 offset;
        ShapeType shape;
        float radius;
        float height;
        string tags;
        ReceiverType receiverType;
        bool allowSelf;
        bool allowOthers = true;
        string parameter;
        bool addFxParameter = true;
        AnimationClip clip;
        Vector2 scroll;

        [MenuItem(AvatarToolsUtil.MenuRoot + "Dynamics/Contact Generator")]
        static void Open()
        {
            GetWindow<ContactGenerator>("Contact Generator");
        }

        void OnEnable()
        {
            if (avatar == null) avatar = AvatarToolsUtil.SelectedAvatar();
            if (string.IsNullOrEmpty(parameter)) LoadPreset(ContactPreset.Headpat);
            SceneView.duringSceneGui += OnSceneGUI;
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            SceneView.RepaintAll();
        }

        public static float AvatarScale(VRCAvatarDescriptor avatar)
        {
            var eyeHeight = avatar.ViewPosition.y * avatar.transform.lossyScale.y;
            return eyeHeight > 0.01f ? eyeHeight / 1.5f : 1f;
        }

        void LoadPreset(ContactPreset value)
        {
            preset = value;
            var scale = avatar != null ? AvatarScale(avatar) : 1f;
            switch (value)
            {
                case ContactPreset.Headpat:
                    SetFields(HumanBodyBones.Head, new Vector3(0, 0.12f, 0) * scale, 0.15f * scale, "Hand, Finger",
                        ReceiverType.Constant, false, "Headpat");
                    break;
                case ContactPreset.Boop:
                    SetFields(HumanBodyBones.Head, new Vector3(0, 0.05f, 0.1f) * scale, 0.03f * scale, "Finger",
                        ReceiverType.OnEnter, true, "Boop");
                    break;
                case ContactPreset.HighFive:
                    SetFields(HumanBodyBones.RightHand, Vector3.zero, 0.1f * scale, "HighFive",
                        ReceiverType.OnEnter, false, "HighFive");
                    break;
            }
        }

        void SetFields(HumanBodyBones targetBone, Vector3 targetOffset, float targetRadius, string targetTags,
            ReceiverType type, bool self, string parameterName)
        {
            sender = false;
            bone = targetBone;
            offset = targetOffset;
            shape = ShapeType.Sphere;
            radius = targetRadius;
            height = targetRadius * 4;
            tags = targetTags;
            receiverType = type;
            allowSelf = self;
            allowOthers = true;
            parameter = parameterName;
        }

        List<ContactSpec> BuildSpecs()
        {
            var tagList = ParseTags(tags);
            if (preset != ContactPreset.HighFive)
            {
                return new List<ContactSpec>
                {
                    new ContactSpec
                    {
                        name = sender ? string.Join("_", tagList) : parameter, sender = sender, bone = bone, offset = offset,
                        shape = shape, radius = radius, height = height, tags = tagList, receiverType = receiverType,
                        allowSelf = allowSelf, allowOthers = allowOthers, parameter = parameter
                    }
                };
            }
            var specs = new List<ContactSpec>();
            foreach (var side in new[] { (HumanBodyBones.LeftHand, "_L"), (HumanBodyBones.RightHand, "_R") })
            {
                specs.Add(new ContactSpec
                {
                    name = parameter + side.Item2, sender = true, bone = side.Item1, shape = ShapeType.Sphere,
                    radius = radius, tags = tagList
                });
                specs.Add(new ContactSpec
                {
                    name = parameter + side.Item2, bone = side.Item1, shape = ShapeType.Sphere, radius = radius,
                    tags = tagList, receiverType = receiverType, allowSelf = allowSelf, allowOthers = allowOthers,
                    parameter = parameter + side.Item2
                });
            }
            return specs;
        }

        public static List<string> ParseTags(string text)
        {
            return (text ?? "").Split(',').Select(t => t.Trim()).Where(t => t.Length > 0).Distinct().ToList();
        }

        public static AnimatorControllerParameterType ParameterType(ReceiverType type)
        {
            return type == ReceiverType.Proximity ? AnimatorControllerParameterType.Float : AnimatorControllerParameterType.Bool;
        }

        public static List<string> Validate(VRCAvatarDescriptor avatar, IReadOnlyList<ContactSpec> specs, bool fxHook)
        {
            var errors = new List<string>();
            var animator = avatar.GetComponent<Animator>();
            if (animator == null || !animator.isHuman)
            {
                errors.Add("The avatar needs a humanoid Animator to place contacts on bones.");
                return errors;
            }
            foreach (var spec in specs)
            {
                if (animator.GetBoneTransform(spec.bone) == null) errors.Add($"The avatar has no {spec.bone} bone.");
                if (spec.radius <= 0) errors.Add("Radius must be greater than zero.");
                if (spec.shape == ShapeType.Capsule && spec.height < spec.radius * 2) errors.Add("Capsule height must be at least twice the radius.");
                if (spec.tags.Count == 0) errors.Add("Add at least one collision tag.");
                if (!spec.sender && string.IsNullOrWhiteSpace(spec.parameter)) errors.Add("Receivers need a parameter name.");
                if (!spec.sender && !spec.allowSelf && !spec.allowOthers) errors.Add("Receivers must allow self, others, or both.");
            }
            var fx = AvatarToolsUtil.GetLayerController(avatar, AnimLayerType.FX);
            if (fxHook && fx != null)
            {
                foreach (var spec in specs.Where(s => !s.sender))
                {
                    var existing = fx.parameters.FirstOrDefault(p => p.name == spec.parameter);
                    if (existing != null && existing.type != ParameterType(spec.receiverType))
                        errors.Add($"FX parameter '{spec.parameter}' already exists as {existing.type}; {spec.receiverType} receivers need {ParameterType(spec.receiverType)}.");
                }
            }
            return errors.Distinct().ToList();
        }

        public static Vector3 WorldPosition(VRCAvatarDescriptor avatar, Transform boneTransform, Vector3 offset)
        {
            return boneTransform.position + avatar.transform.rotation * offset;
        }

        public static List<ContactBase> Create(VRCAvatarDescriptor avatar, IReadOnlyList<ContactSpec> specs)
        {
            var errors = Validate(avatar, specs, false);
            if (errors.Count > 0) throw new InvalidOperationException(string.Join("\n", errors));
            var animator = avatar.GetComponent<Animator>();
            var created = new List<ContactBase>();
            foreach (var spec in specs)
            {
                var boneTransform = animator.GetBoneTransform(spec.bone);
                var objectName = (spec.sender ? "Sender_" : "Receiver_") + spec.name;
                var child = boneTransform.Find(objectName);
                GameObject go;
                if (child == null)
                {
                    go = new GameObject(objectName);
                    Undo.RegisterCreatedObjectUndo(go, "Create Contact");
                    Undo.SetTransformParent(go.transform, boneTransform, "Create Contact");
                }
                else
                {
                    go = child.gameObject;
                    foreach (var old in go.GetComponents<ContactBase>()) Undo.DestroyObjectImmediate(old);
                }
                Undo.RecordObject(go.transform, "Create Contact");
                go.transform.SetPositionAndRotation(WorldPosition(avatar, boneTransform, spec.offset), avatar.transform.rotation);
                go.transform.localScale = Vector3.one;

                var lossy = go.transform.lossyScale;
                var scale = Mathf.Max(Mathf.Abs(lossy.x), Mathf.Abs(lossy.y), Mathf.Abs(lossy.z));
                ContactBase contact;
                if (spec.sender) contact = Undo.AddComponent<VRCContactSender>(go);
                else
                {
                    var receiver = Undo.AddComponent<VRCContactReceiver>(go);
                    receiver.receiverType = spec.receiverType;
                    receiver.allowSelf = spec.allowSelf;
                    receiver.allowOthers = spec.allowOthers;
                    receiver.parameter = spec.parameter;
                    contact = receiver;
                }
                contact.shapeType = spec.shape;
                contact.radius = spec.radius / scale;
                contact.height = spec.height / scale;
                contact.size = Vector3.one * (spec.radius * 2 / scale);
                contact.position = Vector3.zero;
                contact.rotation = Quaternion.identity;
                contact.collisionTags = new List<string>(spec.tags);
                EditorUtility.SetDirty(contact);
                created.Add(contact);
            }
            return created;
        }

        public static void AddFxHook(VRCAvatarDescriptor avatar, string parameterName, ReceiverType type, AnimationClip activeClip)
        {
            var fx = AvatarToolsUtil.GetOrCreateLayerController(avatar, AnimLayerType.FX);
            var parameterType = ParameterType(type);
            var existing = fx.parameters.FirstOrDefault(p => p.name == parameterName);
            if (existing != null && existing.type != parameterType)
                throw new InvalidOperationException($"FX parameter '{parameterName}' already exists as {existing.type}.");
            Undo.RecordObject(fx, "Add Contact FX Hook");
            AvatarToolsUtil.EnsureAnimatorParameter(fx, parameterName, parameterType);
            if (activeClip != null) BuildLayer(avatar, fx, parameterName, type, activeClip);
            EditorUtility.SetDirty(fx);
            AssetDatabase.SaveAssets();
        }

        static void BuildLayer(VRCAvatarDescriptor avatar, AnimatorController fx, string parameterName, ReceiverType type, AnimationClip activeClip)
        {
            var writeDefaults = AvatarToolsUtil.DetectWriteDefaults(avatar);
            var layer = AvatarToolsUtil.ReplaceLayer(fx, "Contact " + parameterName);
            var machine = layer.stateMachine;
            if (type == ReceiverType.Proximity)
            {
                var proximity = AvatarToolsUtil.AddState(machine, "Proximity", activeClip, new Vector3(300, 100), writeDefaults);
                proximity.timeParameterActive = true;
                proximity.timeParameter = parameterName;
                return;
            }

            var idle = AvatarToolsUtil.AddState(machine, "Idle", CreateResetClip(avatar, parameterName, activeClip), new Vector3(300, 100), writeDefaults);
            var active = AvatarToolsUtil.AddState(machine, "Active", activeClip, new Vector3(300, 200), writeDefaults);
            AvatarToolsUtil.AddInstantTransition(idle, active).AddCondition(AnimatorConditionMode.If, 0, parameterName);
            var exit = AvatarToolsUtil.AddInstantTransition(active, idle);
            if (type == ReceiverType.OnEnter)
            {
                exit.hasExitTime = true;
                exit.exitTime = 1;
            }
            else exit.AddCondition(AnimatorConditionMode.IfNot, 0, parameterName);
        }

        // The idle state holds the animated properties at their current scene values so the layer also works with Write Defaults off.
        static AnimationClip CreateResetClip(VRCAvatarDescriptor avatar, string parameterName, AnimationClip activeClip)
        {
            var path = $"{AvatarToolsUtil.AvatarFolder(avatar, "Contacts")}/{AvatarToolsUtil.SafeName(parameterName)}_Idle.anim";
            var reset = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (reset == null)
            {
                reset = new AnimationClip();
                AssetDatabase.CreateAsset(reset, path);
            }
            else reset.ClearCurves();
            var root = avatar.gameObject;
            foreach (var binding in AnimationUtility.GetCurveBindings(activeClip))
                if (AnimationUtility.GetFloatValue(root, binding, out var value))
                    AnimationUtility.SetEditorCurve(reset, binding, AnimationCurve.Constant(0, 1 / 60f, value));
            foreach (var binding in AnimationUtility.GetObjectReferenceCurveBindings(activeClip))
                if (AnimationUtility.GetObjectReferenceValue(root, binding, out var value))
                    AnimationUtility.SetObjectReferenceCurve(reset, binding, new[] { new ObjectReferenceKeyframe { time = 0, value = value } });
            EditorUtility.SetDirty(reset);
            return reset;
        }

        void OnGUI()
        {
            EditorGUI.BeginChangeCheck();
            avatar = AvatarToolsUtil.AvatarField(avatar);
            if (avatar == null)
            {
                EditorGUILayout.HelpBox("Select an avatar with a VRC Avatar Descriptor.", MessageType.Info);
                return;
            }

            scroll = EditorGUILayout.BeginScrollView(scroll);
            var selectedPreset = (ContactPreset)GUILayout.Toolbar((int)preset, new[] { "Headpat", "Boop", "High Five", "Custom" });
            if (selectedPreset != preset) LoadPreset(selectedPreset);
            EditorGUILayout.Space();

            var highFive = preset == ContactPreset.HighFive;
            var custom = preset == ContactPreset.Custom;
            if (highFive)
                EditorGUILayout.HelpBox("Adds a sender and a receiver to each hand. Receivers only react to other players' senders using the same tag.", MessageType.None);
            if (custom)
                sender = EditorGUILayout.Popup("Component", sender ? 1 : 0, new[] { "Receiver", "Sender" }) == 1;

            using (new EditorGUI.DisabledScope(highFive))
                bone = (HumanBodyBones)EditorGUILayout.EnumPopup(new GUIContent("Bone"), bone, b => (HumanBodyBones)b != HumanBodyBones.LastBone, false);
            if (!highFive)
                offset = EditorGUILayout.Vector3Field(new GUIContent("Offset", "Avatar-space offset from the bone, in meters."), offset);
            using (new EditorGUI.DisabledScope(highFive))
                shape = (ShapeType)EditorGUILayout.EnumPopup("Shape", highFive ? ShapeType.Sphere : shape);
            radius = shape == ShapeType.Box && !highFive
                ? EditorGUILayout.FloatField(new GUIContent("Half Size", "Half the cube's edge length, in meters."), radius)
                : EditorGUILayout.FloatField(new GUIContent("Radius", "World-space radius in meters."), radius);
            if (shape == ShapeType.Capsule && !highFive)
                height = EditorGUILayout.FloatField(new GUIContent("Height", "Total capsule height in meters."), height);

            using (new EditorGUILayout.HorizontalScope())
            {
                tags = EditorGUILayout.TextField(new GUIContent("Collision Tags", "Comma separated."), tags);
                if (EditorGUILayout.DropdownButton(new GUIContent("Add"), FocusType.Keyboard, GUILayout.Width(50)))
                {
                    var menu = new GenericMenu();
                    var current = ParseTags(tags);
                    foreach (var tag in BuiltInTags)
                    {
                        var captured = tag;
                        menu.AddItem(new GUIContent(tag), current.Contains(tag), () =>
                        {
                            var list = ParseTags(tags);
                            if (!list.Remove(captured)) list.Add(captured);
                            tags = string.Join(", ", list);
                            Repaint();
                        });
                    }
                    menu.ShowAsContext();
                }
            }

            var isReceiver = highFive || !sender;
            if (isReceiver)
            {
                receiverType = (ReceiverType)EditorGUILayout.EnumPopup("Receiver Type", receiverType);
                allowSelf = EditorGUILayout.Toggle("Allow Self", allowSelf);
                allowOthers = EditorGUILayout.Toggle("Allow Others", allowOthers);
                parameter = EditorGUILayout.TextField(highFive ? "Parameter Prefix" : "Parameter", parameter);
                if (highFive)
                    EditorGUILayout.LabelField(" ", $"{parameter}_L, {parameter}_R", EditorStyles.miniLabel);

                EditorGUILayout.Space();
                EditorGUILayout.LabelField("FX Hook", EditorStyles.boldLabel);
                addFxParameter = EditorGUILayout.Toggle(new GUIContent("Add FX Parameter",
                    $"Adds a local {ParameterType(receiverType)} parameter to the FX controller. Not added to Expression Parameters."), addFxParameter);
                using (new EditorGUI.DisabledScope(!addFxParameter))
                    clip = (AnimationClip)EditorGUILayout.ObjectField(new GUIContent("Clip While Active",
                        receiverType == ReceiverType.Proximity ? "Proximity drives the clip's normalized time." : "Optional FX layer that plays this clip while the contact is active."),
                        clip, typeof(AnimationClip), false);
            }
            EditorGUILayout.EndScrollView();
            if (EditorGUI.EndChangeCheck()) SceneView.RepaintAll();

            var specs = BuildSpecs();
            var fxHook = isReceiver && addFxParameter;
            var errors = Validate(avatar, specs, fxHook);
            foreach (var error in errors) EditorGUILayout.HelpBox(error, MessageType.Error);
            using (new EditorGUI.DisabledScope(errors.Count > 0))
            {
                if (GUILayout.Button(specs.Count == 1 ? "Create Contact" : $"Create {specs.Count} Contacts", GUILayout.Height(28)))
                {
                    var created = Create(avatar, specs);
                    if (fxHook)
                        foreach (var spec in specs.Where(s => !s.sender))
                            AddFxHook(avatar, spec.parameter, spec.receiverType, clip);
                    Selection.objects = created.Select(c => (UnityEngine.Object)c.gameObject).ToArray();
                }
            }
        }

        void OnSceneGUI(SceneView view)
        {
            if (avatar == null || Event.current.type != EventType.Repaint) return;
            var animator = avatar.GetComponent<Animator>();
            if (animator == null || !animator.isHuman) return;
            foreach (var spec in BuildSpecs())
            {
                var boneTransform = animator.GetBoneTransform(spec.bone);
                if (boneTransform == null || spec.radius <= 0) continue;
                var center = WorldPosition(avatar, boneTransform, spec.offset);
                var rotation = avatar.transform.rotation;
                Handles.color = spec.sender ? SenderColor : ReceiverColor;
                if (spec.shape == ShapeType.Capsule)
                {
                    var up = rotation * Vector3.up * Mathf.Max(0, spec.height / 2 - spec.radius);
                    DrawWireSphere(center + up, rotation, spec.radius);
                    DrawWireSphere(center - up, rotation, spec.radius);
                    foreach (var side in new[] { Vector3.right, Vector3.left, Vector3.forward, Vector3.back })
                    {
                        var edge = rotation * side * spec.radius;
                        Handles.DrawLine(center + up + edge, center - up + edge);
                    }
                }
                else if (spec.shape == ShapeType.Box)
                {
                    using (new Handles.DrawingScope(Matrix4x4.TRS(center, rotation, Vector3.one)))
                        Handles.DrawWireCube(Vector3.zero, Vector3.one * spec.radius * 2);
                }
                else DrawWireSphere(center, rotation, spec.radius);
            }
        }

        static void DrawWireSphere(Vector3 center, Quaternion rotation, float sphereRadius)
        {
            Handles.DrawWireDisc(center, rotation * Vector3.up, sphereRadius);
            Handles.DrawWireDisc(center, rotation * Vector3.right, sphereRadius);
            Handles.DrawWireDisc(center, rotation * Vector3.forward, sphereRadius);
        }
    }
}

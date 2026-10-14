using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;

namespace AvatarTools.Editor
{
    public class LightingPreviewRig : EditorWindow
    {
        public enum Preset
        {
            BrightDaylight,
            Overcast,
            DarkClub,
            Night,
            Backlit,
            AmbientOnly
        }

        [Serializable]
        public class ObjectRef
        {
            public bool isSet;
            public int instanceId;
            public string globalId;
        }

        [Serializable]
        public class SavedState
        {
            public string sessionToken;
            public string scenePath;
            public AmbientMode ambientMode;
            public Color ambientSkyColor;
            public Color ambientEquatorColor;
            public Color ambientGroundColor;
            public float ambientIntensity;
            public float[] ambientProbe;
            public float reflectionIntensity;
            public ObjectRef skybox;
            public ObjectRef sun;
            public bool fog;
            public Color fogColor;
            public FogMode fogMode;
            public float fogDensity;
            public float fogStartDistance;
            public float fogEndDistance;
            public List<ObjectRef> disabledLights = new List<ObjectRef>();
            public Preset preset;
            public float yaw;
            public float intensityMultiplier = 1;
        }

        struct LightDef
        {
            public LightType Type;
            public Color Color;
            public float Intensity;
            public Vector3 Euler;
            public Vector3 Position;
            public float Range;
            public LightShadows Shadows;
        }

        class PresetDef
        {
            public string Label;
            public AmbientMode Mode;
            public Color Sky, Equator, Ground;
            public bool KeepSkybox;
            public float Reflection;
            public LightDef[] Lights;
        }

        public const string RigName = "__AvatarToolsLightRig";
        const string SessionKey = "AvatarTools.LightingPreviewRig.Session";
        static string PrefsKey => "AvatarTools.LightingPreviewRig.State." + Application.dataPath;

        static LightDef Directional(Color color, float intensity, Vector3 euler, LightShadows shadows) =>
            new LightDef { Type = LightType.Directional, Color = color, Intensity = intensity, Euler = euler, Shadows = shadows };

        static LightDef Point(Color color, float intensity, Vector3 position, float range) =>
            new LightDef { Type = LightType.Point, Color = color, Intensity = intensity, Position = position, Range = range, Shadows = LightShadows.None };

        static readonly Dictionary<Preset, PresetDef> Presets = new Dictionary<Preset, PresetDef>
        {
            [Preset.BrightDaylight] = new PresetDef
            {
                Label = "Bright Daylight", Mode = AmbientMode.Trilight, KeepSkybox = true, Reflection = 1,
                Sky = new Color(0.55f, 0.65f, 0.8f), Equator = new Color(0.45f, 0.45f, 0.42f), Ground = new Color(0.25f, 0.22f, 0.2f),
                Lights = new[] { Directional(new Color(1f, 0.96f, 0.88f), 1.3f, new Vector3(50, 150, 0), LightShadows.Soft) }
            },
            [Preset.Overcast] = new PresetDef
            {
                Label = "Overcast / Flat", Mode = AmbientMode.Flat, KeepSkybox = true, Reflection = 0.6f,
                Sky = new Color(0.62f, 0.63f, 0.66f),
                Lights = new[] { Directional(new Color(0.85f, 0.88f, 0.95f), 0.35f, new Vector3(80, 180, 0), LightShadows.None) }
            },
            [Preset.DarkClub] = new PresetDef
            {
                Label = "Dark Club (colored point lights)", Mode = AmbientMode.Flat, KeepSkybox = false, Reflection = 0.1f,
                Sky = new Color(0.02f, 0.02f, 0.035f),
                Lights = new[]
                {
                    Point(new Color(1f, 0.1f, 0.8f), 2.5f, new Vector3(1.5f, 2f, 1f), 6),
                    Point(new Color(0.1f, 0.9f, 1f), 2.5f, new Vector3(-1.5f, 1.8f, 1f), 6),
                    Point(new Color(0.25f, 0.2f, 1f), 2.5f, new Vector3(0, 2.5f, -1.5f), 6)
                }
            },
            [Preset.Night] = new PresetDef
            {
                Label = "Night (very dark ambient)", Mode = AmbientMode.Flat, KeepSkybox = false, Reflection = 0.05f,
                Sky = new Color(0.015f, 0.018f, 0.03f),
                Lights = new[] { Directional(new Color(0.55f, 0.62f, 0.85f), 0.12f, new Vector3(35, 200, 0), LightShadows.Soft) }
            },
            [Preset.Backlit] = new PresetDef
            {
                Label = "Backlit / Rim", Mode = AmbientMode.Flat, KeepSkybox = true, Reflection = 0.4f,
                Sky = new Color(0.12f, 0.12f, 0.14f),
                Lights = new[]
                {
                    Directional(new Color(1f, 0.95f, 0.9f), 1.6f, new Vector3(15, 0, 0), LightShadows.Soft),
                    Directional(new Color(0.6f, 0.7f, 0.9f), 0.15f, new Vector3(10, 180, 0), LightShadows.None)
                }
            },
            [Preset.AmbientOnly] = new PresetDef
            {
                Label = "No realtime light (ambient only)", Mode = AmbientMode.Trilight, KeepSkybox = true, Reflection = 1,
                Sky = new Color(0.6f, 0.62f, 0.68f), Equator = new Color(0.5f, 0.5f, 0.5f), Ground = new Color(0.3f, 0.3f, 0.3f),
                Lights = Array.Empty<LightDef>()
            }
        };

        Preset preset;
        float yaw;
        float intensityMultiplier = 1;
        Transform center;
        List<string> lastProblems = new List<string>();

        [MenuItem(AvatarToolsUtil.MenuRoot + "Setup/Lighting Preview Rig")]
        static void Open()
        {
            GetWindow<LightingPreviewRig>("Lighting Preview Rig");
        }

        void OnEnable()
        {
            var avatar = AvatarToolsUtil.SelectedAvatar();
            if (avatar != null) center = avatar.transform;
            var state = LoadState();
            if (state == null) return;
            preset = state.preset;
            yaw = state.yaw;
            intensityMultiplier = state.intensityMultiplier;
        }

        void OnGUI()
        {
            var state = LoadState();
            var scene = SceneManager.GetActiveScene();
            var sceneMismatch = state != null && state.scenePath != scene.path;

            center = (Transform)EditorGUILayout.ObjectField(new GUIContent("Center on", "The rig is placed at this object's position (usually the avatar)."), center, typeof(Transform), true);

            EditorGUILayout.LabelField("Environment", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(sceneMismatch))
            {
                foreach (var pair in Presets)
                {
                    var active = state != null && state.preset == pair.Key && FindRig(scene) != null;
                    if (GUILayout.Toggle(active, pair.Value.Label, "Button") && !active)
                    {
                        preset = pair.Key;
                        Apply(preset, yaw, intensityMultiplier, center);
                    }
                }

                EditorGUI.BeginChangeCheck();
                yaw = EditorGUILayout.Slider("Key light rotation", yaw, 0, 360);
                intensityMultiplier = EditorGUILayout.Slider("Intensity multiplier", intensityMultiplier, 0, 3);
                if (EditorGUI.EndChangeCheck() && state != null && FindRig(scene) != null)
                    Apply(state.preset, yaw, intensityMultiplier, center);
            }

            EditorGUILayout.Space();
            if (state == null)
            {
                EditorGUILayout.HelpBox("Scene lighting is untouched. Picking an environment saves the scene's render settings and disables its lights until you restore.", MessageType.None);
            }
            else if (sceneMismatch)
            {
                EditorGUILayout.HelpBox($"Saved original lighting belongs to \"{(string.IsNullOrEmpty(state.scenePath) ? "an unsaved scene" : state.scenePath)}\", not the active scene. " +
                                        "Open that scene and restore, or discard the saved state.", MessageType.Warning);
                if (GUILayout.Button("Discard saved state") &&
                    EditorUtility.DisplayDialog("Discard Saved Lighting", "Forget the saved original lighting? It can no longer be restored automatically.", "Discard", "Cancel"))
                    EditorPrefs.DeleteKey(PrefsKey);
            }
            else
            {
                EditorGUILayout.HelpBox($"Preview active. {state.disabledLights.Count} scene light(s) disabled. The rig object is tagged EditorOnly and is never included in builds.", MessageType.Info);
                if (GUILayout.Button("Restore scene", GUILayout.Height(30)))
                    lastProblems = Restore();
            }
            foreach (var problem in lastProblems)
                EditorGUILayout.HelpBox(problem, MessageType.Warning);
        }

        public static SavedState LoadState()
        {
            var json = EditorPrefs.GetString(PrefsKey, "");
            return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<SavedState>(json);
        }

        static void SaveState(SavedState state)
        {
            EditorPrefs.SetString(PrefsKey, JsonUtility.ToJson(state));
        }

        public static GameObject FindRig(Scene scene)
        {
            return scene.IsValid() ? scene.GetRootGameObjects().FirstOrDefault(g => g.name == RigName) : null;
        }

        static ObjectRef Ref(UnityEngine.Object obj)
        {
            if (obj == null) return new ObjectRef();
            return new ObjectRef { isSet = true, instanceId = obj.GetInstanceID(), globalId = GlobalObjectId.GetGlobalObjectIdSlow(obj).ToString() };
        }

        // Instance IDs are only stable within one editor session, so they are trusted only when the session token matches.
        static UnityEngine.Object Resolve(ObjectRef reference, string token)
        {
            if (reference == null || !reference.isSet) return null;
            if (token == SessionState.GetString(SessionKey, ""))
            {
                var obj = EditorUtility.InstanceIDToObject(reference.instanceId);
                if (obj != null) return obj;
            }
            return GlobalObjectId.TryParse(reference.globalId, out var id) ? GlobalObjectId.GlobalObjectIdentifierToObjectSlow(id) : null;
        }

        static string SessionToken()
        {
            var token = SessionState.GetString(SessionKey, "");
            if (string.IsNullOrEmpty(token))
            {
                token = Guid.NewGuid().ToString("N");
                SessionState.SetString(SessionKey, token);
            }
            return token;
        }

        static SavedState Capture(Scene scene)
        {
            var probe = RenderSettings.ambientProbe;
            var coefficients = new float[27];
            for (var c = 0; c < 3; c++)
            for (var i = 0; i < 9; i++)
                coefficients[c * 9 + i] = probe[c, i];
            return new SavedState
            {
                sessionToken = SessionToken(),
                scenePath = scene.path,
                ambientMode = RenderSettings.ambientMode,
                ambientSkyColor = RenderSettings.ambientSkyColor,
                ambientEquatorColor = RenderSettings.ambientEquatorColor,
                ambientGroundColor = RenderSettings.ambientGroundColor,
                ambientIntensity = RenderSettings.ambientIntensity,
                ambientProbe = coefficients,
                reflectionIntensity = RenderSettings.reflectionIntensity,
                skybox = Ref(RenderSettings.skybox),
                sun = Ref(RenderSettings.sun),
                fog = RenderSettings.fog,
                fogColor = RenderSettings.fogColor,
                fogMode = RenderSettings.fogMode,
                fogDensity = RenderSettings.fogDensity,
                fogStartDistance = RenderSettings.fogStartDistance,
                fogEndDistance = RenderSettings.fogEndDistance
            };
        }

        public static GameObject Apply(Preset preset, float yaw, float intensityMultiplier, Transform center)
        {
            var scene = SceneManager.GetActiveScene();
            var state = LoadState();
            if (state != null && state.scenePath != scene.path)
                throw new InvalidOperationException("Saved lighting belongs to another scene; restore or discard it first.");
            if (state == null) state = Capture(scene);
            state.sessionToken = SessionToken();
            state.preset = preset;
            state.yaw = yaw;
            state.intensityMultiplier = intensityMultiplier;

            var existing = FindRig(scene);
            if (existing != null) DestroyImmediate(existing);

            foreach (var light in FindObjectsOfType<Light>())
            {
                if (!light.enabled || light.gameObject.scene != scene) continue;
                light.enabled = false;
                EditorUtility.SetDirty(light);
                state.disabledLights.Add(Ref(light));
            }
            SaveState(state);

            var def = Presets[preset];
            var rig = new GameObject(RigName) { tag = "EditorOnly" };
            SceneManager.MoveGameObjectToScene(rig, scene);
            rig.transform.SetPositionAndRotation(center != null ? center.position : Vector3.zero, Quaternion.Euler(0, yaw, 0));
            Light key = null;
            for (var i = 0; i < def.Lights.Length; i++)
            {
                var d = def.Lights[i];
                var go = new GameObject(d.Type + " " + (i + 1)) { tag = "EditorOnly" };
                go.transform.SetParent(rig.transform, false);
                go.transform.localPosition = d.Position;
                go.transform.localRotation = Quaternion.Euler(d.Euler);
                var light = go.AddComponent<Light>();
                light.type = d.Type;
                light.color = d.Color;
                light.intensity = d.Intensity * intensityMultiplier;
                light.shadows = d.Shadows;
                light.lightmapBakeType = LightmapBakeType.Realtime;
                if (d.Type == LightType.Point) light.range = d.Range;
                if (key == null && d.Type == LightType.Directional) key = light;
            }

            RenderSettings.ambientMode = def.Mode;
            RenderSettings.ambientSkyColor = def.Sky;
            RenderSettings.ambientEquatorColor = def.Equator;
            RenderSettings.ambientGroundColor = def.Ground;
            RenderSettings.ambientIntensity = 1;
            RenderSettings.reflectionIntensity = def.Reflection;
            RenderSettings.fog = false;
            RenderSettings.sun = key;
            RenderSettings.skybox = def.KeepSkybox ? Resolve(state.skybox, state.sessionToken) as Material : null;
            if (def.Mode != AmbientMode.Skybox) DynamicGI.UpdateEnvironment();
            EditorSceneManager.MarkSceneDirty(scene);
            SceneView.RepaintAll();
            return rig;
        }

        public static List<string> Restore()
        {
            var problems = new List<string>();
            var scene = SceneManager.GetActiveScene();
            var rig = FindRig(scene);
            if (rig != null) DestroyImmediate(rig);
            var state = LoadState();
            if (state == null)
            {
                SceneView.RepaintAll();
                return problems;
            }

            var skybox = Resolve(state.skybox, state.sessionToken) as Material;
            if (state.skybox.isSet && skybox == null) problems.Add("The original skybox material could not be found; the skybox was left empty.");
            var sun = Resolve(state.sun, state.sessionToken) as Light;
            if (state.sun.isSet && sun == null) problems.Add("The original sun light could not be found.");

            RenderSettings.ambientMode = state.ambientMode;
            RenderSettings.ambientSkyColor = state.ambientSkyColor;
            RenderSettings.ambientEquatorColor = state.ambientEquatorColor;
            RenderSettings.ambientGroundColor = state.ambientGroundColor;
            RenderSettings.ambientIntensity = state.ambientIntensity;
            RenderSettings.reflectionIntensity = state.reflectionIntensity;
            RenderSettings.skybox = skybox;
            RenderSettings.sun = sun;
            RenderSettings.fog = state.fog;
            RenderSettings.fogColor = state.fogColor;
            RenderSettings.fogMode = state.fogMode;
            RenderSettings.fogDensity = state.fogDensity;
            RenderSettings.fogStartDistance = state.fogStartDistance;
            RenderSettings.fogEndDistance = state.fogEndDistance;
            if (state.ambientProbe != null && state.ambientProbe.Length == 27)
            {
                var probe = new SphericalHarmonicsL2();
                for (var c = 0; c < 3; c++)
                for (var i = 0; i < 9; i++)
                    probe[c, i] = state.ambientProbe[c * 9 + i];
                RenderSettings.ambientProbe = probe;
            }

            var missing = 0;
            foreach (var reference in state.disabledLights)
            {
                if (Resolve(reference, state.sessionToken) is Light light)
                {
                    light.enabled = true;
                    EditorUtility.SetDirty(light);
                }
                else missing++;
            }
            if (missing > 0) problems.Add($"{missing} previously disabled light(s) no longer exist and were not re-enabled.");

            EditorPrefs.DeleteKey(PrefsKey);
            EditorSceneManager.MarkSceneDirty(scene);
            SceneView.RepaintAll();
            return problems;
        }
    }
}

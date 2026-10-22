using System.Collections.Generic;
using System.IO;
using System.Linq;
using com.vrcfury.api;
using com.vrcfury.api.Components;
using Poiyomi.ModularShaderSystem;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;

namespace VRCSquish.Editor
{
    public class BakeResult
    {
        public bool Success;
        public List<string> Messages = new List<string>();
    }

    public static class SquishBaker
    {
        public const string CameraName = "VRC Squish Camera";
        public const string NoSelfCameraName = "VRC Squish Camera NoSelf";
        public const string FxObjectName = "VRC Squish FX";
        public const string ToggleObjectName = "VRC Squish Toggle";
        const string ParamOne = "VRCSquish/One";
        const string ParamScale = "ScaleFactor";
        const string ParamOn = "VRCSquish/On";
        const string ParamSelf = "VRCSquish/Self";
        const string ParamLocal = "IsLocal";
        const string MenuName = "VRCSquish";
        const int PlayerLocalLayer = 10;

        class RendererJob
        {
            public Renderer Renderer;
            public Mesh Mesh;
            public Texture2D Texture;
            public List<int> Slots;
        }

        public static Camera FindCamera(SquishRegion region) => FindCamera(region, CameraName);

        static Camera FindCamera(SquishRegion region, string name)
        {
            Transform t = region.transform.Find(name);

            return t != null ? t.GetComponent<Camera>() : null;
        }

        public static List<Material> SquishMaterials(SquishSetup setup)
        {
            return setup.renderers.Where(r => r != null)
                .SelectMany(r => r.sharedMaterials)
                .Where(m => m != null && m.HasProperty("_SquishData") && m.GetTexture("_SquishData") != null)
                .Distinct().ToList();
        }

        public static BakeResult Bake(SquishSetup setup)
        {
            BakeResult result = new BakeResult();
            VRCAvatarDescriptor avatar = setup.GetComponentInParent<VRCAvatarDescriptor>();

            if (avatar == null) return Fail(result, "No avatar descriptor");

            List<SquishRegion> regions = setup.regions.Where(r => r != null).Distinct().ToList();

            if (regions.Count == 0) return Fail(result, "No regions");
            if (regions.Count > SquishSetup.MaxRegions) return Fail(result, $"Max {SquishSetup.MaxRegions} regions");

            List<Renderer> renderers = setup.renderers.Where(r => r != null).Distinct().ToList();

            if (renderers.Count == 0) return Fail(result, "No meshes");

            string folder = $"{setup.output.TrimEnd('/')}/{Sanitize(avatar.gameObject.name)}";
            VRCSquishShaderInstaller.EnsureFolder(folder);

            Undo.SetCurrentGroupName("Bake VRC Squish");
            int undoGroup = Undo.GetCurrentGroup();

            Dictionary<Material, Mesh> materialMesh = new Dictionary<Material, Mesh>();
            List<RendererJob> jobs = new List<RendererJob>();
            try
            {
                for (int ri = 0; ri < renderers.Count; ri++)
                {
                    Renderer renderer = renderers[ri];
                    EditorUtility.DisplayProgressBar("VRC Squish", renderer.name, (float)ri / renderers.Count);
                    SquishGeometry geo = SquishGeometry.Capture(renderer);

                    if (geo == null) continue;

                    Texture2D tex = BakeTexture(geo, regions, out bool[] affectedVertices, out int affected);
                    List<int> slots = affected > 0 ? AffectedSlots(renderer, geo.SourceMesh, affectedVertices) : new List<int>();

                    if (slots.Count == 0)
                    {
                        Object.DestroyImmediate(tex);
                        continue;
                    }

                    jobs.Add(new RendererJob { Renderer = renderer, Mesh = geo.SourceMesh, Texture = tex, Slots = slots });
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (jobs.Count == 0) return Fail(result, "No mesh inside a region");

            List<Material> squishMaterials = jobs.SelectMany(j => j.Slots.Select(s => j.Renderer.sharedMaterials[s])).Where(m => m != null).Distinct().ToList();
            Dictionary<Material, Shader> shaderFor = new Dictionary<Material, Shader>();

            PrepareMaterials(squishMaterials, shaderFor, result.Messages);

            foreach (RendererJob job in jobs)
            {
                Texture2D tex = SaveTexture(job.Texture, $"{folder}/{Sanitize(job.Renderer.name)}_SquishData.asset");
                AssignMaterials(setup, job, tex, folder, shaderFor, materialMesh);
            }

            int tiles = regions.Count > 1 ? 2 : 1;
            RenderTexture atlas = GetOrCreateAtlas($"{folder}/Depth.renderTexture", Mathf.Clamp(setup.resolution, 16, 1024) * tiles);
            Vector4[] cams = new Vector4[SquishSetup.MaxRegions];

            for (int k = 0; k < regions.Count; k++)
            {
                SquishRegion region = regions[k];
                cams[k] = new Vector4(region.OrthoHalfSize, region.depth, SquishRegion.NearPlane(k), region.FarPlane);
                BuildCamera(region, k, tiles, atlas, CameraName, setup.layers);
                BuildCamera(region, k, tiles, atlas, NoSelfCameraName, setup.layers & ~(1 << PlayerLocalLayer));
            }

            foreach (Material mat in materialMesh.Keys)
            {
                Undo.RecordObject(mat, "Bake VRC Squish");
                mat.SetTexture("_SquishDepth", atlas);
                mat.SetFloat("_SquishTiles", tiles);
                for (int k = 0; k < SquishSetup.MaxRegions; k++) mat.SetVector("_SquishCam" + k, cams[k]);
                EditorUtility.SetDirty(mat);
            }

            AnimatorController controller = BuildController(avatar, renderers, regions, $"{folder}/VRCSquish_FX.controller");

            BuildFullController(setup, controller, folder);
            BuildToggle(setup, avatar, renderers, $"{folder}/VRCSquish_On.anim");

            Undo.CollapseUndoOperations(undoGroup);
            AssetDatabase.SaveAssets();

            result.Success = true;
            return result;
        }

        public static BakeResult RebuildShaders(SquishSetup setup)
        {
            BakeResult result = new BakeResult();
            List<ModularShader> sources = SquishMaterials(setup)
                .Select(m => VRCSquishShaderInstaller.SourceShaderName(m.shader))
                .Distinct()
                .Select(VRCSquishShaderInstaller.FindModularShader)
                .Where(s => s != null)
                .ToList();

            if (sources.Count == 0) return Fail(result, "Nothing to rebuild");

            VRCSquishShaderInstaller.Install(sources);

            result.Success = true;
            result.Messages.Add($"Rebuilt {Plural(sources.Count, "shader")}");

            return result;
        }

        static Texture2D BakeTexture(SquishGeometry geo, List<SquishRegion> regions, out bool[] affectedVertices, out int affected)
        {
            int count = geo.Positions.Length;
            int texels = count * 2;
            int width = Mathf.Clamp(Mathf.NextPowerOfTwo(texels), 1, 2048);
            int height = Mathf.Max(1, Mathf.CeilToInt(texels / (float)width));
            Color[] pixels = new Color[width * height];

            affectedVertices = new bool[count];
            affected = 0;

            (SquishRegion region, Quaternion inv, Vector3 origin)[] frames = regions.Select(r => (region: r, inv: Quaternion.Inverse(r.transform.rotation), origin: r.transform.position)).ToArray();

            for (int i = 0; i < count; i++)
            {
                int best = -1;
                float bestWeight = 0;
                Vector3 bestLocal = default;

                for (int k = 0; k < frames.Length; k++)
                {
                    Vector3 local = frames[k].inv * (geo.Positions[i] - frames[k].origin);
                    float w = frames[k].region.Weight(local);

                    if (w <= bestWeight) continue;

                    bestWeight = w;
                    best = k;
                    bestLocal = local;
                }

                if (best < 0 || bestWeight < 0.001f) continue;

                Quaternion inv = frames[best].inv;
                Vector3 n = (inv * geo.Normals[i]).normalized;
                Vector3 t = inv * geo.Tangents[i];

                if (t.sqrMagnitude < 1e-8f) t = Vector3.Cross(n, Mathf.Abs(n.y) < 0.9f ? Vector3.up : Vector3.right);
                Vector2 on = OctEncode(n);
                Vector2 ot = OctEncode(t.normalized);

                pixels[i * 2] = new Color(bestLocal.x, bestLocal.y, bestLocal.z, best + 1 + bestWeight * 0.99f);
                pixels[i * 2 + 1] = new Color(on.x, on.y, ot.x, ot.y);
                affectedVertices[i] = true;
                affected++;
            }

            Texture2D tex = new Texture2D(width, height, TextureFormat.RGBAHalf, false, true)
            {
                filterMode = FilterMode.Point,
                wrapMode = TextureWrapMode.Clamp,
                anisoLevel = 0,
            };

            tex.SetPixels(pixels);
            tex.Apply(false, false);

            return tex;
        }

        static Vector2 OctEncode(Vector3 n)
        {
            n /= Mathf.Abs(n.x) + Mathf.Abs(n.y) + Mathf.Abs(n.z);

            if (n.z < 0)
            {
                float x = n.x;
                n.x = (1f - Mathf.Abs(n.y)) * (x >= 0 ? 1f : -1f);
                n.y = (1f - Mathf.Abs(x)) * (n.y >= 0 ? 1f : -1f);
            }

            return new Vector2(n.x, n.y);
        }

        static Texture2D SaveTexture(Texture2D tex, string path)
        {
            tex.name = Path.GetFileNameWithoutExtension(path);
            Texture2D existing = AssetDatabase.LoadAssetAtPath<Texture2D>(path);

            if (existing != null)
            {
                EditorUtility.CopySerialized(tex, existing);
                EditorUtility.SetDirty(existing);
                Object.DestroyImmediate(tex);
                return existing;
            }

            AssetDatabase.CreateAsset(tex, path);

            return tex;
        }

        static List<int> AffectedSlots(Renderer renderer, Mesh mesh, bool[] affectedVertices)
        {
            List<int> slots = new List<int>();
            int materialCount = renderer.sharedMaterials.Length;

            for (int s = 0; s < materialCount; s++)
            {
                int sub = Mathf.Min(s, mesh.subMeshCount - 1);
                int[] tris = mesh.GetTriangles(sub);

                for (int i = 0; i < tris.Length; i++)
                {
                    if (tris[i] >= affectedVertices.Length || !affectedVertices[tris[i]]) continue;
                    slots.Add(s);
                    break;
                }
            }
            return slots;
        }

        static void PrepareMaterials(List<Material> materials, Dictionary<Material, Shader> shaderFor, List<string> messages)
        {
            List<Material> locked = materials.Where(Thry.ShaderOptimizer.IsMaterialLocked).ToList();

            Dictionary<Material, string> sourceName = new Dictionary<Material, string>();

            foreach (Material m in materials)
            {
                Shader shader = locked.Contains(m) ? Thry.ShaderOptimizer.GetOriginalShader(m) : m.shader;
                if (shader == null)
                {
                    messages.Add($"{m.name}: original shader missing");
                    continue;
                }

                string source = VRCSquishShaderInstaller.SourceShaderName(shader);

                if (!source.StartsWith(".poiyomi/"))
                {
                    messages.Add($"{m.name}: not Poiyomi");
                    continue;
                }

                sourceName[m] = source;
            }

            List<ModularShader> toGenerate = new List<ModularShader>();

            foreach (string name in sourceName.Values.Distinct().Where(n => !VRCSquishShaderInstaller.IsUpToDate(VRCSquishShaderInstaller.FindSquishVariant(n))))
            {
                ModularShader modular = VRCSquishShaderInstaller.FindModularShader(name);
                if (modular != null) toGenerate.Add(modular);
                else messages.Add($"{name}: no Poiyomi source");
            }

            if (locked.Count > 0)
            {
                Thry.ShaderOptimizer.SetLockedForAllMaterials(locked, 0, showProgressbar: true, allowCancel: false);

                foreach (Material m in locked.Where(Thry.ShaderOptimizer.IsMaterialLocked))
                {
                    messages.Add($"{m.name}: unlock failed");
                    sourceName.Remove(m);
                }
            }

            if (toGenerate.Count > 0) VRCSquishShaderInstaller.Install(toGenerate);

            foreach (KeyValuePair<Material, string> pair in sourceName)
            {
                Shader squish = VRCSquishShaderInstaller.FindSquishVariant(pair.Value);

                if (squish != null) shaderFor[pair.Key] = squish;
                else messages.Add($"{pair.Key.name}: shader build failed");
            }
        }

        static void AssignMaterials(SquishSetup setup, RendererJob job, Texture2D tex, string folder, Dictionary<Material, Shader> shaderFor, Dictionary<Material, Mesh> materialMesh)
        {
            Renderer renderer = job.Renderer;
            Material[] mats = renderer.sharedMaterials;
            bool changedSlots = false;
            foreach (int s in job.Slots)
            {
                Material mat = mats[s];

                if (mat == null || !shaderFor.TryGetValue(mat, out Shader squish)) continue;

                if (mat.shader != squish)
                {
                    Undo.RecordObject(mat, "Bake VRC Squish");
                    int queue = mat.renderQueue;
                    mat.shader = squish;
                    mat.renderQueue = queue;
                }

                if (materialMesh.TryGetValue(mat, out Mesh owner) && owner != job.Mesh)
                {
                    Material clone = new Material(mat) { name = $"{mat.name} ({renderer.name})" };
                    string clonePath = AssetDatabase.GenerateUniqueAssetPath($"{folder}/{Sanitize(clone.name)}.mat");
                    AssetDatabase.CreateAsset(clone, clonePath);
                    if (!setup.swaps.Any(w => w.renderer == renderer && w.slot == s))
                    {
                        Undo.RecordObject(setup, "Bake VRC Squish");
                        setup.swaps.Add(new SquishSetup.Swap { renderer = renderer, slot = s, material = mat });
                    }
                    mats[s] = mat = clone;
                    changedSlots = true;
                }
                materialMesh[mat] = job.Mesh;

                Undo.RecordObject(mat, "Bake VRC Squish");
                mat.SetTexture("_SquishData", tex);
                mat.SetFloat("_SquishEnabled", 1);
                mat.EnableKeyword("POI_SQUISH");
                mat.SetOverrideTag("_SquishScaleAnimated", "1");
                mat.SetOverrideTag("_SquishOnAnimated", "2");
                if (string.IsNullOrEmpty(mat.GetTag("thry_rename_suffix", false, "")))
                    mat.SetOverrideTag("thry_rename_suffix", Thry.ShaderOptimizer.GetRenamedPropertySuffix(mat));
                mat.SetFloat("_SquishOn", 0);
                EditorUtility.SetDirty(mat);
            }

            if (!changedSlots) return;

            Undo.RecordObject(renderer, "Bake VRC Squish");
            renderer.sharedMaterials = mats;
        }

        static RenderTexture GetOrCreateAtlas(string path, int size)
        {
            RenderTexture rt = AssetDatabase.LoadAssetAtPath<RenderTexture>(path);

            if (rt != null && (rt.width != size || rt.format != RenderTextureFormat.Depth))
            {
                AssetDatabase.DeleteAsset(path);
                rt = null;
            }

            if (rt != null) return rt;

            rt = new RenderTexture(size, size, 24, RenderTextureFormat.Depth)
            {
                name = Path.GetFileNameWithoutExtension(path),
                filterMode = FilterMode.Bilinear,
                wrapMode = TextureWrapMode.Clamp,
                useMipMap = false,
                autoGenerateMips = false,
                antiAliasing = 1,
            };

            AssetDatabase.CreateAsset(rt, path);

            return rt;
        }

        static void BuildCamera(SquishRegion region, int index, int tiles, RenderTexture target, string name, int layers)
        {
            Transform old = region.transform.Find(name);

            if (old != null) Undo.DestroyObjectImmediate(old.gameObject);

            GameObject go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Bake VRC Squish");

            go.transform.SetParent(region.transform, false);
            go.transform.SetPositionAndRotation(region.RegionToWorld.MultiplyPoint3x4(region.CameraLocalPosition), region.transform.rotation);

            float tile = 1f / tiles;
            Camera cam = go.AddComponent<Camera>();

            cam.orthographic = true;
            cam.orthographicSize = region.OrthoHalfSize;
            cam.nearClipPlane = SquishRegion.NearPlane(index);
            cam.farClipPlane = region.FarPlane;
            cam.clearFlags = CameraClearFlags.SolidColor;
            cam.backgroundColor = Color.clear;
            cam.cullingMask = layers;
            cam.depth = -100;
            cam.targetTexture = target;
            cam.rect = new Rect((index % tiles) * tile, (index / tiles) * tile, tile, tile);
            cam.allowHDR = false;
            cam.allowMSAA = false;
            cam.useOcclusionCulling = false;
            cam.renderingPath = RenderingPath.Forward;
            cam.stereoTargetEye = StereoTargetEyeMask.None;
            cam.enabled = false;
        }

        static AnimatorController BuildController(VRCAvatarDescriptor avatar, List<Renderer> renderers, List<SquishRegion> regions, string path)
        {
            AssetDatabase.DeleteAsset(path);

            AnimatorController controller = AnimatorController.CreateAnimatorControllerAtPath(path);

            controller.AddParameter(new AnimatorControllerParameter { name = ParamOne, type = AnimatorControllerParameterType.Float, defaultFloat = 1 });
            controller.AddParameter(new AnimatorControllerParameter { name = ParamScale, type = AnimatorControllerParameterType.Float, defaultFloat = 1 });
            controller.AddParameter(ParamOn, AnimatorControllerParameterType.Bool);
            controller.AddParameter(ParamSelf, AnimatorControllerParameterType.Bool);
            controller.AddParameter(ParamLocal, AnimatorControllerParameterType.Bool);

            List<(string path, System.Type type)> targets = renderers
                .Select(r => (path: AnimationUtility.CalculateTransformPath(r.transform, avatar.transform), type: r.GetType()))
                .ToList();

            BlendTree root = NewTree(controller, "VRC Squish", BlendTreeType.Direct);

            root.children = new[]
            {
                new ChildMotion { motion = Lerp(controller, ParamScale, targets, "material._SquishScale", 0, 10), directBlendParameter = ParamOne, timeScale = 1 },
            };

            AnimatorControllerLayer layer = controller.layers[0];
            layer.name = "VRC Squish";

            AnimatorState state = layer.stateMachine.AddState("Squish");
            state.motion = root;
            state.writeDefaultValues = true;
            layer.stateMachine.defaultState = state;

            controller.layers = new[] { layer, BuildCameraLayer(controller, avatar, regions) };

            EditorUtility.SetDirty(controller);
            return controller;
        }

        static AnimatorControllerLayer BuildCameraLayer(AnimatorController controller, VRCAvatarDescriptor avatar, List<SquishRegion> regions)
        {
            AnimatorStateMachine machine = new AnimatorStateMachine { name = "VRC Squish Cameras", hideFlags = HideFlags.HideInHierarchy };

            AssetDatabase.AddObjectToAsset(machine, controller);

            List<string> withSelf = CameraPaths(avatar, regions, CameraName);
            List<string> noSelf = CameraPaths(avatar, regions, NoSelfCameraName);

            AnimatorState off = CameraState(controller, machine, "Off", withSelf, noSelf, false, false);
            AnimatorState self = CameraState(controller, machine, "Self", withSelf, noSelf, true, false);
            AnimatorState solo = CameraState(controller, machine, "NoSelf", withSelf, noSelf, false, true);
            machine.defaultState = off;

            CameraTransition(machine, off, (AnimatorConditionMode.IfNot, ParamOn));
            CameraTransition(machine, self, (AnimatorConditionMode.If, ParamOn), (AnimatorConditionMode.If, ParamSelf));
            CameraTransition(machine, self, (AnimatorConditionMode.If, ParamOn), (AnimatorConditionMode.IfNot, ParamLocal));
            CameraTransition(machine, solo, (AnimatorConditionMode.If, ParamOn), (AnimatorConditionMode.IfNot, ParamSelf), (AnimatorConditionMode.If, ParamLocal));

            return new AnimatorControllerLayer { name = "VRC Squish Cameras", stateMachine = machine, defaultWeight = 1 };
        }

        static List<string> CameraPaths(VRCAvatarDescriptor avatar, List<SquishRegion> regions, string name)
        {
            return regions.Select(r => FindCamera(r, name))
                .Where(c => c != null)
                .Select(c => AnimationUtility.CalculateTransformPath(c.transform, avatar.transform))
                .ToList();
        }

        static AnimatorState CameraState(AnimatorController controller, AnimatorStateMachine machine, string name, List<string> withSelf, List<string> noSelf, bool selfOn, bool noSelfOn)
        {
            AnimationClip clip = new AnimationClip { name = $"VRC Squish Cameras {name}" };

            foreach (string path in withSelf)
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(Camera), "m_Enabled"), AnimationCurve.Constant(0, 1 / 60f, selfOn ? 1 : 0));

            foreach (string path in noSelf)
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, typeof(Camera), "m_Enabled"), AnimationCurve.Constant(0, 1 / 60f, noSelfOn ? 1 : 0));

            AssetDatabase.AddObjectToAsset(clip, controller);

            AnimatorState state = machine.AddState(name);
            state.motion = clip;
            state.writeDefaultValues = true;
            return state;
        }

        static void CameraTransition(AnimatorStateMachine machine, AnimatorState target, params (AnimatorConditionMode mode, string param)[] conditions)
        {
            AnimatorStateTransition t = machine.AddAnyStateTransition(target);
            t.hasExitTime = false;
            t.duration = 0;
            t.canTransitionToSelf = false;

            foreach ((AnimatorConditionMode mode, string param) c in conditions) t.AddCondition(c.mode, 0, c.param);
        }

        static BlendTree NewTree(AnimatorController controller, string name, BlendTreeType type)
        {
            BlendTree tree = new BlendTree { name = name, blendType = type, useAutomaticThresholds = false, hideFlags = HideFlags.HideInHierarchy };

            AssetDatabase.AddObjectToAsset(tree, controller);

            return tree;
        }

        static BlendTree Lerp(AnimatorController controller, string param, List<(string path, System.Type type)> targets, string prop, float min, float max)
        {
            BlendTree tree = NewTree(controller, param, BlendTreeType.Simple1D);

            tree.blendParameter = param;
            tree.AddChild(Clip(controller, $"{param} {min}", targets, prop, min), min);
            tree.AddChild(Clip(controller, $"{param} {max}", targets, prop, max), max);

            return tree;
        }

        static AnimationClip Clip(AnimatorController controller, string name, List<(string path, System.Type type)> targets, string prop, float value)
        {
            AnimationClip clip = new AnimationClip { name = name };

            foreach ((string path, System.Type type) t in targets)
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(t.path, t.type, prop), AnimationCurve.Constant(0, 1 / 60f, value));

            AssetDatabase.AddObjectToAsset(clip, controller);

            return clip;
        }

        static void BuildFullController(SquishSetup setup, AnimatorController controller, string folder)
        {
            Transform old = setup.transform.Find(FxObjectName);

            if (old != null) Undo.DestroyObjectImmediate(old.gameObject);

            GameObject go = new GameObject(FxObjectName);

            Undo.RegisterCreatedObjectUndo(go, "Bake VRC Squish");

            go.transform.SetParent(setup.transform, false);

            FuryFullController fc = FuryComponents.CreateFullController(go);

            fc.AddController(controller);
            fc.AddGlobalParam(ParamOne);
            fc.AddGlobalParam(ParamScale);
            fc.AddGlobalParam(ParamOn);
            fc.AddGlobalParam(ParamSelf);
            fc.AddMenu(BuildMenu($"{folder}/VRCSquish_Menu.asset"), MenuName);
            fc.AddParams(BuildParams($"{folder}/VRCSquish_Params.asset"));
        }

        static VRCExpressionsMenu BuildMenu(string path)
        {
            AssetDatabase.DeleteAsset(path);

            VRCExpressionsMenu menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();

            menu.controls.Add(new VRCExpressionsMenu.Control
            {
                name = "Self Interaction",
                type = VRCExpressionsMenu.Control.ControlType.Toggle,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = ParamSelf },
                value = 1,
            });

            AssetDatabase.CreateAsset(menu, path);

            return menu;
        }

        static VRCExpressionParameters BuildParams(string path)
        {
            AssetDatabase.DeleteAsset(path);

            VRCExpressionParameters prms = ScriptableObject.CreateInstance<VRCExpressionParameters>();

            prms.parameters = new[]
            {
                new VRCExpressionParameters.Parameter { name = ParamSelf, valueType = VRCExpressionParameters.ValueType.Bool, defaultValue = 0, saved = false, networkSynced = true },
            };

            AssetDatabase.CreateAsset(prms, path);

            return prms;
        }

        static void BuildToggle(SquishSetup setup, VRCAvatarDescriptor avatar, List<Renderer> renderers, string clipPath)
        {
            Transform old = setup.transform.Find(ToggleObjectName);
            if (old != null) Undo.DestroyObjectImmediate(old.gameObject);

            AssetDatabase.DeleteAsset(clipPath);

            AnimationClip clip = new AnimationClip { name = "VRC Squish On" };
            AnimationCurve on = AnimationCurve.Constant(0, 1 / 60f, 1);

            foreach (Renderer r in renderers)
            {
                string path = AnimationUtility.CalculateTransformPath(r.transform, avatar.transform);
                AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, r.GetType(), "material._SquishOn"), on);

                IEnumerable<string> suffixes = r.sharedMaterials
                    .Where(m => m != null && m.HasProperty("_SquishData") && m.GetTexture("_SquishData") != null)
                    .Select(Thry.ShaderOptimizer.GetRenamedPropertySuffix)
                    .Distinct();

                foreach (string suffix in suffixes)
                    AnimationUtility.SetEditorCurve(clip, EditorCurveBinding.FloatCurve(path, r.GetType(), $"material._SquishOn_{suffix}"), on);
            }

            AssetDatabase.CreateAsset(clip, clipPath);

            GameObject go = new GameObject(ToggleObjectName);
            Undo.RegisterCreatedObjectUndo(go, "Bake VRC Squish");
            go.transform.SetParent(setup.transform, false);

            FuryToggle toggle = FuryComponents.CreateToggle(go);
            toggle.SetMenuPath($"{MenuName}/Enabled");
            toggle.SetGlobalParameter(ParamOn);
            toggle.GetActions().AddAnimationClip(clip);
        }

        public static void ClearRegions(SquishSetup setup)
        {
            VRCAvatarDescriptor avatar = setup.GetComponentInParent<VRCAvatarDescriptor>();
            List<SquishRegion> regions = setup.regions.Where(r => r != null).ToList();

            if (avatar != null) regions.AddRange(avatar.GetComponentsInChildren<SquishRegion>(true));

            Undo.RecordObject(setup, "Clear Regions");
            setup.regions.Clear();

            foreach (SquishRegion r in regions.Distinct())
                if (r != null) Undo.DestroyObjectImmediate(r.gameObject);
        }

        public static BakeResult Uninstall(SquishSetup setup)
        {
            BakeResult result = new BakeResult();
            VRCAvatarDescriptor avatar = setup.GetComponentInParent<VRCAvatarDescriptor>();

            Undo.SetCurrentGroupName("Remove VRC Squish");

            int undoGroup = Undo.GetCurrentGroup();

            List<Renderer> renderers = setup.renderers.Where(r => r != null)
                .Concat(setup.swaps.Where(w => w.renderer != null).Select(w => w.renderer))
                .Distinct().ToList();

            List<Material> touched = renderers.SelectMany(r => r.sharedMaterials)
                .Concat(setup.swaps.Select(w => w.material))
                .Where(m => m != null)
                .Distinct().ToList();

            List<Material> locked = touched.Where(m => Thry.ShaderOptimizer.IsMaterialLocked(m)
                && VRCSquishShaderInstaller.IsSquishShader(Thry.ShaderOptimizer.GetOriginalShader(m))).ToList();

            if (locked.Count > 0) Thry.ShaderOptimizer.SetLockedForAllMaterials(locked, 0, showProgressbar: true, allowCancel: false);

            foreach (SquishSetup.Swap swap in setup.swaps)
            {
                if (swap.renderer == null || swap.material == null) continue;
                Material[] mats = swap.renderer.sharedMaterials;
                if (swap.slot >= mats.Length) continue;
                Undo.RecordObject(swap.renderer, "Remove VRC Squish");
                mats[swap.slot] = swap.material;
                swap.renderer.sharedMaterials = mats;
            }

            int reverted = 0;
            foreach (Material m in touched)
            {
                if (!VRCSquishShaderInstaller.IsSquishShader(m.shader)) continue;

                Shader poi = Shader.Find(VRCSquishShaderInstaller.SourceShaderName(m.shader));

                if (poi == null)
                {
                    result.Messages.Add($"{m.name}: Poiyomi shader missing");
                    continue;
                }

                Undo.RecordObject(m, "Remove VRC Squish");

                int queue = m.renderQueue;

                m.SetTexture("_SquishData", null);
                m.SetTexture("_SquishDepth", null);
                m.SetFloat("_SquishEnabled", 0);
                m.DisableKeyword("POI_SQUISH");
                m.SetOverrideTag("_SquishScaleAnimated", "");
                m.SetOverrideTag("_SquishOnAnimated", "");
                m.shader = poi;
                m.renderQueue = queue;

                EditorUtility.SetDirty(m);

                reverted++;
            }

            string folder = avatar != null ? $"{setup.output.TrimEnd('/')}/{Sanitize(avatar.gameObject.name)}" : null;

            ClearRegions(setup);

            Undo.DestroyObjectImmediate(setup.gameObject);
            Undo.CollapseUndoOperations(undoGroup);

            if (folder != null && AssetDatabase.IsValidFolder(folder))
            {
                bool inUse = avatar.GetComponentsInChildren<Renderer>(true)
                    .SelectMany(r => r.sharedMaterials)
                    .Where(m => m != null)
                    .Any(m => AssetDatabase.GetAssetPath(m).StartsWith(folder + "/"));

                if (inUse) result.Messages.Add($"Kept {folder}: still in use");
                else AssetDatabase.DeleteAsset(folder);
            }
            AssetDatabase.SaveAssets();

            result.Success = true;
            result.Messages.Insert(0, $"Removed, {Plural(reverted, "material")} reverted");

            return result;
        }

        static string Plural(int count, string word) => count == 1 ? $"1 {word}" : $"{count} {word}s";

        static BakeResult Fail(BakeResult result, string message)
        {
            result.Success = false;
            result.Messages.Add(message);

            return result;
        }

        static string Sanitize(string name)
        {
            foreach (char c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');

            return name;
        }
    }

    public class SquishGeometry
    {
        public Mesh SourceMesh;
        public Vector3[] Positions;
        public Vector3[] Normals;
        public Vector3[] Tangents;
        public int[] Triangles;

        public static SquishGeometry Capture(Renderer renderer)
        {
            Mesh source, baked;

            Matrix4x4 toWorld;

            if (renderer is SkinnedMeshRenderer smr)
            {
                source = smr.sharedMesh;
                if (source == null) return null;
                baked = new Mesh();
                smr.BakeMesh(baked);
                toWorld = PickBakeMatrix(smr, baked);
            }
            else
            {
                MeshFilter mf = renderer.GetComponent<MeshFilter>();
                source = mf != null ? mf.sharedMesh : null;
                if (source == null) return null;
                baked = source;
                toWorld = renderer.transform.localToWorldMatrix;
            }

            Vector3[] v = baked.vertices;
            Vector3[] n = baked.normals;
            Vector4[] t = baked.tangents;
            SquishGeometry g = new SquishGeometry
            {
                SourceMesh = source,
                Positions = new Vector3[v.Length],
                Normals = new Vector3[v.Length],
                Tangents = new Vector3[v.Length],
                Triangles = baked.triangles,
            };
            for (int i = 0; i < v.Length; i++)
            {
                g.Positions[i] = toWorld.MultiplyPoint3x4(v[i]);
                g.Normals[i] = i < n.Length ? toWorld.MultiplyVector(n[i]).normalized : Vector3.up;
                g.Tangents[i] = i < t.Length ? toWorld.MultiplyVector(t[i]).normalized : Vector3.zero;
            }

            if (baked != source) Object.DestroyImmediate(baked);

            return g;
        }

        static Matrix4x4 PickBakeMatrix(SkinnedMeshRenderer smr, Mesh baked)
        {
            Transform tr = smr.transform;
            Matrix4x4 withScale = tr.localToWorldMatrix;
            Matrix4x4 noScale = Matrix4x4.TRS(tr.position, tr.rotation, Vector3.one);

            if ((tr.lossyScale - Vector3.one).sqrMagnitude < 1e-6f) return noScale;

            baked.RecalculateBounds();

            float target = smr.bounds.size.magnitude;
            float a = Mathf.Abs(Mathf.Log(Mathf.Max(withScale.MultiplyVector(baked.bounds.size).magnitude, 1e-6f) / target));
            float b = Mathf.Abs(Mathf.Log(Mathf.Max(noScale.MultiplyVector(baked.bounds.size).magnitude, 1e-6f) / target));

            return a < b ? withScale : noScale;
        }
    }
}

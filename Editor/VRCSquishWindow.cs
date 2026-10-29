using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.SceneManagement;
using VRC.SDK3.Avatars.Components;

namespace VRCSquish.Editor
{
    public class VRCSquishWindow : EditorWindow
    {
        enum Step { Avatar, Meshes, Regions, Bake, Config }

        static readonly string[] StepNames = { "Avatar", "Meshes", "Regions", "Bake", "Config" };
        static readonly int[] Resolutions = { 64, 128, 256, 512 };
        static readonly string[] ResolutionNames = { "64", "128", "256", "512" };
        static readonly (string prop, string label, float min, float max)[] Sliders =
        {
            ("_SquishStrength", "Strength", 0f, 1f),
            ("_SquishMaxDepth", "Depth", 0f, 0.2f),
            ("_SquishSoftness", "Softness", 0.002f, 0.15f),
            ("_SquishBulge", "Bulge", 0f, 1f),
            ("_SquishNormals", "Normals", 0f, 1f),
            ("_SquishContactThreshold", "Threshold", -0.02f, 0.02f),
            ("_SquishTessMax", "Factor", 1f, 32f),
            ("_SquishTessEdge", "Edge", 0.001f, 0.05f),
            ("_SquishTessReach", "Reach", 0f, 0.2f),
            ("_SquishTessPhong", "Smoothing", 0f, 1f),
        };
        static readonly Color ShapeColor = new Color(1f, 0.45f, 0.7f);
        static readonly Color EdgeColor = new Color(0.3f, 0.5f, 1f);

        const string SetupName = "VRC Squish";
        const string RegionName = "Squish Region";
        const string CubeName = "Squish Cube";

        [SerializeField] VRCAvatarDescriptor _avatar;
        [SerializeField] SquishRegion _selected;
        [SerializeField] HumanBodyBones _bone = HumanBodyBones.Chest;
        [SerializeField] GameObject _cube;
        [SerializeField] List<string> _messages = new List<string>();

        List<SquishGeometry> _geometry;
        bool _visible = true;
        Vector2 _scroll;
        readonly Dictionary<SquishRegion, List<OverlayVertex>> _overlays = new Dictionary<SquishRegion, List<OverlayVertex>>();
        static Material _surfaceMaterial;

        [MenuItem("Ahoya/VRC Squish")]
        static void Open()
        {
            GetWindow<VRCSquishWindow>("VRC Squish").Show();
        }

        void OnEnable()
        {
            SceneView.duringSceneGui += OnSceneGUI;
            EditorApplication.update += Tick;
            Undo.undoRedoPerformed += OnUndo;

            if (_avatar == null) _avatar = SelectedAvatar();
        }

        void OnDisable()
        {
            SceneView.duringSceneGui -= OnSceneGUI;
            EditorApplication.update -= Tick;
            Undo.undoRedoPerformed -= OnUndo;
            SceneView.RepaintAll();
        }

        void OnDestroy()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode) return;

            RemoveCube();
            SquishSetup setup = Setup;

            if (setup != null) SetPreview(setup, false);

            SceneView.RepaintAll();
        }

        void OnBecameVisible()
        {
            _visible = true;
            SceneView.RepaintAll();
        }

        void OnBecameInvisible()
        {
            _visible = false;
            SceneView.RepaintAll();
        }

        void OnUndo()
        {
            _geometry = null;
            Repaint();
        }

        void OnSelectionChange()
        {
            if (_avatar != null) return;
            _avatar = SelectedAvatar();
            Repaint();
        }

        SquishSetup Setup => _avatar != null ? _avatar.GetComponentInChildren<SquishSetup>(true) : null;

        void OnGUI()
        {
            SquishSetup setup = Setup;

            if (setup != null) setup.regions.RemoveAll(r => r == null);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);

            for (int i = 0; i < StepNames.Length; i++) Section((Step)i, setup);

            EditorGUILayout.Space();

            LockButton(setup);

            EditorGUILayout.Space();

            foreach (string m in _messages) EditorGUILayout.LabelField(m, EditorStyles.wordWrappedMiniLabel);

            EditorGUILayout.EndScrollView();
        }

        void LockButton(SquishSetup setup)
        {
            bool ready = Reachable(Step.Config, setup);
            List<Material> mats = ready ? SquishBaker.SquishMaterials(setup) : new List<Material>();
            bool locked = mats.Count > 0 && mats.All(PoiLock.IsMaterialLocked);

            using (new EditorGUI.DisabledScope(!ready))
            {
                if (!GUILayout.Button(locked ? "Unlock" : "Lock", GUILayout.Height(34))) return;
            }

            List<Material> targets = mats.Where(m => PoiLock.IsMaterialLocked(m) == locked).ToList();

            PoiLock.SetLockedForAllMaterials(targets, locked ? 0 : 1);

            List<Material> failed = targets.Where(m => PoiLock.IsMaterialLocked(m) == locked).ToList();

            _messages = failed.Select(m => $"{m.name}: {(locked ? "unlock" : "lock")} failed").ToList();
            GUIUtility.ExitGUI();
        }

        void Section(Step step, SquishSetup setup)
        {
            bool open = Reachable(step, setup);
            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(!open))
            {
                EditorGUILayout.LabelField(StepNames[(int)step], EditorStyles.boldLabel);

                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    if (!open)
                    {
                        EditorGUILayout.LabelField(" ");
                        return;
                    }

                    switch (step)
                    {
                        case Step.Avatar: AvatarStep(setup); break;
                        case Step.Meshes: MeshesStep(setup); break;
                        case Step.Regions: RegionsStep(setup); break;
                        case Step.Bake: BakeStep(setup); break;
                        case Step.Config: ConfigStep(setup); break;
                    }
                }
            }
        }

        bool Reachable(Step step, SquishSetup setup)
        {
            switch (step)
            {
                case Step.Avatar: return true;
                case Step.Meshes: return setup != null;
                case Step.Regions: return setup != null && setup.renderers.Any(r => r != null);
                case Step.Bake: return Reachable(Step.Regions, setup) && setup.regions.Any(r => r != null);
                default:
                    return Reachable(Step.Bake, setup) && setup.baked
                        && SquishBaker.SquishMaterials(setup).Count > 0
                        && setup.regions.Where(r => r != null).All(r => SquishBaker.FindCamera(r) != null);
            }
        }


        static void Hide(Component c)
        {
            if ((c.hideFlags & HideFlags.HideInInspector) != 0) return;

            c.hideFlags |= HideFlags.HideInInspector;
            EditorUtility.SetDirty(c);
        }

        void MarkDirty(SquishSetup setup)
        {
            if (setup != null) setup.baked = false;

            _overlays.Clear();

            if (setup != null) EditorUtility.SetDirty(setup);

            SceneView.RepaintAll();
        }

        void AvatarStep(SquishSetup setup)
        {
            List<VRCAvatarDescriptor> avatars = Object.FindObjectsOfType<VRCAvatarDescriptor>(true)
                .Where(a => a.gameObject.scene.IsValid())
                .OrderBy(a => a.gameObject.name)
                .ToList();

            List<string> labels = new List<string> { "None" };

            foreach (VRCAvatarDescriptor a in avatars)
            {
                string label = a.gameObject.name;
                int n = 2;
                while (labels.Contains(label)) label = $"{a.gameObject.name} ({n++})";
                labels.Add(label);
            }

            int index = EditorGUILayout.Popup("Avatar", avatars.IndexOf(_avatar) + 1, labels.ToArray());
            VRCAvatarDescriptor avatar = index > 0 ? avatars[index - 1] : null;

            if (avatar != _avatar)
            {
                _avatar = avatar;
                _selected = null;
                _geometry = null;
                _messages.Clear();
                return;
            }

            if (_avatar == null) return;

            EditorGUILayout.Space();

            if (setup == null)
            {
                if (GUILayout.Button("Add", GUILayout.Height(30))) CreateSetup();
            }
            else if (GUILayout.Button("Remove", GUILayout.Height(30)))
            {
                RemoveSetup(setup);
            }
        }

        void CreateSetup()
        {
            GameObject go = new GameObject(SetupName);

            Undo.RegisterCreatedObjectUndo(go, "Add VRC Squish");

            go.transform.SetParent(_avatar.transform, false);

            SquishSetup setup = Undo.AddComponent<SquishSetup>(go);

            setup.renderers.AddRange(PoiRenderers());

            Hide(setup);

            _messages.Clear();
        }

        void RemoveSetup(SquishSetup setup)
        {
            if (!EditorUtility.DisplayDialog("VRC Squish", $"Fully remove VRC Squish from {_avatar.name}?", "Remove", "Cancel")) return;
            RemoveCube();

            _selected = null;

            SetPreview(setup, false);

            _messages = SquishBaker.Uninstall(setup).Messages;

            SceneView.RepaintAll();
            GUIUtility.ExitGUI();
        }

        List<Renderer> PoiRenderers()
        {
            return _avatar.GetComponentsInChildren<Renderer>(true)
                .Where(r => r is SkinnedMeshRenderer || r is MeshRenderer)
                .Where(r => r.sharedMaterials.Any(m => m != null && m.shader.name.ToLowerInvariant().Contains("poiyomi")))
                .ToList();
        }

        void MeshesStep(SquishSetup setup)
        {
            List<Renderer> all = PoiRenderers();
            if (all.Count == 0)
            {
                EditorGUILayout.LabelField("None");
                return;
            }

            foreach (Renderer r in all)
            {
                bool on = setup.renderers.Contains(r);
                bool now = EditorGUILayout.ToggleLeft(r.name, on);
                if (now == on) continue;

                Undo.RecordObject(setup, "Meshes");

                if (now) setup.renderers.Add(r);
                else setup.renderers.Remove(r);

                _geometry = null;
                MarkDirty(setup);
            }
        }

        void RegionsStep(SquishSetup setup)
        {
            for (int i = 0; i < setup.regions.Count; i++)
            {
                SquishRegion region = setup.regions[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    string parent = region.transform.parent != null ? region.transform.parent.name : region.name;

                    if (GUILayout.Toggle(_selected == region, $"{i + 1}  {parent}", "Button") && _selected != region)
                    {
                        _selected = region;
                        FrameSelected();
                        SceneView.RepaintAll();
                    }

                    if (GUILayout.Button("X", GUILayout.Width(24)))
                    {
                        RemoveRegion(setup, region);
                        GUIUtility.ExitGUI();
                    }
                }
            }

            using (new EditorGUI.DisabledScope(setup.regions.Count >= SquishSetup.MaxRegions))
            using (new EditorGUILayout.HorizontalScope())
            {
                _bone = (HumanBodyBones)EditorGUILayout.EnumPopup(_bone);
                if (GUILayout.Button("Add", GUILayout.Width(60))) AddRegion(setup);
            }

            using (new EditorGUI.DisabledScope(setup.regions.Count == 0))
            {
                if (GUILayout.Button("Clear") && EditorUtility.DisplayDialog("VRC Squish", "Remove all regions?", "Remove", "Cancel"))
                {
                    _selected = null;

                    RemoveCube();

                    SquishBaker.ClearRegions(setup);

                    MarkDirty(setup);

                    GUIUtility.ExitGUI();
                }
            }

            if (_selected == null || !setup.regions.Contains(_selected)) return;

            EditorGUILayout.Space();
            EditorGUI.BeginChangeCheck();

            Vector3 size = EditorGUILayout.Vector3Field("Size", _selected.size);
            float softness = EditorGUILayout.Slider("Softness", _selected.softness, 0.01f, 1f);
            float depth = EditorGUILayout.FloatField("Depth", _selected.depth);

            if (!EditorGUI.EndChangeCheck()) return;

            Undo.RecordObject(_selected, "Region");
            _selected.size = Vector3.Max(size, Vector3.one * 0.005f);
            _selected.softness = softness;
            _selected.depth = Mathf.Max(depth, 0.005f);
            EditorUtility.SetDirty(_selected);
            MarkDirty(setup);
        }

        void AddRegion(SquishSetup setup)
        {
            Animator animator = _avatar.GetComponent<Animator>();
            Transform bone = null;
            if (animator != null && animator.isHuman && _bone != HumanBodyBones.LastBone) bone = animator.GetBoneTransform(_bone);

            if (bone == null)
            {
                _messages.Clear();
                _messages.Add($"{_bone}: missing");
                return;
            }

            GameObject go = new GameObject(RegionName);
            Undo.RegisterCreatedObjectUndo(go, "Add Region");

            go.transform.SetParent(bone, false);
            go.transform.SetPositionAndRotation(bone.position + _avatar.transform.forward * 0.1f, Quaternion.LookRotation(_avatar.transform.forward, _avatar.transform.up));

            SquishRegion region = Undo.AddComponent<SquishRegion>(go);
            Hide(region);

            Undo.RecordObject(setup, "Add Region");
            setup.regions.Add(region);

            _selected = region;

            MarkDirty(setup);
            FrameSelected();
        }

        void RemoveRegion(SquishSetup setup, SquishRegion region)
        {
            Undo.RecordObject(setup, "Remove Region");
            setup.regions.Remove(region);

            if (_selected == region) _selected = null;

            Undo.DestroyObjectImmediate(region.gameObject);
            MarkDirty(setup);
        }

        void FrameSelected()
        {
            if (_selected == null || SceneView.lastActiveSceneView == null) return;

            SceneView.lastActiveSceneView.Frame(new Bounds(_selected.transform.position, _selected.size * 5f), false);
        }

        void BakeStep(SquishSetup setup)
        {
            EditorGUI.BeginChangeCheck();
            int mask = InternalEditorUtility.LayerMaskToConcatenatedLayersMask(setup.layers);
            mask = EditorGUILayout.MaskField("Layers", mask, InternalEditorUtility.layers);
            int resolution = EditorGUILayout.IntPopup("Resolution", setup.resolution, ResolutionNames, Resolutions);
            bool hideSelf = EditorGUILayout.Toggle("Hide Self", setup.hideSelf);

            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(setup, "Bake Settings");
                setup.layers = InternalEditorUtility.ConcatenatedLayersMaskToLayerMask(mask);
                setup.resolution = resolution;
                setup.hideSelf = hideSelf;
                MarkDirty(setup);
            }

            EditorGUILayout.Space();

            if (GUILayout.Button("Bake", GUILayout.Height(34)))
            {
                BakeResult result = SquishBaker.Bake(setup);
                _messages = result.Messages;
                if (result.Success)
                {
                    setup.baked = true;
                    EditorUtility.SetDirty(setup);
                }
                GUIUtility.ExitGUI();
            }

            using (new EditorGUI.DisabledScope(SquishBaker.SquishMaterials(setup).Count == 0))
            {
                if (GUILayout.Button("Rebuild"))
                {
                    _messages = SquishBaker.RebuildShaders(setup).Messages;
                    GUIUtility.ExitGUI();
                }
            }
        }

        void ConfigStep(SquishSetup setup)
        {
            List<Material> mats = SquishBaker.SquishMaterials(setup);

            if (mats.Count == 0) return;

            using (new EditorGUI.DisabledScope(mats.Any(PoiLock.IsMaterialLocked)))
                ConfigProperties(mats);

            EditorGUILayout.Space();
            if (_cube == null)
            {
                if (GUILayout.Button("Cube")) CreateCube(setup);
            }
            else if (GUILayout.Button("Remove Cube"))
            {
                RemoveCube();
            }
        }

        static void ConfigProperties(List<Material> mats)
        {
            foreach ((string prop, string label, float min, float max) s in Sliders)
            {
                if (!mats[0].HasProperty(s.prop)) continue;

                float v = mats[0].GetFloat(s.prop);
                float nv = EditorGUILayout.Slider(s.label, v, s.min, s.max);

                if (Mathf.Approximately(nv, v)) continue;

                Undo.RecordObjects(mats.ToArray(), "Config");
                foreach (Material m in mats) m.SetFloat(s.prop, nv);
            }

            bool flip = mats[0].GetFloat("_SquishFlipY") > 0.5f;
            bool newFlip = EditorGUILayout.Toggle("Flip", flip);

            if (newFlip == flip) return;

            Undo.RecordObjects(mats.ToArray(), "Config");
            foreach (Material m in mats) m.SetFloat("_SquishFlipY", newFlip ? 1 : 0);
        }

        void CreateCube(SquishSetup setup)
        {
            SquishRegion region = _selected != null && setup.regions.Contains(_selected) ? _selected : setup.regions.FirstOrDefault(r => r != null);
            if (region == null) return;
            _cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            _cube.name = CubeName;
            Undo.RegisterCreatedObjectUndo(_cube, "Cube");
            DestroyImmediate(_cube.GetComponent<Collider>());
            _cube.layer = 9;
            _cube.transform.localScale = Vector3.one * Mathf.Max(region.OrthoHalfSize * 0.4f, 0.02f);
            _cube.transform.SetPositionAndRotation(region.RegionToWorld.MultiplyPoint3x4(new Vector3(0, 0, region.size.z + _cube.transform.localScale.z * 0.5f)), region.transform.rotation);
            Selection.activeGameObject = _cube;
        }

        void RemoveCube()
        {
            if (_cube != null) Undo.DestroyObjectImmediate(_cube);
            _cube = null;
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                Scene scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded) continue;

                foreach (GameObject root in scene.GetRootGameObjects())
                    if (root.name == CubeName) Undo.DestroyObjectImmediate(root);
            }
        }

        static readonly int SquishOnId = Shader.PropertyToID("_SquishOn");
        static MaterialPropertyBlock _previewBlock;

        static void SetPreview(SquishSetup setup, bool on)
        {
            foreach (Renderer r in setup.renderers)
            {
                if (r == null) continue;

                if (!on)
                {
                    if (r.HasPropertyBlock()) r.SetPropertyBlock(null);

                    continue;
                }

                if (_previewBlock == null) _previewBlock = new MaterialPropertyBlock();

                r.GetPropertyBlock(_previewBlock);
                _previewBlock.SetFloat(SquishOnId, 1);
                foreach (Material m in r.sharedMaterials)
                    if (m != null && PoiLock.IsMaterialLocked(m))
                        _previewBlock.SetFloat($"_SquishOn_{PoiLock.GetRenamedPropertySuffix(m)}", 1);
                r.SetPropertyBlock(_previewBlock);
            }
        }

        void Tick()
        {
            if (EditorApplication.isPlaying) return;
            SquishSetup setup = Setup;

            if (setup == null) return;

            bool preview = Reachable(Step.Config, setup);
            SetPreview(setup, preview);

            if (!preview) return;

            foreach (SquishRegion region in setup.regions)
            {
                Camera cam = region != null ? SquishBaker.FindCamera(region) : null;
                if (cam != null && cam.targetTexture != null) cam.Render();
            }

            SceneView.RepaintAll();
        }

        void OnSceneGUI(SceneView view)
        {
            if (!_visible || _cube != null || _avatar == null) return;

            SquishSetup setup = Setup;

            if (setup == null || !Reachable(Step.Regions, setup)) return;

            foreach (SquishRegion region in setup.regions)
                if (region != null) DrawOverlay(setup, region, region == _selected);

            if (_selected == null || !setup.regions.Contains(_selected)) return;

            Transform t = _selected.transform;
            Quaternion handleRotation = Tools.pivotRotation == PivotRotation.Local ? t.rotation : Quaternion.identity;
            Vector3 pos = t.position;
            Quaternion rot = t.rotation;
            EditorGUI.BeginChangeCheck();
            switch (Tools.current)
            {
                case Tool.Move:
                    pos = Handles.PositionHandle(pos, handleRotation);
                    break;
                case Tool.Rotate:
                    rot = Handles.RotationHandle(rot, pos);
                    break;
                case Tool.Transform:
                    Vector3 scale = Vector3.one;
                    Handles.TransformHandle(ref pos, ref rot, ref scale);
                    break;
            }
            if (EditorGUI.EndChangeCheck())
            {
                Undo.RecordObject(t, "Move Region");
                t.SetPositionAndRotation(pos, rot);
                MarkDirty(setup);
            }

            Matrix4x4 toWorld = _selected.RegionToWorld;
            using (new Handles.DrawingScope(Color.white, toWorld * Matrix4x4.Scale(new Vector3(_selected.size.x, _selected.size.y, 1f))))
            {
                Handles.zTest = CompareFunction.Always;
                float inner = Mathf.Max(1f - _selected.softness, 0.001f);
                Handles.color = new Color(0.1f, 0.1f, 0.15f, 0.85f);
                Handles.DrawWireDisc(Vector3.zero, Vector3.forward, 1f, 5f);
                Handles.color = new Color(0.1f, 0.1f, 0.15f, 0.5f);
                Handles.DrawWireDisc(Vector3.zero, Vector3.forward, inner, 3.5f);
                Handles.color = Color.white;
                Handles.DrawWireDisc(Vector3.zero, Vector3.forward, 1f, 2f);
                Handles.color = new Color(1f, 1f, 1f, 0.6f);
                Handles.DrawWireDisc(Vector3.zero, Vector3.forward, inner, 1.2f);
            }
            using (new Handles.DrawingScope(Color.white, toWorld))
            {
                Handles.zTest = CompareFunction.Always;
                EditorGUI.BeginChangeCheck();
                Vector3 size = _selected.size;
                for (int axis = 0; axis < 2; axis++)
                {
                    for (int sign = -1; sign <= 1; sign += 2)
                    {
                        Vector3 dir = Vector3.zero;
                        dir[axis] = sign;
                        Vector3 p = dir * size[axis];
                        float handleSize = HandleUtility.GetHandleSize(p) * 0.09f;
                        Vector3 moved = Handles.Slider(p, dir, handleSize, FacingDot, 0f);
                        size[axis] = Mathf.Max(0.005f, Mathf.Abs(moved[axis]));
                    }
                }

                if (!EditorGUI.EndChangeCheck()) return;

                Undo.RecordObject(_selected, "Region");
                _selected.size = size;
                EditorUtility.SetDirty(_selected);
                MarkDirty(setup);
                Repaint();
            }
        }

        static void FacingDot(int id, Vector3 position, Quaternion rotation, float size, EventType eventType)
        {
            Camera cam = Camera.current;
            Quaternion facing = cam != null ? Quaternion.Inverse(Handles.matrix.rotation) * cam.transform.rotation : rotation;
            if (eventType == EventType.Repaint)
            {
                Color color = Handles.color;
                Handles.color = new Color(0.1f, 0.1f, 0.15f, 0.85f);
                Handles.DotHandleCap(id, position, facing, size * 1.35f, eventType);
                Handles.color = color;
            }
            Handles.DotHandleCap(id, position, facing, size, eventType);
        }

        void DrawOverlay(SquishSetup setup, SquishRegion region, bool selected)
        {
            if (Event.current.type != EventType.Repaint) return;

            if (_geometry == null)
            {
                _geometry = setup.renderers.Where(r => r != null).Select(SquishGeometry.Capture).Where(g => g != null).ToList();
                _overlays.Clear();
            }

            if (!_overlays.TryGetValue(region, out List<OverlayVertex> overlay))
            {
                overlay = BuildOverlay(region);
                _overlays[region] = overlay;
            }

            if (overlay.Count == 0) return;

            EnsureSurfaceMaterial();
            _surfaceMaterial.SetFloat("_Softness", region.softness);
            _surfaceMaterial.SetFloat("_Alpha", selected ? 0.6f : 0.3f);
            _surfaceMaterial.SetFloat("_Outline", selected ? 1f : 0.5f);
            GL.PushMatrix();
            for (int pass = 0; pass < 2; pass++)
            {
                _surfaceMaterial.SetPass(pass);
                GL.Begin(GL.TRIANGLES);
                foreach (OverlayVertex v in overlay)
                {
                    GL.MultiTexCoord2(0, v.Local.x, v.Local.y);
                    GL.MultiTexCoord2(1, v.Distance, 0);
                    GL.Vertex(v.Position);
                }
                GL.End();
            }
            GL.PopMatrix();
        }

        struct OverlayVertex
        {
            public Vector3 Position;
            public Vector2 Local;
            public float Distance;
        }

        List<OverlayVertex> BuildOverlay(SquishRegion region)
        {
            List<OverlayVertex> result = new List<OverlayVertex>();
            Quaternion inv = Quaternion.Inverse(region.transform.rotation);
            Vector3 origin = region.transform.position;
            foreach (SquishGeometry g in _geometry)
            {
                Vector3[] locals = new Vector3[g.Positions.Length];
                float[] distances = new float[g.Positions.Length];
                for (int i = 0; i < locals.Length; i++)
                {
                    locals[i] = inv * (g.Positions[i] - origin);
                    distances[i] = region.Distance(locals[i]);
                }
                int[] tris = g.Triangles;
                for (int t = 0; t + 2 < tris.Length; t += 3)
                {
                    int a = tris[t], b = tris[t + 1], c = tris[t + 2];

                    if (distances[a] > 1.15f && distances[b] > 1.15f && distances[c] > 1.15f) continue;

                    foreach (int i in new[] { a, b, c })
                        result.Add(new OverlayVertex { Position = g.Positions[i] + g.Normals[i] * 0.0008f, Local = locals[i], Distance = distances[i] });
                }
            }
            return result;
        }

        const string OverlayShader = @"Shader ""Hidden/Ahoya/Gizmo""
{
    Properties
    {
        _Color (""Color"", Color) = (1, 0.45, 0.7, 1)
        _Edge (""Edge"", Color) = (0.3, 0.5, 1, 1)
        _Softness (""Softness"", Float) = 0.8
        _Alpha (""Alpha"", Float) = 0.6
        _Outline (""Outline"", Float) = 1
        _Stripe (""Stripe"", Float) = 0.008
    }
    SubShader
    {
        Tags { ""Queue"" = ""Transparent"" }
        Cull Off
        Pass
        {
            ZWrite On
            ZTest LEqual
            ColorMask 0
        }
        Pass
        {
            ZWrite Off
            ZTest LEqual
            Blend SrcAlpha OneMinusSrcAlpha
            CGPROGRAM
            #pragma vertex vert
            #pragma fragment frag
            #include ""UnityCG.cginc""

            float4 _Color;
            float4 _Edge;
            float _Softness;
            float _Alpha;
            float _Outline;
            float _Stripe;

            struct appdata
            {
                float4 vertex : POSITION;
                float2 local : TEXCOORD0;
                float2 distance : TEXCOORD1;
            };

            struct v2f
            {
                float4 pos : SV_POSITION;
                float2 local : TEXCOORD0;
                float distance : TEXCOORD1;
            };

            v2f vert(appdata v)
            {
                v2f o;
                o.pos = UnityObjectToClipPos(v.vertex);
                o.local = v.local;
                o.distance = v.distance.x;
                return o;
            }

            float4 frag(v2f i) : SV_Target
            {
                float e = i.distance;
                float fw = max(fwidth(e), 0.0001);
                float px = abs(e - 1.0) / fw;
                float rim = 1.0 - smoothstep(3.0, 4.0, px);
                float core = 1.0 - smoothstep(1.0, 2.0, px);
                if (e > 1.0 && rim <= 0.0) discard;

                float t = saturate((e - (1.0 - _Softness)) / max(_Softness, 0.0001));
                float w = 1.0 - t * t * (3.0 - 2.0 * t);
                float3 c = lerp(_Edge.rgb, _Color.rgb, w);

                float s = (i.local.x + i.local.y) / _Stripe;
                float sw = max(fwidth(s), 0.0001);
                float band = smoothstep(0.5 - sw, 0.5 + sw, abs(frac(s) * 2.0 - 1.0));
                c *= lerp(0.82, 1.12, band);

                float a = e <= 1.0 ? _Alpha : 0.0;
                float3 outline = lerp(_Edge.rgb * 0.35, lerp(_Edge.rgb, 1.0, 0.25), core);
                c = lerp(c, outline, rim * _Outline);
                a = max(a, rim * _Outline);
                return float4(c, a);
            }
            ENDCG
        }
    }
}";

        static void EnsureSurfaceMaterial()
        {
            if (_surfaceMaterial != null) return;

            Shader shader = ShaderUtil.CreateShaderAsset(OverlayShader, false);
            shader.hideFlags = HideFlags.HideAndDontSave;
            _surfaceMaterial = new Material(shader) { hideFlags = HideFlags.HideAndDontSave };
            _surfaceMaterial.SetColor("_Color", ShapeColor);
            _surfaceMaterial.SetColor("_Edge", EdgeColor);
        }

        static VRCAvatarDescriptor SelectedAvatar()
        {
            GameObject go = Selection.activeGameObject;

            return go != null ? go.GetComponentInParent<VRCAvatarDescriptor>(true) : null;
        }
    }
}

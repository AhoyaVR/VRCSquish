using System.Collections.Generic;
using System.IO;
using System.Linq;
using Poiyomi.ModularShaderSystem;
using UnityEditor;
using UnityEngine;

namespace VRCSquish.Editor
{
    public static class VRCSquishShaderInstaller
    {
        const string Root = "Assets/VRCSquish";
        const string TemplatePath = Root + "/Shader/VRLTC_VRCSquish.poiTemplateCollection";
        const string ModulePath = Root + "/Shader/VRLM_VRCSquish.asset";
        const string ModularShaderFolder = Root + "/Shader/ModularShaders";
        const string GeneratedShaderFolder = Root + "/Shader/Generated";
        const string SkeletonPath = ModularShaderFolder + "/VRLM_VRCSquishTessellationSkeleton.asset";
        const string ShaderPathPrefix = ".poiyomi/User Modules/";
        const string Suffix = " Squish";

        const string VertFragSkeleton = "VRLM_PoiVertFragSkeleton";
        const string HullDomainSkeleton = "VRLM_PoiVertHullDomainFragSkeleton";
        const string EarlyZPassModule = "VRLM_PoiPassEarlyZ";
        const string PoiHullDomainProgram = "PoiVertHullDomainProgram";
        const string PoiTessellationProperties = "PoiTessellationProperties";
        const string SquishHullDomainProgram9 = "VRCSquishHullDomainProgram";
        const string SquishHullDomainProgram10 = "VRCSquishHullDomainProgram10";

        public const int ModuleVersion = 14;

        public static void Install(List<ModularShader> sources)
        {
            ShaderModule module = GetOrCreateModule();

            if (module == null) return;

            EnsureFolder(ModularShaderFolder);
            EnsureFolder(GeneratedShaderFolder);

            try
            {
                for (int i = 0; i < sources.Count; i++)
                {
                    ModularShader src = sources[i];
                    EditorUtility.DisplayProgressBar("VRC Squish", src.ShaderPath, (float)i / sources.Count);
                    ShaderGenerator.GenerateShader(GeneratedShaderFolder, GetOrCreateCopy(src, module));
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

        public static bool IsSquishShader(Shader s) => s != null && s.name.StartsWith(ShaderPathPrefix) && s.name.EndsWith(Suffix);

        public static Shader FindSquishVariant(string sourceShaderName) =>
            Shader.Find(ShaderPathPrefix + Path.GetFileName(sourceShaderName) + Suffix);

        public static bool IsUpToDate(Shader s)
        {
            int i = s != null ? s.FindPropertyIndex("_VRCSquishVersion") : -1;

            return i >= 0 && s.GetPropertyDefaultFloatValue(i) >= ModuleVersion;
        }

        public static string SourceShaderName(Shader s)
        {
            if (!IsSquishShader(s)) return s.name;

            string bare = s.name.Substring(ShaderPathPrefix.Length, s.name.Length - ShaderPathPrefix.Length - Suffix.Length);
            return ".poiyomi/" + bare;
        }

        public static ModularShader FindModularShader(string shaderName)
        {
            return AssetDatabase.FindAssets("t:ModularShader")
                .Select(g => AssetDatabase.LoadAssetAtPath<ModularShader>(AssetDatabase.GUIDToAssetPath(g)))
                .FirstOrDefault(s => s != null && s.ShaderPath == shaderName);
        }

        static ShaderModule GetOrCreateModule()
        {
            AssetDatabase.ImportAsset(TemplatePath, ImportAssetOptions.ForceUpdate);
            Dictionary<string, TemplateAsset> templates = AssetDatabase.LoadAllAssetsAtPath(TemplatePath).OfType<TemplateAsset>().ToDictionary(t => t.name);

            if (templates.Count == 0)
            {
                Debug.LogError($"[VRC Squish] Missing {TemplatePath}");
                return null;
            }

            ShaderModule module = AssetDatabase.LoadAssetAtPath<ShaderModule>(ModulePath);
            bool isNew = module == null;
            if (isNew) module = ScriptableObject.CreateInstance<ShaderModule>();

            module.Id = "VRCSquish";
            module.Name = "Squish";
            module.Version = ModuleVersion.ToString();
            module.Author = "Ahoya";
            module.Description = "";
            module.EnableProperties = new List<EnableProperty>();
            module.Properties = new List<Property>();
            module.ModuleDependencies = new List<string>();
            module.IncompatibleWith = new List<string>();
            module.Functions = new List<ShaderFunction>();

            module.Templates = new List<ModuleTemplate>
            {
                Template(templates["VRCSquishProperties"], "THIRDPARTY_PROPERTIES"),
                Template(templates["VRCSquishKeywords"], "SHADER_KEYWORDS"),
                Template(templates["VRCSquishVariables"], "BASE_PROPERTY_VARIABLES", "ADD_PROPERTY_VARIABLES", "SHADOW_PROPERTY_VARIABLES", "OUTLINE_PROPERTY_VARIABLES", "EARLYZ_PROPERTY_VARIABLES", "META_PROPERTY_VARIABLES", "FUR_BASE_PROPERTY_VARIABLES", "DEPTH_PROPERTY_VARIABLES", "MOTION_VECTORS_PROPERTY_VARIABLES", "LILFUR_PROPERTY_VARIABLES"),
                Template(templates["VRCSquishFunctions"], "VERTEX_BASE_FUNCTIONS", "VERTEX_ADD_FUNCTIONS", "VERTEX_SHADOW_FUNCTIONS", "VERTEX_OUTLINE_FUNCTIONS", "VERTEX_EARLYZ_FUNCTIONS", "VERTEX_META_FUNCTIONS", "VERTEX_FUR_BASE_FUNCTIONS", "VERTEX_DEPTH_FUNCTIONS", "VERTEX_MOTION_VECTORS_FUNCTIONS", "VERTEX_LILFUR_FUNCTIONS"),
                Template(templates["VRCSquishFunctionCalls"], "VERTEX_INPUT_MODS"),
                Template(templates["VRCSquishPostClip"], "VERTEX_MODS_POST_CLIPPOS"),
            };

            if (isNew) AssetDatabase.CreateAsset(module, ModulePath);

            else EditorUtility.SetDirty(module);

            AssetDatabase.SaveAssets();

            return module;
        }

        static ModuleTemplate Template(TemplateAsset asset, params string[] keywords)
        {
            return new ModuleTemplate { Template = asset, Keywords = keywords.ToList(), NeedsVariant = false, Queue = 100 };
        }

        static ModularShader GetOrCreateCopy(ModularShader src, ShaderModule module)
        {
            string path = $"{ModularShaderFolder}/{src.Id.Replace(" ", "")}{Suffix.Trim()}.asset";
            ModularShader copy = AssetDatabase.LoadAssetAtPath<ModularShader>(path);
            bool isNew = copy == null;

            if (isNew) copy = ScriptableObject.CreateInstance<ModularShader>();

            copy.Id = src.Id + "Squish";
            copy.Name = (string.IsNullOrEmpty(src.Name) ? src.Id : src.Name) + Suffix;
            copy.Version = src.Version;
            copy.Author = src.Author;
            copy.Description = src.Description;
            copy.UseTemplatesForProperties = src.UseTemplatesForProperties;
            copy.ShaderPropertiesTemplate = src.ShaderPropertiesTemplate;
            copy.ShaderTemplate = src.ShaderTemplate;
            copy.CustomEditor = src.CustomEditor;
            copy.Properties = src.Properties.ToList();
            copy.ShaderPath = ShaderPathPrefix + Path.GetFileName(src.ShaderPath) + Suffix;

            ShaderModule skeleton = GetOrCreateTessellationSkeleton();
            bool tessellated = false;
            copy.BaseModules = new List<ShaderModule>();

            foreach (ShaderModule m in src.BaseModules)
            {
                if (m == null || m.Id == module.Id) continue;
                if (skeleton != null && m.name == EarlyZPassModule) continue;
                if (skeleton != null && (m.name == VertFragSkeleton || m.name == HullDomainSkeleton))
                {
                    copy.BaseModules.Add(skeleton);
                    tessellated = true;
                    continue;
                }
                copy.BaseModules.Add(m);
            }

            copy.BaseModules.Add(module);

            if (!tessellated) Debug.LogWarning($"[VRC Squish] {src.ShaderPath}: no tessellation");

            copy.AdditionalModules = (src.AdditionalModules ?? new List<ShaderModule>()).ToList();
            copy.LockBaseModules = src.LockBaseModules;
            copy.AdditionalSerializedData = src.AdditionalSerializedData;

            if (copy.LastGeneratedShaders == null) copy.LastGeneratedShaders = new List<Shader>();

            CopyField(src, copy, "DisabledSections");

            if (isNew) AssetDatabase.CreateAsset(copy, path);

            else EditorUtility.SetDirty(copy);
            return copy;
        }

        static ShaderModule GetOrCreateTessellationSkeleton()
        {
            ShaderModule poi = AssetDatabase.FindAssets($"{HullDomainSkeleton} t:ShaderModule")
                .Select(g => AssetDatabase.LoadAssetAtPath<ShaderModule>(AssetDatabase.GUIDToAssetPath(g)))
                .FirstOrDefault(m => m != null && m.name == HullDomainSkeleton);

            TemplateAsset poiProgram = poi?.Templates.Select(t => t.Template).FirstOrDefault(t => t != null && t.name == PoiHullDomainProgram);

            if (poiProgram == null)
            {
                Debug.LogError($"[VRC Squish] Missing {HullDomainSkeleton}");
                return null;
            }

            string programName = SquishProgramFor(poiProgram.Template);

            if (programName == null)
            {
                Debug.LogError("[VRC Squish] Unsupported Poiyomi tessellation");
                return null;
            }

            TemplateAsset program = AssetDatabase.LoadAllAssetsAtPath(TemplatePath).OfType<TemplateAsset>()
                .FirstOrDefault(t => t.name == programName);

            if (program == null)
            {
                Debug.LogError($"[VRC Squish] Missing {programName}");
                return null;
            }

            EnsureFolder(ModularShaderFolder);
            ShaderModule skeleton = AssetDatabase.LoadAssetAtPath<ShaderModule>(SkeletonPath);
            bool isNew = skeleton == null;
            if (isNew) skeleton = ScriptableObject.CreateInstance<ShaderModule>();

            skeleton.Id = poi.Id;
            skeleton.Name = "VRC Squish Tessellation";
            skeleton.Version = poi.Version;
            skeleton.Author = "Ahoya";
            skeleton.Description = "";
            skeleton.AllowDuplicates = poi.AllowDuplicates;
            skeleton.EnableProperties = poi.EnableProperties.ToList();
            skeleton.Properties = poi.Properties.ToList();
            skeleton.ModuleDependencies = poi.ModuleDependencies.ToList();
            skeleton.IncompatibleWith = poi.IncompatibleWith.ToList();
            skeleton.Functions = poi.Functions.ToList();
            skeleton.AdditionalSerializedData = poi.AdditionalSerializedData;
            skeleton.Templates = poi.Templates
                .Where(t => t.Template == null || t.Template.name != PoiTessellationProperties)
                .Select(t => t.Template != null && t.Template.name == PoiHullDomainProgram 
                ? new ModuleTemplate { Template = program, Keywords = t.Keywords.ToList(), NeedsVariant = t.NeedsVariant, Queue = t.Queue } : t).ToList();

            CopyField(poi, skeleton, "ForceDuplicateLogic");

            if (isNew) AssetDatabase.CreateAsset(skeleton, SkeletonPath);

            else EditorUtility.SetDirty(skeleton);

            return skeleton;
        }

        static string SquishProgramFor(string poiProgram)
        {
            if (string.IsNullOrEmpty(poiProgram)) return null;

            if (poiProgram.Contains("vertPrePass")) return SquishHullDomainProgram10;

            if (poiProgram.Contains("tessAppData") && poiProgram.Contains("vertexId")) return SquishHullDomainProgram9;

            return null;
        }

        static void CopyField(Object from, Object to, string field)
        {
            SerializedProperty source = new SerializedObject(from).FindProperty(field);

            if (source == null) return;

            SerializedObject target = new SerializedObject(to);
            target.CopyFromSerializedProperty(source);
            target.ApplyModifiedPropertiesWithoutUndo();
        }

        internal static void EnsureFolder(string folder)
        {
            folder = folder.TrimEnd('/');

            if (AssetDatabase.IsValidFolder(folder)) return;

            string parent = Path.GetDirectoryName(folder)?.Replace('\\', '/');

            if (!string.IsNullOrEmpty(parent)) EnsureFolder(parent);

            AssetDatabase.CreateFolder(parent, Path.GetFileName(folder));
        }
    }
}

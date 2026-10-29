using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace VRCSquish.Editor
{
    public static class PoiLock
    {
        const BindingFlags Flags = BindingFlags.Public | BindingFlags.Static;

        static readonly Type Optimizer = FindOptimizer();
        static readonly MethodInfo IsLockedMethod = Find("IsMaterialLocked");
        static readonly MethodInfo OriginalMethod = Find("GetOriginalShader");
        static readonly MethodInfo SuffixMethod = Find("GetRenamedPropertySuffix");
        static readonly MethodInfo SetLockedMethod = Find("SetLockedForAllMaterials");

        public static bool IsMaterialLocked(Material material)
        {
            if (IsLockedMethod == null || material == null) return false;

            return (bool)IsLockedMethod.Invoke(null, new object[] { material });
        }

        public static Shader GetOriginalShader(Material material)
        {
            if (OriginalMethod == null) return material.shader;

            return (Shader)OriginalMethod.Invoke(null, new object[] { material });
        }

        public static string GetRenamedPropertySuffix(Material material)
        {
            if (SuffixMethod == null) return material.GetTag("thry_rename_suffix", false, "");

            return (string)SuffixMethod.Invoke(null, new object[] { material });
        }

        public static void SetLockedForAllMaterials(IEnumerable<Material> materials, int lockState)
        {
            if (SetLockedMethod == null) return;

            ParameterInfo[] parameters = SetLockedMethod.GetParameters();
            object[] args = new object[parameters.Length];
            for (int i = 0; i < parameters.Length; i++)
            {
                ParameterInfo p = parameters[i];
                switch (p.Name)
                {
                    case "materials": args[i] = materials.ToList(); break;
                    case "lockState": args[i] = lockState; break;
                    case "showProgressbar": args[i] = true; break;
                    case "showDialog": args[i] = false; break;
                    case "allowCancel": args[i] = false; break;
                    default: args[i] = p.HasDefaultValue ? p.DefaultValue : null; break;
                }
            }
            SetLockedMethod.Invoke(null, args);
        }

        static Type FindOptimizer()
        {
            foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                foreach (Type type in Types(assembly))
                {
                    if (type.Name != "ShaderOptimizer" || type.Namespace == null || !type.Namespace.StartsWith("Thry")) continue;

                    if (type.GetMethod("IsMaterialLocked", Flags) != null) return type;
                }
            }
            return null;
        }

        static IEnumerable<Type> Types(Assembly assembly)
        {
            try
            {
                return assembly.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                return e.Types.Where(t => t != null);
            }
        }

        static MethodInfo Find(string name)
        {
            if (Optimizer == null) return null;

            return Optimizer.GetMethods(Flags).FirstOrDefault(m => m.Name == name);
        }
    }
}

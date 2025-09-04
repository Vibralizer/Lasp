#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;

namespace Lasp.Editor
{
    internal static class LaspBackendDefines
    {
        const string DefLibsoundio = "LASP_BACKEND_LIBSOUNDIO";
        const string DefMiniaudio  = "LASP_BACKEND_MINIAUDIO";

        public static void Apply(LaspBackend choice)
        {
            // libsoundio is default
            string chosen = null;
            switch (choice)
            {
                case LaspBackend.libsoundio: chosen = DefLibsoundio; break;
                case LaspBackend.miniaudio:  chosen = DefMiniaudio;  break;
            }

            foreach (var group in GetValidBuildTargetGroups())
            {
                var nbt = NamedBuildTarget.FromBuildTargetGroup(group);
                
                PlayerSettings.GetScriptingDefineSymbols(nbt, out string[] definesArr);

                var set = new HashSet<string>(definesArr ?? Array.Empty<string>(), StringComparer.Ordinal);
                set.Remove(DefLibsoundio);
                set.Remove(DefMiniaudio);
                set.Add(chosen);

                var ordered = set.OrderBy(s => s, StringComparer.Ordinal).ToArray();
                PlayerSettings.SetScriptingDefineSymbols(nbt, ordered);
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }
        
        static IEnumerable<BuildTargetGroup> GetValidBuildTargetGroups()
        {
            var list = new List<BuildTargetGroup>();
            foreach (BuildTargetGroup g in Enum.GetValues(typeof(BuildTargetGroup)))
            {
                if (g == BuildTargetGroup.Unknown) continue;
                try
                {
                    var nbt = NamedBuildTarget.FromBuildTargetGroup(g);
                    PlayerSettings.GetScriptingDefineSymbols(nbt, out string[] _);
                    list.Add(g);
                }
                catch { /* ignore unsupported groups */ }
            }
            return list;
        }
    }
}
#endif

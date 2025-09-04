#if UNITY_EDITOR
using UnityEditor;
using UnityEngine;
using System.IO;

namespace Lasp.Editor
{
    internal enum LaspBackend
    { 
      libsoundio,
      miniaudio
    }

    [System.Serializable]
    internal sealed class LaspSettings : ScriptableObject
    {
        public LaspBackend backend = LaspBackend.libsoundio;

        // Key used by EditorBuildSettings
        const string Key = "LaspSettings";

        // Path for the persisted asset (must be under Assets/)
        const string AssetPath = "Assets/ProjectSettings/LaspSettings.asset";

        internal static LaspSettings LoadOrCreate()
        {
            // If a config object is already registered, use it
            if (EditorBuildSettings.TryGetConfigObject(Key, out LaspSettings s) && s)
                return s;

            // Try to load an existing asset from disk
            s = AssetDatabase.LoadAssetAtPath<LaspSettings>(AssetPath);
            if (!s)
            {
                // Ensure directory exists
                var dir = Path.GetDirectoryName(AssetPath);
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

                // Create & persist the asset under Assets/
                s = CreateInstance<LaspSettings>();
                s.hideFlags |= HideFlags.HideInInspector; // keep it out of the Project view
                AssetDatabase.CreateAsset(s, AssetPath);
                AssetDatabase.SaveAssets();
            }

            // Register the persisted asset with EditorBuildSettings
            EditorBuildSettings.AddConfigObject(Key, s,  overwrite:true);
            return s;
        }

        internal static void Save(LaspSettings s)
        {
            if (!s) return;
            EditorUtility.SetDirty(s);
            AssetDatabase.SaveAssets();
        }
    }
}
#endif
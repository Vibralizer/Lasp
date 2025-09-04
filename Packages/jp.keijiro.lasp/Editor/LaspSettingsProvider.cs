#if UNITY_EDITOR
using UnityEditor;

namespace Lasp.Editor
{
    internal static class LaspSettingsProvider
    {
        [SettingsProvider]
        public static SettingsProvider Create()
        {
            return new SettingsProvider("Project/Lasp", SettingsScope.Project)
            {
                label = "Lasp",
                guiHandler = _ =>
                {
                    var s = LaspSettings.LoadOrCreate();

                    // Dropdown applies the backend change immediately
                    var newBackend = (LaspBackend)EditorGUILayout.EnumPopup("Backend", s.backend);
                    if (newBackend != s.backend)
                    {
                        s.backend = newBackend;
                        LaspSettings.Save(s);
                        LaspBackendDefines.Apply(s.backend); // update defines immediately
                    }

                    EditorGUILayout.HelpBox(
                        "Changing the backend updates scripting define symbols immediately. Unity will recompile after you switch.",
                        MessageType.Info);
                }
            };
        }
    }
}
#endif
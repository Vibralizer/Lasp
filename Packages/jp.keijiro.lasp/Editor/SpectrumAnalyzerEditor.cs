using UnityEngine;
using UnityEditor;

namespace Lasp.Editor
{
    //
    // Custom editor (inspector) for SpectrumAnalyzer
    //
    [CanEditMultipleObjects]
    [CustomEditor(typeof(SpectrumAnalyzer))]
    sealed class SpectrumAnalyzerEditor : UnityEditor.Editor
    {
        SerializedProperty _channel;
        SerializedProperty _resolution;
        SerializedProperty _mixToMono;
        DeviceSelector _deviceSelector;
        DynamicRangeEditor _dynamicRange;

        static GUIContent [] _resolutionLabels = {
            new GUIContent("128"), new GUIContent("256"),
            new GUIContent("512"), new GUIContent("1024"),
            new GUIContent("2048")
        };

        static int [] _resolutionOptions = { 128, 256, 512, 1024, 2048 };

        public override bool RequiresConstantRepaint()
        {
            // Keep updated while playing.
            return EditorApplication.isPlaying && targets.Length == 1;
        }

        void OnEnable()
        {
            var finder = new PropertyFinder(serializedObject);
            _channel = finder["_channel"];
            _resolution = finder["_resolution"];
            _mixToMono = finder["_mixToMono"];

            _deviceSelector = new DeviceSelector(serializedObject);
            _dynamicRange = new DynamicRangeEditor(serializedObject);
        }

        public override void OnInspectorGUI()
        {
            var targetComponent = (SpectrumAnalyzer)target;

            serializedObject.Update();

            // Device selection (disabled during play mode)
            using (new EditorGUI.DisabledScope(EditorApplication.isPlaying))
                _deviceSelector.ShowGUI();

            // Mix to mono
            EditorGUILayout.PropertyField(_mixToMono);

            // Channel selection is disabled when Mix to Mono is on.
            if (!_mixToMono.boolValue)
            {
                // Channel selection
                EditorGUILayout.PropertyField(_channel);
            }

            // Spectrum resolution (disabled during play mode)
            using (new EditorGUI.DisabledScope(EditorApplication.isPlaying))
                EditorGUILayout.IntPopup
                  (_resolution, _resolutionLabels, _resolutionOptions);

            // Dynamic range properties
            _dynamicRange.ShowGUI();

            // Spectrum graph
            if (targets.Length == 1 && EditorApplication.isPlaying)
                SpectrumDrawer.DrawGraph(targetComponent.logSpectrumSpan);

            // Reset peak button
            _dynamicRange.ShowResetPeakButton(targets);

            serializedObject.ApplyModifiedProperties();
        }
    }
}

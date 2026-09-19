using UnityEngine;
using UnityEditor;

namespace AreaCapture.Editor
{
    [CustomEditor(typeof(CaptureZone))]
    public class CaptureZoneEditor : UnityEditor.Editor
    {
        public override void OnInspectorGUI()
        {
            DrawDefaultInspector();

            var zone = (CaptureZone)target;

            EditorGUILayout.Space();

            string rotationNote = AreaCaptureExporter.GetRotationNote(zone, out MessageType rotationType);
            if (rotationNote != null)
                EditorGUILayout.HelpBox(rotationNote, rotationType);

            var settings = AreaCaptureExporter.LoadSettingsFromPrefs();
            int imageCount = AreaCaptureExporter.CountImages(zone, settings);
            EditorGUILayout.HelpBox(
                $"Export with the saved settings produces {imageCount} image(s) (max tile {AreaCaptureExporter.EffectiveMaxTilePixels(settings)} px).",
                MessageType.None);

            EditorGUILayout.Space(4);

            GUI.backgroundColor = Color.green;
            if (GUILayout.Button(new GUIContent("Quick Export (Last Settings)",
                "Exports this zone using the settings last saved in the Area Capture window."),
                GUILayout.Height(30)))
            {
                AreaCaptureExporter.ExportZones(new[] { zone }, settings, success =>
                {
                    if (success)
                        EditorUtility.DisplayDialog("Success",
                            $"Export completed!\nFiles saved to: {settings.OutputDirectory}", "OK");
                    else
                        EditorUtility.DisplayDialog("Aborted",
                            "Export was canceled or failed. Check the Console for details.", "OK");
                });
            }
            GUI.backgroundColor = Color.white;

            EditorGUILayout.LabelField(
                "Uses settings from the last Area Capture window session.",
                EditorStyles.centeredGreyMiniLabel);
        }
    }
}

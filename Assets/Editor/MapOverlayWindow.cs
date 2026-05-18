using UnityEditor;
using UnityEngine;

public class MapOverlayWindow : EditorWindow
{
    private MapBounds mapBounds;
    private MapTag mapTag;
    private int resolution = 512;
    private string fileName = "overlay_map.png";

    [MenuItem("Tools/Map Overlay/Generator")]
    public static void Open()
    {
        GetWindow<MapOverlayWindow>("Map Overlay");
    }

    void OnGUI()
    {
        GUILayout.Label("Overlay Map Generator", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        mapBounds = (MapBounds)EditorGUILayout.ObjectField(
            "Map Bounds",
            mapBounds,
            typeof(MapBounds),
            true);

        mapTag = (MapTag)EditorGUILayout.ObjectField(
            "Map Tag",
            mapTag,
            typeof(MapTag),
            false);

        resolution = EditorGUILayout.IntPopup(
            "Resolution",
            resolution,
            new[] { "256", "512", "1024" },
            new[] { 256, 512, 1024 });

        fileName = EditorGUILayout.TextField(
            "Output File",
            fileName);

        EditorGUILayout.Space();

        GUI.enabled = mapBounds && mapTag;

        if (GUILayout.Button("Generate Overlay Map", GUILayout.Height(30)))
        {
            Generate();
        }

        GUI.enabled = true;
    }

    void Generate()
    {
        Texture2D tex =
            MapoverlayGenerator.Generate(
                mapBounds,
                mapTag,
                resolution);

        if (tex == null)
        {
            Debug.LogError("Failed to generate map");
            return;
        }

        string path =
            EditorUtility.SaveFilePanel(
                "Save Overlay Map",
                "Assets",
                fileName,
                "png");

        if (string.IsNullOrEmpty(path))
            return;

        byte[] png = tex.EncodeToPNG();
        System.IO.File.WriteAllBytes(path, png);

        AssetDatabase.Refresh();

        Debug.Log("Overlay map generated: " + path);
    }
}

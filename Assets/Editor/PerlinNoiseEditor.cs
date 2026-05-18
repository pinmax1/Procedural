using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(HeightMapWithPerlin))]
public class PerlinTerrainEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        HeightMapWithPerlin gen = (HeightMapWithPerlin)target;

        if (GUILayout.Button("Generate Terrain"))
        {
            gen.Generate();
        }
    }
}
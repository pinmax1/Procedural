using UnityEditor;
using UnityEngine;

[CustomEditor(typeof(PoissonSpawner))]
public class PoissonSpawnerEditor : Editor
{
    public override void OnInspectorGUI()
    {
        DrawDefaultInspector();

        PoissonSpawner spawner = (PoissonSpawner)target;

        GUILayout.Space(10);

        if (GUILayout.Button("Generate"))
        {
            spawner.Generate();
        }

        if (GUILayout.Button("Clear"))
        {
            spawner.Clear();
        }
    }
}
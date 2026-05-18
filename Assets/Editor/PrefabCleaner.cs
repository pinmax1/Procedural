using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

public class PrefabCleaner : EditorWindow
{
    private GameObject prefab;

    [MenuItem("Tools/Prefab Cleaner")]
    public static void Open()
    {
        GetWindow<PrefabCleaner>("Prefab Cleaner");
    }

    void OnGUI()
    {
        GUILayout.Label("Delete all instances of a prefab", EditorStyles.boldLabel);
        EditorGUILayout.Space();

        prefab = (GameObject)EditorGUILayout.ObjectField("Prefab", prefab, typeof(GameObject), false);

        EditorGUILayout.Space();

        GUI.enabled = prefab != null;

        if (GUILayout.Button("Find", GUILayout.Height(25)))
        {
            List<GameObject> found = FindInstances();
            Debug.Log($"PrefabCleaner: found {found.Count} instances of '{prefab.name}'.");
        }

        GUI.color = new Color(1f, 0.4f, 0.4f);
        if (GUILayout.Button("Delete All", GUILayout.Height(30)))
        {
            List<GameObject> found = FindInstances();
            if (found.Count == 0)
            {
                Debug.Log("PrefabCleaner: nothing to delete.");
                return;
            }

            if (EditorUtility.DisplayDialog(
                "Confirm Delete",
                $"Delete {found.Count} instances of '{prefab.name}'?",
                "Delete", "Cancel"))
            {
                Undo.SetCurrentGroupName($"Delete {found.Count} prefab instances");
                int group = Undo.GetCurrentGroup();

                foreach (GameObject obj in found)
                {
                    Undo.DestroyObjectImmediate(obj);
                }

                Undo.CollapseUndoOperations(group);
                Debug.Log($"PrefabCleaner: deleted {found.Count} instances of '{prefab.name}'.");
            }
        }
        GUI.color = Color.white;

        GUI.enabled = true;
    }

    List<GameObject> FindInstances()
    {
        List<GameObject> result = new();
        GameObject[] allObjects = FindObjectsByType<GameObject>(FindObjectsSortMode.None);

        foreach (GameObject obj in allObjects)
        {
            GameObject source = PrefabUtility.GetCorrespondingObjectFromSource(obj);
            if (source == prefab)
                result.Add(obj);
        }

        return result;
    }
}

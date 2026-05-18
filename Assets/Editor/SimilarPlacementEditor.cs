using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

[CustomEditor(typeof(SimilarPlacement))]
public class SimilarPlacementEditor : Editor
{
    enum PickMode { Off, Positive, Negative }

    static PickMode pickMode = PickMode.Off;
    static List<GameObject> hiddenObjects = new();
    static bool useCurvatureMetric;
    static bool useLocalHeightDifferenceMetric;

    void OnEnable()
    {
        SceneView.duringSceneGui += OnSceneGUI;
    }

    void OnDisable()
    {
        SceneView.duringSceneGui -= OnSceneGUI;
        SetPickMode(PickMode.Off);
    }

    public override void OnInspectorGUI()
    {
        serializedObject.Update();

        EditorGUILayout.PropertyField(serializedObject.FindProperty("bounds"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("jfaShader"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("referenceTag"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("distanceTags"));

        GUILayout.Space(8);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("prefab"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("countToPlace"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("minSpacing"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("terrainMask"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("candidateGridResolution"));

        GUILayout.Space(8);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("featureMapResolution"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("distanceFieldBackend"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("useFastPointDistanceFields"));
        EditorGUILayout.PropertyField(serializedObject.FindProperty("fastDistanceSeedRadiusPixels"));

        GUILayout.Space(8);
        EditorGUILayout.LabelField("Debug", EditorStyles.boldLabel);
        DrawBool("saveDebugTextures", "Save Debug");

        GUILayout.Space(8);
        EditorGUILayout.LabelField("Metrics Settings", EditorStyles.boldLabel);
        DrawBool("useHeightFeature", "Use Height");
        DrawBool("useSlopeFeature", "Use Slope");
        DrawBool("useAspectFeature", "Use Aspect");
        DrawBool("useCliffDistanceFeature", "Use Cliff Distance");
        DrawBool("useConvexDistanceFeature", "Use Convex Distance");
        useCurvatureMetric = EditorGUILayout.Toggle("Use Curvature", useCurvatureMetric);
        useLocalHeightDifferenceMetric = EditorGUILayout.Toggle("Use Local Height Difference", useLocalHeightDifferenceMetric);
        DrawCustomMetricList();

        GUILayout.Space(8);
        EditorGUILayout.PropertyField(serializedObject.FindProperty("placedObjects"));

        serializedObject.FindProperty("collectGenerationMetrics").boolValue = true;
        serializedObject.FindProperty("saveGenerationMetrics").boolValue = true;

        serializedObject.ApplyModifiedProperties();

        SimilarPlacement placer = (SimilarPlacement)target;

        GUILayout.Space(10);

        if (GUILayout.Button("Generate Feature Maps"))
            placer.GenerateFeatureMaps();

        if (GUILayout.Button("Place Objects From Generated Maps"))
            placer.PlaceObjectsFromGeneratedMaps();

        if (GUILayout.Button("Clear Generated Feature Maps"))
            placer.ClearGeneratedFeatureMaps();

        if (GUILayout.Button("Generate Full (Maps + Placement)"))
            placer.Generate();

        if (GUILayout.Button("Clear Placed"))
            placer.Clear();

        GUILayout.Space(10);
        EditorGUILayout.LabelField("Pick Positive References", EditorStyles.boldLabel);

        Color prevBg = GUI.backgroundColor;
        bool isPosPick = pickMode == PickMode.Positive;
        if (isPosPick) GUI.backgroundColor = Color.green;

        string posPickLabel = isPosPick
            ? "Positive Pick ON - click to add reference"
            : "Pick Positive References";

        if (GUILayout.Button(posPickLabel, GUILayout.Height(28)))
            SetPickMode(isPosPick ? PickMode.Off : PickMode.Positive);

        GUI.backgroundColor = prevBg;

        if (isPosPick)
        {
            EditorGUILayout.HelpBox(
                "Click objects in the Scene view to tag them as positive references.\n" +
                "Click the button again to stop.",
                MessageType.Info);
        }

        if (placer.referenceTag != null)
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Hide Positive Refs"))
                HideObjectsWithTag(placer.referenceTag);
            if (GUILayout.Button("Show Positive Refs"))
                ShowObjectsWithTag(placer.referenceTag);
            EditorGUILayout.EndHorizontal();

            if (GUILayout.Button("Clear All Positive Refs"))
            {
                if (EditorUtility.DisplayDialog(
                    "Clear Positive References",
                    $"Remove tag '{placer.referenceTag.name}' from all objects?",
                    "Yes",
                    "Cancel"))
                {
                    ClearObjectsWithTag(placer.referenceTag);
                }
            }
        }

        GUILayout.Space(10);

        if (GUILayout.Button("Choose Debug Texture Folder"))
            ChooseDebugTextureFolder(placer);

        if (GUILayout.Button("Choose Generation Metrics Folder"))
            ChooseGenerationMetricsFolder(placer);
    }

    void DrawBool(string propertyName, string label)
    {
        SerializedProperty property = serializedObject.FindProperty(propertyName);
        property.boolValue = EditorGUILayout.Toggle(label, property.boolValue);
    }

    void DrawCustomMetricList()
    {
        SerializedProperty metrics = serializedObject.FindProperty("customMetricBehaviours");

        GUILayout.Space(4);
        EditorGUILayout.LabelField("Custom Metrics", EditorStyles.miniBoldLabel);

        EditorGUI.indentLevel++;
        metrics.arraySize = Mathf.Max(0, EditorGUILayout.IntField("Size", metrics.arraySize));

        for (int i = 0; i < metrics.arraySize; i++)
        {
            SerializedProperty element = metrics.GetArrayElementAtIndex(i);
            MonoBehaviour current = element.objectReferenceValue as MonoBehaviour;
            MonoBehaviour selected = (MonoBehaviour)EditorGUILayout.ObjectField(
                $"Element {i}",
                current,
                typeof(MonoBehaviour),
                true);

            if (selected != null && !(selected is IMetric))
            {
                EditorGUILayout.HelpBox($"{selected.GetType().Name} does not implement IMetric.", MessageType.Warning);
            }

            element.objectReferenceValue = selected;
        }
        EditorGUI.indentLevel--;
    }

    void SetPickMode(PickMode mode)
    {
        if (pickMode == mode) return;
        pickMode = mode;

        if (mode == PickMode.Off)
            ShowHiddenObjects();
        else
            HideTaggedObjects(mode == PickMode.Positive);
    }

    void HideTaggedObjects(bool hidePositive)
    {
        SimilarPlacement placer = target as SimilarPlacement;
        if (placer == null) return;

        ShowHiddenObjects();

        MapTag tagToHide = hidePositive ? placer.referenceTag : placer.negativeReferenceTag;
        if (tagToHide == null) return;

        foreach (TagComponent tc in FindSceneTagComponents())
        {
            if (tc != null && tc.tags.Contains(tagToHide))
            {
                SceneVisibilityManager.instance.Hide(tc.gameObject, false);
                hiddenObjects.Add(tc.gameObject);
            }
        }

        SceneView.RepaintAll();
    }

    static void ShowHiddenObjects()
    {
        foreach (GameObject obj in hiddenObjects)
        {
            if (obj != null)
                SceneVisibilityManager.instance.Show(obj, false);
        }
        hiddenObjects.Clear();
        SceneView.RepaintAll();
    }

    void HideObjectsWithTag(MapTag tag)
    {
        if (tag == null) return;

        foreach (TagComponent tc in FindSceneTagComponents())
        {
            if (tc != null && tc.tags.Contains(tag))
            {
                SceneVisibilityManager.instance.Hide(tc.gameObject, false);
                if (!hiddenObjects.Contains(tc.gameObject))
                    hiddenObjects.Add(tc.gameObject);
            }
        }

        SceneView.RepaintAll();
    }

    void ShowObjectsWithTag(MapTag tag)
    {
        if (tag == null) return;

        foreach (TagComponent tc in FindSceneTagComponents())
        {
            if (tc != null && tc.tags.Contains(tag))
            {
                SceneVisibilityManager.instance.Show(tc.gameObject, false);
                hiddenObjects.Remove(tc.gameObject);
            }
        }

        SceneView.RepaintAll();
    }

    void ClearObjectsWithTag(MapTag tag)
    {
        int count = 0;
        foreach (TagComponent tc in FindSceneTagComponents())
        {
            if (tc.tags.Contains(tag))
            {
                Undo.RecordObject(tc, "Remove Tag");
                tc.tags.Remove(tag);
                EditorUtility.SetDirty(tc);
                count++;
            }
        }
        Debug.Log($"SimilarPlacement: removed tag '{tag.name}' from {count} objects.");
    }

    void ChooseDebugTextureFolder(SimilarPlacement placer)
    {
        ChooseAssetsRelativeFolder(
            "Choose Debug Texture Folder",
            placer.debugTextureFolder,
            selectedRelative =>
            {
                Undo.RecordObject(placer, "Change Debug Texture Folder");
                placer.debugTextureFolder = selectedRelative;
                EditorUtility.SetDirty(placer);
            });
    }

    void ChooseGenerationMetricsFolder(SimilarPlacement placer)
    {
        ChooseAssetsRelativeFolder(
            "Choose Generation Metrics Folder",
            placer.generationMetricsFolder,
            selectedRelative =>
            {
                Undo.RecordObject(placer, "Change Generation Metrics Folder");
                placer.generationMetricsFolder = selectedRelative;
                EditorUtility.SetDirty(placer);
            });
    }

    void ChooseAssetsRelativeFolder(string title, string currentFolder, System.Action<string> applySelectedFolder)
    {
        string assetsPath = Path.GetFullPath(Application.dataPath).Replace('\\', '/').TrimEnd('/');
        string currentRelative = string.IsNullOrWhiteSpace(currentFolder)
            ? string.Empty
            : currentFolder.Trim().Replace('\\', '/').Trim('/');

        if (currentRelative.StartsWith("Assets/"))
            currentRelative = currentRelative.Substring("Assets/".Length).Trim('/');

        string currentFullPath = string.IsNullOrEmpty(currentRelative)
            ? assetsPath
            : Path.Combine(assetsPath, currentRelative);

        string selectedPath = EditorUtility.OpenFolderPanel(title, currentFullPath, string.Empty);
        if (string.IsNullOrEmpty(selectedPath))
            return;

        selectedPath = Path.GetFullPath(selectedPath).Replace('\\', '/').TrimEnd('/');

        if (selectedPath != assetsPath && !selectedPath.StartsWith(assetsPath + "/"))
        {
            EditorUtility.DisplayDialog(
                title,
                "Choose a folder inside this project's Assets folder.",
                "OK");
            return;
        }

        string selectedRelative = selectedPath == assetsPath
            ? string.Empty
            : selectedPath.Substring(assetsPath.Length + 1);
        applySelectedFolder(selectedRelative);
    }

    static List<TagComponent> FindSceneTagComponents()
    {
        List<TagComponent> result = new();

        for (int i = 0; i < SceneManager.sceneCount; i++)
        {
            Scene scene = SceneManager.GetSceneAt(i);
            if (!scene.isLoaded) continue;

            foreach (GameObject root in scene.GetRootGameObjects())
                result.AddRange(root.GetComponentsInChildren<TagComponent>(true));
        }

        return result;
    }

    void OnSceneGUI(SceneView sceneView)
    {
        if (pickMode == PickMode.Off) return;

        SimilarPlacement placer = target as SimilarPlacement;
        if (placer == null) return;

        MapTag activeTag = pickMode == PickMode.Positive ? placer.referenceTag : placer.negativeReferenceTag;
        if (activeTag == null) return;

        bool isNeg = pickMode == PickMode.Negative;
        string modeLabel = isNeg ? "NEGATIVE PICK" : "POSITIVE PICK";
        Color modeColor = isNeg ? Color.red : Color.green;

        Handles.BeginGUI();
        GUIStyle style = new GUIStyle(EditorStyles.boldLabel)
        {
            fontSize = 14,
            normal = { textColor = modeColor },
            alignment = TextAnchor.UpperCenter
        };
        GUI.Label(new Rect(sceneView.position.width / 2 - 150, 10, 300, 30),
            $"{modeLabel}: click to tag object", style);
        Handles.EndGUI();

        Event e = Event.current;

        if (e.type == EventType.MouseDown && e.button == 0)
        {
            GameObject picked = HandleUtility.PickGameObject(e.mousePosition, true);

            if (picked != null)
            {
                Undo.RecordObject(placer, isNeg ? "Pick Negative Reference" : "Pick Positive Reference");

                TagComponent tc = picked.GetComponent<TagComponent>();
                if (tc == null)
                    tc = Undo.AddComponent<TagComponent>(picked);

                if (!tc.tags.Contains(activeTag))
                {
                    Undo.RecordObject(tc, "Add Tag");
                    tc.tags.Add(activeTag);
                }

                if (placer.placedObjects.Contains(picked))
                    placer.placedObjects.Remove(picked);

                EditorUtility.SetDirty(placer);
                EditorUtility.SetDirty(tc);

                string kind = isNeg ? "negative" : "positive";
                Debug.Log($"SimilarPlacement: picked '{picked.name}' as {kind} reference.");

                e.Use();
                Selection.activeGameObject = placer.gameObject;
            }
        }

        if (e.type == EventType.Layout)
            HandleUtility.AddDefaultControl(GUIUtility.GetControlID(FocusType.Passive));

        sceneView.Repaint();
    }
}

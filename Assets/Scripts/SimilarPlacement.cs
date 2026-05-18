using System.Collections.Generic;
using System.IO;
using Stopwatch = System.Diagnostics.Stopwatch;
using UnityEngine;
using Unity.Sentis;
using Unity.Collections;

public interface IMetric
{
    float Evaluate(Vector3 position);
}

[ExecuteInEditMode]
public class SimilarPlacement : MonoBehaviour
{
    public enum DistanceFieldBackend
    {
        GpuJfa,
        CpuJfa
    }

    [Header("References")]
    public MapBounds bounds;
    public ComputeShader jfaShader;

    [Header("Reference Tag (objects with this tag are examples)")]
    public MapTag referenceTag;

    [Header("Negative Reference Tag (objects with this tag are bad examples)")]
    public MapTag negativeReferenceTag;

    [Header("Distance Tags (for distance fields)")]
    public List<MapTag> distanceTags = new();

    [Header("Placement Settings")]
    public GameObject prefab;
    public int countToPlace = 10;
    public float minSpacing = 2f;
    public LayerMask terrainMask = ~0;
    public float heightOffset = 0f;

    [Header("Candidate Sampling")]
    public int candidateGridResolution = 200;

    [Header("Feature Maps")]
    public int featureMapResolution = 512;
    public DistanceFieldBackend distanceFieldBackend = DistanceFieldBackend.GpuJfa;
    public bool useFastPointDistanceFields = true;
    public int fastDistanceSeedRadiusPixels = 2;
    [TextArea(2, 4)]
    public string lastFeatureMapsSummary;

    [Header("Debug")]
    public bool saveDebugTextures = true;
    public string debugTextureFolder = "DebugMaps/SimilarPlacement";

    [Header("Generation Metrics")]
    public bool collectGenerationMetrics = true;
    public bool saveGenerationMetrics = true;
    [Tooltip("Folder inside Assets used for generation metric JSON/CSV files.")]
    public string generationMetricsFolder = "Metrics/SimilarPlacement";
    [TextArea(3, 6)]
    public string lastGenerationMetricsSummary;

    [Header("Feature Settings")]
    public bool useHeightFeature = false;

    [Header("Terrain Feature Settings")]
    public bool useSlopeFeature = false;

    public bool useAspectFeature = false;

    public float flatAspectSlopeThreshold = 1f;

    public bool useCliffDistanceFeature = false;

    public float cliffHeightThreshold = 5f;

    public bool useConvexDistanceFeature = false;

    [Tooltip("Laplacian threshold to classify as convex ridge (positive = more selective)")]
    public float convexCurvatureThreshold = 0.5f;

    [Header("Custom Metrics")]
    [Tooltip("MonoBehaviour components that implement IMetric. Each valid component adds one feature.")]
    public List<MonoBehaviour> customMetricBehaviours = new();

    [Header("Result")]
    public List<GameObject> placedObjects = new();

    [Header("Promote to Reference")]
    [Tooltip("Drag placed objects here, then click 'Promote to Reference' to convert them")]
    public List<GameObject> objectsToPromote = new();

    int TEX_SIZE => Mathf.Clamp(featureMapResolution, 64, 2048);

    // Bundle of all pre-computed feature arrays
    class FeatureArrays
    {
        public float[][,] distArrays;
        public float[,] heightMap;
        public float[,] slopeMap;
        public float[,] aspectSinMap;
        public float[,] aspectCosMap;
        public float[,] cliffDistMap;
        public float[,] convexDistMap;
        public IMetric[] customMetrics;
    }

    FeatureArrays cachedFeatureArrays;
    string cachedFeatureMapsKey;

    void OnValidate()
    {
        candidateGridResolution = Mathf.Max(1, candidateGridResolution);
        featureMapResolution = Mathf.Clamp(featureMapResolution, 64, 2048);
        fastDistanceSeedRadiusPixels = Mathf.Clamp(fastDistanceSeedRadiusPixels, 0, 32);
        minSpacing = Mathf.Max(0f, minSpacing);
    }

    [System.Serializable]
    public class GenerationStageMetric
    {
        public string name;
        public double milliseconds;
    }

    [System.Serializable]
    public class FeatureRangeMetric
    {
        public string name;
        public float min;
        public float max;
    }

    [System.Serializable]
    public class GenerationMetricsReport
    {
        public string timestamp;
        public string status;
        public string algorithm;
        public string sceneName;
        public string prefabName;
        public string referenceTagName;
        public string distanceFieldBackend;
        public int distanceTagCount;
        public int featureCount;
        public int referenceObjectCount;
        public int candidateGridResolution;
        public int featureMapResolution;
        public int gridCellsTotal;
        public int terrainHitCount;
        public int featureSampleRejectedCount;
        public int rangeRejectedCount;
        public int candidateCount;
        public int orderedCandidateCount;
        public int spacingRejectedCount;
        public int placedCount;
        public float boundsSizeX;
        public float boundsSizeZ;
        public float minSpacing;
        public double totalMilliseconds;
        public List<GenerationStageMetric> stages = new();
        public List<FeatureRangeMetric> featureRanges = new();
    }

    public void Clear()
    {
        for (int i = placedObjects.Count - 1; i >= 0; i--)
        {
            if (placedObjects[i] != null)
                DestroyImmediate(placedObjects[i]);
        }
        placedObjects.Clear();
    }

    public void Generate()
    {
        Stopwatch totalTimer = Stopwatch.StartNew();
        GenerationMetricsReport metrics = CreateGenerationMetricsReport("RuleBasedMinMax");
        Stopwatch stageTimer = Stopwatch.StartNew();

        Clear();
        RecordStage(metrics, "clear_previous_objects", stageTimer);

        if (!ValidatePlacementSettings(metrics, totalTimer))
            return;

        stageTimer.Restart();
        FeatureArrays featureArrays = ComputeAllFeatureArrays(metrics);
        CacheFeatureArrays(featureArrays);
        RecordStage(metrics, "compute_feature_arrays", stageTimer);

        PlaceObjectsUsingFeatureArrays(featureArrays, metrics, totalTimer);
    }

    public void GenerateFeatureMaps()
    {
        Stopwatch totalTimer = Stopwatch.StartNew();
        GenerationMetricsReport metrics = CreateGenerationMetricsReport("FeatureMapsOnly");

        if (!ValidateFeatureMapSettings())
        {
            FinishGenerationMetrics(metrics, totalTimer, "failed_missing_feature_map_settings");
            return;
        }

        Stopwatch stageTimer = Stopwatch.StartNew();
        FeatureArrays featureArrays = ComputeAllFeatureArrays(metrics);
        CacheFeatureArrays(featureArrays);
        if (metrics != null)
            metrics.featureCount = GetFeatureCount();
        RecordStage(metrics, "compute_feature_arrays", stageTimer);

        totalTimer.Stop();
        lastFeatureMapsSummary =
            $"Generated: {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
            $"Resolution: {TEX_SIZE}x{TEX_SIZE}\n" +
            $"Features: {GetFeatureCount()}\n" +
            $"Time: {totalTimer.Elapsed.TotalMilliseconds:F2} ms";

        FinishGenerationMetrics(metrics, totalTimer, "success_feature_maps_cached");

        Debug.Log($"SimilarPlacement: feature maps generated and cached in {totalTimer.Elapsed.TotalMilliseconds:F2} ms.");

#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    public void PlaceObjectsFromGeneratedMaps()
    {
        Stopwatch totalTimer = Stopwatch.StartNew();
        GenerationMetricsReport metrics = CreateGenerationMetricsReport("RuleBasedCachedMaps");
        Stopwatch stageTimer = Stopwatch.StartNew();

        Clear();
        RecordStage(metrics, "clear_previous_objects", stageTimer);

        if (!ValidatePlacementSettings(metrics, totalTimer))
            return;

        if (!HasUsableCachedFeatureArrays())
        {
            Debug.LogWarning("SimilarPlacement: generated feature maps are missing or outdated. Click 'Generate Feature Maps' first.");
            FinishGenerationMetrics(metrics, totalTimer, "failed_missing_or_outdated_feature_maps");
            return;
        }

        PlaceObjectsUsingFeatureArrays(cachedFeatureArrays, metrics, totalTimer);
    }

    public void ClearGeneratedFeatureMaps()
    {
        cachedFeatureArrays = null;
        cachedFeatureMapsKey = null;
        lastFeatureMapsSummary = "No generated feature maps cached.";
        Debug.Log("SimilarPlacement: cleared cached feature maps.");

#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    bool ValidateFeatureMapSettings()
    {
        if (bounds == null)
        {
            Debug.LogWarning("SimilarPlacement: assign bounds and distanceTags before generating feature maps.");
            return false;
        }

        if (distanceFieldBackend == DistanceFieldBackend.GpuJfa && jfaShader == null)
        {
            Debug.LogWarning("SimilarPlacement: assign jfaShader or switch Distance Field Backend to CPU JFA.");
            return false;
        }

        return true;
    }

    bool ValidatePlacementSettings(GenerationMetricsReport metrics, Stopwatch totalTimer)
    {
        if (referenceTag == null || bounds == null || prefab == null ||
            (distanceFieldBackend == DistanceFieldBackend.GpuJfa && jfaShader == null))
        {
            Debug.LogWarning("SimilarPlacement: assign bounds, referenceTag, distanceTags, prefab and GPU jfaShader if GPU JFA is selected.");
            FinishGenerationMetrics(metrics, totalTimer, "failed_missing_settings");
            return false;
        }

        return true;
    }

    void CacheFeatureArrays(FeatureArrays featureArrays)
    {
        cachedFeatureArrays = featureArrays;
        cachedFeatureMapsKey = BuildFeatureMapsKey();

        lastFeatureMapsSummary =
            $"Generated: {System.DateTime.Now:yyyy-MM-dd HH:mm:ss}\n" +
            $"Resolution: {TEX_SIZE}x{TEX_SIZE}\n" +
            $"Features: {GetFeatureCount()}";
    }

    bool HasUsableCachedFeatureArrays()
    {
        return cachedFeatureArrays != null && cachedFeatureMapsKey == BuildFeatureMapsKey();
    }

    string BuildFeatureMapsKey()
    {
        var sb = new System.Text.StringBuilder();
        sb.Append(bounds != null ? bounds.GetInstanceID() : 0).Append('|');
        if (bounds != null)
        {
            sb.Append(bounds.transform.position).Append('|');
            sb.Append(bounds.size).Append('|');
            sb.Append(bounds.maxHeight).Append('|');
        }
        sb.Append(terrainMask.value).Append('|');
        sb.Append(TEX_SIZE).Append('|');
        sb.Append(distanceFieldBackend).Append('|');
        sb.Append(useHeightFeature).Append('|');
        sb.Append(useSlopeFeature).Append('|');
        sb.Append(useAspectFeature).Append('|');
        sb.Append(flatAspectSlopeThreshold).Append('|');
        sb.Append(useCliffDistanceFeature).Append('|');
        sb.Append(cliffHeightThreshold).Append('|');
        sb.Append(useConvexDistanceFeature).Append('|');
        sb.Append(convexCurvatureThreshold).Append('|');
        sb.Append(useFastPointDistanceFields).Append('|');
        sb.Append(fastDistanceSeedRadiusPixels).Append('|');

        for (int i = 0; i < distanceTags.Count; i++)
            sb.Append(distanceTags[i] != null ? distanceTags[i].GetInstanceID() : 0).Append(',');

        sb.Append('|');
        for (int i = 0; i < customMetricBehaviours.Count; i++)
            sb.Append(customMetricBehaviours[i] != null ? customMetricBehaviours[i].GetInstanceID() : 0).Append(',');

        return sb.ToString();
    }

    void PlaceObjectsUsingFeatureArrays(FeatureArrays featureArrays, GenerationMetricsReport metrics, Stopwatch totalTimer)
    {
        Stopwatch stageTimer = Stopwatch.StartNew();
        List<GameObject> referenceObjects = FindObjectsWithTag(referenceTag);
        if (metrics != null)
            metrics.referenceObjectCount = referenceObjects.Count;
        RecordStage(metrics, "find_reference_objects", stageTimer);

        if (referenceObjects.Count == 0)
        {
            Debug.LogWarning($"SimilarPlacement: no objects found with tag '{referenceTag.name}'.");
            FinishGenerationMetrics(metrics, totalTimer, "failed_no_references");
            return;
        }

        Debug.Log($"SimilarPlacement: found {referenceObjects.Count} reference objects with tag '{referenceTag.name}'.");

        Vector3 boundsOrigin = GetBoundsOrigin();

        stageTimer.Restart();
        int featureCount = GetFeatureCount();
        float[] minPerFeature = new float[featureCount];
        float[] maxPerFeature = new float[featureCount];
        if (metrics != null)
            metrics.featureCount = featureCount;

        for (int t = 0; t < featureCount; t++)
        {
            minPerFeature[t] = float.MaxValue;
            maxPerFeature[t] = float.MinValue;
        }

        foreach (GameObject refObj in referenceObjects)
        {
            Vector2Int texCoord = WorldToTexCoord(refObj.transform.position, boundsOrigin);
            if (texCoord.x < 0 || texCoord.x >= TEX_SIZE || texCoord.y < 0 || texCoord.y >= TEX_SIZE)
            {
                Debug.LogWarning($"SimilarPlacement: {refObj.name} is outside bounds, skipping.");
                continue;
            }

            float[] features = SampleFeaturesAtWorld(refObj.transform.position.x, refObj.transform.position.z,
                boundsOrigin, featureArrays, featureCount, refObj.transform.position.y);
            if (features == null) continue;

            for (int t = 0; t < featureCount; t++)
            {
                if (features[t] < minPerFeature[t]) minPerFeature[t] = features[t];
                if (features[t] > maxPerFeature[t]) maxPerFeature[t] = features[t];
            }
        }

        string[] featureNames = GetFeatureNames();
        for (int t = 0; t < featureCount; t++)
        {
            Debug.Log($"Feature '{featureNames[t]}': range [{minPerFeature[t]:F4}, {maxPerFeature[t]:F4}]");
            if (metrics != null)
            {
                metrics.featureRanges.Add(new FeatureRangeMetric
                {
                    name = featureNames[t],
                    min = minPerFeature[t],
                    max = maxPerFeature[t]
                });
            }
        }
        RecordStage(metrics, "build_reference_feature_ranges", stageTimer);

        stageTimer.Restart();
        List<Vector3> candidates = new();
        float stepX = bounds.size.x / candidateGridResolution;
        float stepZ = bounds.size.y / candidateGridResolution;
        int terrainHitCount = 0;
        int featureSampleRejectedCount = 0;
        int rangeRejectedCount = 0;

        for (int gx = 0; gx < candidateGridResolution; gx++)
        {
            for (int gz = 0; gz < candidateGridResolution; gz++)
            {
                float worldX = boundsOrigin.x + (gx + 0.5f) * stepX;
                float worldZ = boundsOrigin.z + (gz + 0.5f) * stepZ;

                float[] features = SampleFeaturesAtWorld(worldX, worldZ, boundsOrigin, featureArrays, featureCount);
                if (features == null)
                {
                    featureSampleRejectedCount++;
                    continue;
                }

                bool valid = true;
                for (int t = 0; t < featureCount; t++)
                {
                    if (features[t] < minPerFeature[t] || features[t] > maxPerFeature[t])
                    {
                        valid = false;
                        break;
                    }
                }

                if (!valid)
                {
                    rangeRejectedCount++;
                    continue;
                }

                Vector2Int texCoord = WorldToTexCoord(new Vector3(worldX, 0, worldZ), boundsOrigin);
                float h = featureArrays.heightMap != null
                ? featureArrays.heightMap[texCoord.x, texCoord.y]
                : RaycastHeight(worldX, worldZ);

                candidates.Add(new Vector3(worldX, h + heightOffset, worldZ));
            }
        }

        if (metrics != null)
        {
            metrics.gridCellsTotal = candidateGridResolution * candidateGridResolution;
            metrics.terrainHitCount = terrainHitCount;
            metrics.featureSampleRejectedCount = featureSampleRejectedCount;
            metrics.rangeRejectedCount = rangeRejectedCount;
            metrics.candidateCount = candidates.Count;
        }
        RecordStage(metrics, "scan_candidate_grid", stageTimer);

        Debug.Log($"SimilarPlacement: {candidates.Count} valid candidate positions found.");

        if (candidates.Count == 0)
        {
            Debug.LogWarning("SimilarPlacement: no valid positions found.");
            FinishGenerationMetrics(metrics, totalTimer, "failed_no_candidates");
            return;
        }

        stageTimer.Restart();
        List<int> indices = new List<int>(candidates.Count);
        for (int i = 0; i < candidates.Count; i++) indices.Add(i);
        ShuffleList(indices);
        if (metrics != null)
            metrics.orderedCandidateCount = indices.Count;
        RecordStage(metrics, "shuffle_candidates", stageTimer);

        float[] scores = null;
        PlaceFromCandidates(candidates, indices, scores, referenceObjects, metrics);
        FinishGenerationMetrics(metrics, totalTimer, "success");
    }

    bool NeedsTerrainFeatures() =>
        useHeightFeature || useSlopeFeature || useAspectFeature || useCliffDistanceFeature || useConvexDistanceFeature;

    string GetDebugTextureDirectory()
    {
        string relativeFolder = string.IsNullOrWhiteSpace(debugTextureFolder)
            ? string.Empty
            : debugTextureFolder.Trim().Replace('\\', '/').Trim('/');

        if (relativeFolder.StartsWith("Assets/"))
            relativeFolder = relativeFolder.Substring("Assets/".Length).Trim('/');

        string fullPath = string.IsNullOrEmpty(relativeFolder)
            ? Application.dataPath
            : Path.GetFullPath(Path.Combine(Application.dataPath, relativeFolder));

        string assetsPath = Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedFullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        bool isInsideAssets = string.Equals(normalizedFullPath, assetsPath, System.StringComparison.OrdinalIgnoreCase) ||
            normalizedFullPath.StartsWith(assetsPath + Path.DirectorySeparatorChar, System.StringComparison.OrdinalIgnoreCase) ||
            normalizedFullPath.StartsWith(assetsPath + Path.AltDirectorySeparatorChar, System.StringComparison.OrdinalIgnoreCase);

        if (!isInsideAssets)
        {
            Debug.LogWarning("SimilarPlacement: debugTextureFolder must be relative to Assets. Saving debug maps to Assets instead.");
            fullPath = Application.dataPath;
        }

        if (!Directory.Exists(fullPath))
            Directory.CreateDirectory(fullPath);

        return fullPath;
    }

    string GetDebugTexturePath(string fileName)
    {
        return Path.Combine(GetDebugTextureDirectory(), fileName);
    }

    void SaveDebugMap(float[,] map, string name)
    {
        if (!saveDebugTextures) return;

        // Find min/max for normalization (skip sentinel values)
        float min = float.MaxValue, max = float.MinValue;
        for (int x = 0; x < TEX_SIZE; x++)
        {
            for (int y = 0; y < TEX_SIZE; y++)
            {
                float v = map[x, y];
                if (v <= float.MinValue) continue;
                if (v < min) min = v;
                if (v > max) max = v;
            }
        }

        float range = max - min;
        if (range < 1e-6f) range = 1f;

        Texture2D tex = new Texture2D(TEX_SIZE, TEX_SIZE, TextureFormat.RGBA32, false);
        for (int x = 0; x < TEX_SIZE; x++)
        {
            for (int y = 0; y < TEX_SIZE; y++)
            {
                float v = map[x, y];
                if (v <= float.MinValue)
                {
                    tex.SetPixel(x, y, Color.magenta);
                    continue;
                }
                float t = (v - min) / range;
                tex.SetPixel(x, y, new Color(t, t, t, 1f));
            }
        }
        tex.Apply();

        byte[] png = tex.EncodeToPNG();
        string fullPath = GetDebugTexturePath($"debug_terrain_{name}.png");
        File.WriteAllBytes(fullPath, png);
        Debug.Log($"[SimilarPlacement] Saved debug: {fullPath}");
        DestroyImmediate(tex);
    }

    void SaveDebugAspectMap(float[,] sinMap, float[,] cosMap, float[,] slopeMap)
    {
        if (!saveDebugTextures) return;

        Texture2D tex = new Texture2D(TEX_SIZE, TEX_SIZE, TextureFormat.RGBA32, false);
        for (int x = 0; x < TEX_SIZE; x++)
        {
            for (int y = 0; y < TEX_SIZE; y++)
            {
                float s = sinMap[x, y];
                float c = cosMap[x, y];
                float slope = slopeMap[x, y];
                if (slope < flatAspectSlopeThreshold)
                {
                    tex.SetPixel(x, y, Color.gray);
                    continue;
                }

                if (s == 0f && c == 0f)
                {
                    tex.SetPixel(x, y, Color.black);
                    continue;
                }
                // Angle 0..1 for hue
                float angle = Mathf.Atan2(s, c); // -PI..PI
                float hue = (angle + Mathf.PI) / (2f * Mathf.PI); // 0..1
                tex.SetPixel(x, y, Color.HSVToRGB(hue, 1f, 1f));
            }
        }
        tex.Apply();

        byte[] png = tex.EncodeToPNG();
        string fullPath = GetDebugTexturePath("debug_terrain_aspect.png");
        File.WriteAllBytes(fullPath, png);
        Debug.Log($"[SimilarPlacement] Saved debug: {fullPath}");
        DestroyImmediate(tex);
    }


    FeatureArrays ComputeAllFeatureArrays(GenerationMetricsReport metrics = null)
    {
        Stopwatch stageTimer = Stopwatch.StartNew();
        FeatureArrays fa = new FeatureArrays();
        fa.customMetrics = GetCustomMetrics();
        fa.distArrays = ComputeDistanceArrays(metrics);
        RecordStage(metrics, "feature_distance_arrays", stageTimer);
        Debug.Log("ABOBA");
        if (NeedsTerrainFeatures())
        {
            Debug.Log("ABOBA2");
            stageTimer.Restart();
            float[,] heightMap = BuildHeightMap();
            fa.heightMap = heightMap;
            SaveDebugMap(heightMap, "heightmap");
            RecordStage(metrics, "feature_heightmap", stageTimer);

            float cellSizeX = bounds.size.x / TEX_SIZE;
            float cellSizeZ = bounds.size.y / TEX_SIZE;

            if (useSlopeFeature || useAspectFeature)
            {
                stageTimer.Restart();
                ComputeSlopeAndAspect(heightMap, cellSizeX, cellSizeZ,
                    out float[,] slopeMap, out float[,] aspectSinMap, out float[,] aspectCosMap);
                fa.slopeMap = slopeMap;
                fa.aspectSinMap = aspectSinMap;
                fa.aspectCosMap = aspectCosMap;

                SaveDebugMap(slopeMap, "slope");
                SaveDebugMap(aspectSinMap, "aspect_sin");
                SaveDebugMap(aspectCosMap, "aspect_cos");
                SaveDebugAspectMap(aspectSinMap, aspectCosMap, slopeMap);
                RecordStage(metrics, "feature_slope_aspect", stageTimer);
            }

            if (useCliffDistanceFeature)
            {
                stageTimer.Restart();
                float[,] heightDiffMap = ComputeMaxHeightDiff(heightMap);
                SaveDebugMap(heightDiffMap, "height_diff");
                fa.cliffDistMap = ComputeThresholdDistanceField(heightDiffMap, cliffHeightThreshold, "cliff");
                SaveDebugMap(fa.cliffDistMap, "cliff_distance");
                RecordStage(metrics, "feature_cliff_distance", stageTimer);
            }

            if (useConvexDistanceFeature)
            {
                stageTimer.Restart();
                float[,] curvatureMap = ComputeCurvature(heightMap, cellSizeX, cellSizeZ);
                SaveDebugMap(curvatureMap, "curvature");
                fa.convexDistMap = ComputeThresholdDistanceField(curvatureMap, convexCurvatureThreshold, "convex");
                SaveDebugMap(fa.convexDistMap, "convex_distance");
                RecordStage(metrics, "feature_convex_distance", stageTimer);
            }
        }

        return fa;
    }

    float[][,] ComputeDistanceArrays(GenerationMetricsReport metrics = null)
    {
        string debugDirectory = saveDebugTextures ? GetDebugTextureDirectory() : null;
        float[][,] distArrays = new float[distanceTags.Count][,];

        for (int i = 0; i < distanceTags.Count; i++)
        {
            string tagName = distanceTags[i] != null ? distanceTags[i].name : "unknown";

            Stopwatch overlayTimer = Stopwatch.StartNew();
            Texture2D overlay = useFastPointDistanceFields
                ? MapoverlayGenerator.GenerateFromTaggedObjectPositions(bounds, distanceTags[i], TEX_SIZE, saveDebugTextures, debugDirectory, fastDistanceSeedRadiusPixels)
                : MapoverlayGenerator.Generate(bounds, distanceTags[i], TEX_SIZE, saveDebugTextures, debugDirectory);
            RecordStage(metrics, $"feature_overlay_{tagName}", overlayTimer);

            if (distanceFieldBackend == DistanceFieldBackend.CpuJfa)
            {
                Stopwatch cpuTimer = Stopwatch.StartNew();
                distArrays[i] = JumpFlooding.ComputeDistanceFieldCpu(overlay, saveDebugTextures, $"jf_cpu_{tagName}", debugDirectory);
                RecordStage(metrics, $"feature_cpu_jfa_{tagName}", cpuTimer);
            }
            else
            {
                Stopwatch gpuTimer = Stopwatch.StartNew();
                RenderTexture distanceField = JumpFlooding.ComputeDistanceField(
                    jfaShader, overlay, saveDebugTextures, 
                    $"jf_gpu_{tagName}", debugDirectory);
                RecordStage(metrics, $"feature_jfa_gpu_{tagName}", gpuTimer);
                float[,] result = JumpFlooding.ReadDistanceField(distanceField);
                distanceField.Release();
                distArrays[i] = result;
            }

            Object.DestroyImmediate(overlay);
        }

        return distArrays;
    }

    float[,] BuildHeightMap()
    {
        float[,] heightMap = new float[TEX_SIZE, TEX_SIZE];
        Vector3 boundsOrigin = GetBoundsOrigin();
        float cellSizeX = bounds.size.x / TEX_SIZE;
        float cellSizeZ = bounds.size.y / TEX_SIZE;
        float rayY    = bounds.transform.position.y + bounds.maxHeight + 1f;
        float rayDist = bounds.maxHeight * 2f;

        int total = TEX_SIZE * TEX_SIZE;
        var commands = new NativeArray<RaycastCommand>(total, Allocator.TempJob);
        var results  = new NativeArray<RaycastHit>   (total, Allocator.TempJob);


        QueryParameters qp = new QueryParameters(terrainMask);
        for (int x = 0; x < TEX_SIZE; x++)
        for (int y = 0; y < TEX_SIZE; y++)
        {
            float worldX = boundsOrigin.x + (x + 0.5f) * cellSizeX;
            float worldZ = boundsOrigin.z + (y + 0.5f) * cellSizeZ;
            commands[x * TEX_SIZE + y] = new RaycastCommand(
                new Vector3(worldX, rayY, worldZ),
                Vector3.down, qp, rayDist
            );
        }

        RaycastCommand.ScheduleBatch(commands, results, 64).Complete();

        for (int x = 0; x < TEX_SIZE; x++)
        for (int y = 0; y < TEX_SIZE; y++)
        {
            var hit = results[x * TEX_SIZE + y];
            heightMap[x, y] = hit.collider != null ? hit.point.y : float.MinValue;
        }

        commands.Dispose();
        results.Dispose();

        Debug.Log("SimilarPlacement: heightmap built.");
        return heightMap;
    }

    void ComputeSlopeAndAspect(float[,] heightMap, float cellSizeX, float cellSizeZ,
        out float[,] slopeMap, out float[,] aspectSinMap, out float[,] aspectCosMap)
    {
        slopeMap = new float[TEX_SIZE, TEX_SIZE];
        aspectSinMap = new float[TEX_SIZE, TEX_SIZE];
        aspectCosMap = new float[TEX_SIZE, TEX_SIZE];

        for (int x = 0; x < TEX_SIZE; x++)
        {
            for (int y = 0; y < TEX_SIZE; y++)
            {
                if (heightMap[x, y] <= float.MinValue)
                {
                    slopeMap[x, y] = 0f;
                    aspectSinMap[x, y] = 0f;
                    aspectCosMap[x, y] = 0f;
                    continue;
                }

                // X gradient
                int x0 = Mathf.Max(x - 1, 0);
                int x1 = Mathf.Min(x + 1, TEX_SIZE - 1);
                float hx0 = heightMap[x0, y];
                float hx1 = heightMap[x1, y];
                if (hx0 <= float.MinValue) hx0 = heightMap[x, y];
                if (hx1 <= float.MinValue) hx1 = heightMap[x, y];
                float dx = (hx1 - hx0) / ((x1 - x0) * cellSizeX);

                // Z gradient
                int y0 = Mathf.Max(y - 1, 0);
                int y1 = Mathf.Min(y + 1, TEX_SIZE - 1);
                float hy0 = heightMap[x, y0];
                float hy1 = heightMap[x, y1];
                if (hy0 <= float.MinValue) hy0 = heightMap[x, y];
                if (hy1 <= float.MinValue) hy1 = heightMap[x, y];
                float dz = (hy1 - hy0) / ((y1 - y0) * cellSizeZ);

                float gradMag = Mathf.Sqrt(dx * dx + dz * dz);
                float slope = Mathf.Atan(gradMag) * Mathf.Rad2Deg;
                slopeMap[x, y] = slope;

                if (slope < flatAspectSlopeThreshold)
                {
                    aspectSinMap[x, y] = 0f;
                    aspectCosMap[x, y] = 0f;
                    continue;
                }

                float aspect = Mathf.Atan2(-dz, -dx);
                aspectSinMap[x, y] = Mathf.Sin(aspect);
                aspectCosMap[x, y] = Mathf.Cos(aspect);
            }
        }

        LogMapStats(slopeMap, "slope");
        LogMapStats(aspectSinMap, "aspect_sin");
        LogMapStats(aspectCosMap, "aspect_cos");
        Debug.Log("SimilarPlacement: slope and aspect maps computed.");
    }

    void LogMapStats(float[,] map, string name)
    {
        float min = float.MaxValue, max = float.MinValue;
        double sum = 0;
        int count = 0;
        for (int x = 0; x < TEX_SIZE; x++)
        {
            for (int y = 0; y < TEX_SIZE; y++)
            {
                float v = map[x, y];
                if (v <= float.MinValue) continue;
                if (v < min) min = v;
                if (v > max) max = v;
                sum += v;
                count++;
            }
        }
        float mean = count > 0 ? (float)(sum / count) : 0f;
        Debug.Log($"[SimilarPlacement] {name}: min={min:F6} max={max:F6} mean={mean:F6} valid={count}/{TEX_SIZE * TEX_SIZE}");
    }


    float[,] ComputeCurvature(float[,] heightMap, float cellSizeX, float cellSizeZ)
    {
        float[,] curvature = new float[TEX_SIZE, TEX_SIZE];

        for (int x = 1; x < TEX_SIZE - 1; x++)
        {
            for (int y = 1; y < TEX_SIZE - 1; y++)
            {
                float hc = heightMap[x, y];
                if (hc <= float.MinValue) { curvature[x, y] = 0f; continue; }

                float hxm = heightMap[x - 1, y];
                float hxp = heightMap[x + 1, y];
                float hym = heightMap[x, y - 1];
                float hyp = heightMap[x, y + 1];

                if (hxm <= float.MinValue || hxp <= float.MinValue ||
                    hym <= float.MinValue || hyp <= float.MinValue)
                {
                    curvature[x, y] = 0f;
                    continue;
                }

                float d2x = (hxp - 2f * hc + hxm) / (cellSizeX * cellSizeX);
                float d2z = (hyp - 2f * hc + hym) / (cellSizeZ * cellSizeZ);
                curvature[x, y] = -(d2x + d2z);
            }
        }

        Debug.Log("SimilarPlacement: curvature map computed.");
        return curvature;
    }

    float[,] ComputeMaxHeightDiff(float[,] heightMap)
    {
        float[,] diffMap = new float[TEX_SIZE, TEX_SIZE];

        for (int x = 0; x < TEX_SIZE; x++)
        {
            for (int y = 0; y < TEX_SIZE; y++)
            {
                float hc = heightMap[x, y];
                if (hc <= float.MinValue) { diffMap[x, y] = 0f; continue; }

                float maxDiff = 0f;
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        int nx = x + dx;
                        int ny = y + dy;
                        if (nx < 0 || nx >= TEX_SIZE || ny < 0 || ny >= TEX_SIZE) continue;
                        float hn = heightMap[nx, ny];
                        if (hn <= float.MinValue) continue;
                        float diff = Mathf.Abs(hc - hn);
                        if (diff > maxDiff) maxDiff = diff;
                    }
                }
                diffMap[x, y] = maxDiff;
            }
        }

        LogMapStats(diffMap, "height_diff");
        Debug.Log("SimilarPlacement: height diff map computed.");
        return diffMap;
    }

    float[,] ComputeThresholdDistanceField(float[,] valueMap, float threshold, string debugName)
    {
        Texture2D binaryTex = new Texture2D(TEX_SIZE, TEX_SIZE, TextureFormat.RGBA32, false);
        Color clear = Color.clear;
        Color white = Color.white;
        int seedCount = 0;

        for (int x = 0; x < TEX_SIZE; x++)
        {
            for (int y = 0; y < TEX_SIZE; y++)
            {
                bool isSeed = valueMap[x, y] >= threshold;
                binaryTex.SetPixel(x, y, isSeed ? white : clear);
                if (isSeed) seedCount++;
            }
        }
        binaryTex.Apply();

        Debug.Log($"SimilarPlacement: {debugName} threshold={threshold} seeds={seedCount}/{TEX_SIZE * TEX_SIZE}");

        string debugDirectory = saveDebugTextures ? GetDebugTextureDirectory() : null;
        float[,] distMap;
        if (distanceFieldBackend == DistanceFieldBackend.CpuJfa)
        {
            distMap = JumpFlooding.ComputeDistanceFieldCpu(binaryTex, saveDebugTextures, $"jf_cpu_{debugName}", debugDirectory);
        }
        else
        {
            RenderTexture distRT = JumpFlooding.ComputeDistanceField(jfaShader, binaryTex, saveDebugTextures, $"jf_gpu_{debugName}", debugDirectory);
            distMap = JumpFlooding.ReadDistanceField(distRT);
            distRT.Release();
        }
        DestroyImmediate(binaryTex);

        Debug.Log($"SimilarPlacement: {debugName} distance field computed.");
        return distMap;
    }

    Vector3 GetBoundsOrigin()
    {
        return bounds.transform.position - new Vector3(bounds.size.x / 2f, 0, bounds.size.y / 2f);
    }


    int GetFeatureCount()
    {
        int count = distanceTags.Count;
        if (useHeightFeature) count += 1;
        if (useSlopeFeature) count += 1;
        if (useAspectFeature) count += 2; // sin + cos
        if (useCliffDistanceFeature) count += 1;
        if (useConvexDistanceFeature) count += 1;
        count += GetCustomMetrics().Length;
        return count;
    }

    string[] GetFeatureNames()
    {
        List<string> names = new();
        for (int i = 0; i < distanceTags.Count; i++)
        {
            string n = distanceTags[i] != null ? distanceTags[i].name : "unknown";
            names.Add(n);
        }
        if (useHeightFeature) names.Add("height");
        if (useSlopeFeature) names.Add("slope");
        if (useAspectFeature) { names.Add("aspect_sin"); names.Add("aspect_cos"); }
        if (useCliffDistanceFeature) names.Add("cliff_distance");
        if (useConvexDistanceFeature) names.Add("convex_distance");
        foreach (IMetric metric in GetCustomMetrics())
            names.Add($"custom_{GetMetricName(metric)}");
        return names.ToArray();
    }

    float[] SampleFeaturesAtWorld(float worldX, float worldZ, Vector3 boundsOrigin, FeatureArrays fa, int featureCount, float worldY = float.NaN)
    {
        Vector2Int texCoord = WorldToTexCoord(new Vector3(worldX, 0, worldZ), boundsOrigin);
        if (texCoord.x < 0 || texCoord.x >= TEX_SIZE || texCoord.y < 0 || texCoord.y >= TEX_SIZE)
            return null;

        float[] sample = new float[featureCount];
        int idx = 0;

        // Distance tag features
        for (int t = 0; t < distanceTags.Count; t++)
            sample[idx++] = fa.distArrays[t][texCoord.x, texCoord.y];

        // Height feature (requires raycast)
        if (useHeightFeature)
        {
            float h = fa.heightMap != null
                ? fa.heightMap[texCoord.x, texCoord.y]
                : RaycastHeight(worldX, worldZ);
            if (h <= float.MinValue) return null;
            sample[idx++] = h;
        }

        // Slope feature
        if (useSlopeFeature)
            sample[idx++] = fa.slopeMap[texCoord.x, texCoord.y];

        // Aspect feature (sin/cos pair)
        if (useAspectFeature)
        {
            sample[idx++] = fa.aspectSinMap[texCoord.x, texCoord.y];
            sample[idx++] = fa.aspectCosMap[texCoord.x, texCoord.y];
        }

        // Cliff distance feature
        if (useCliffDistanceFeature)
            sample[idx++] = fa.cliffDistMap[texCoord.x, texCoord.y];

        // Convex ridge distance feature
        if (useConvexDistanceFeature)
            sample[idx++] = fa.convexDistMap[texCoord.x, texCoord.y];

        if (fa.customMetrics != null)
        {
            if (float.IsNaN(worldY))
            {
                worldY = RaycastHeight(worldX, worldZ);
                if (worldY <= float.MinValue)
                    worldY = 0f;
            }

            Vector3 position = new Vector3(worldX, worldY, worldZ);
            for (int i = 0; i < fa.customMetrics.Length; i++)
            {
                IMetric metric = fa.customMetrics[i];
                if (metric == null) return null;

                float value = metric.Evaluate(position);
                if (float.IsNaN(value) || float.IsInfinity(value)) return null;
                sample[idx++] = value;
            }
        }

        return sample;
    }

    IMetric[] GetCustomMetrics()
    {
        List<IMetric> metrics = new();
        for (int i = 0; i < customMetricBehaviours.Count; i++)
        {
            MonoBehaviour behaviour = customMetricBehaviours[i];
            if (behaviour == null)
                continue;

            if (behaviour is IMetric metric)
                metrics.Add(metric);
        }

        return metrics.ToArray();
    }

    string GetMetricName(IMetric metric)
    {
        if (metric is Object obj && obj != null)
            return obj.name;

        return metric.GetType().Name;
    }

    float RaycastHeight(float worldX, float worldZ)
    {
        Vector3 rayOrigin = new Vector3(worldX, bounds.transform.position.y + bounds.maxHeight + 1f, worldZ);
        if (Physics.Raycast(rayOrigin, Vector3.down, out RaycastHit hit, bounds.maxHeight * 2f, terrainMask))
            return hit.point.y;
        return float.MinValue;
    }

    void PlaceFromCandidates(List<Vector3> candidates, List<int> orderedIndices, float[] scores, List<GameObject> referenceObjects, GenerationMetricsReport metrics = null)
    {
        Stopwatch stageTimer = Stopwatch.StartNew();
        List<Vector3> chosen = new();
        float sqrSpacing = minSpacing * minSpacing;
        int spacingRejectedCount = 0;

        foreach (int idx in orderedIndices)
        {
            if (chosen.Count >= countToPlace) break;

            Vector3 pos = candidates[idx];
            bool tooClose = false;

            foreach (Vector3 c in chosen)
            {
                if ((c - pos).sqrMagnitude < sqrSpacing) { tooClose = true; break; }
            }

            if (!tooClose)
            {
                foreach (GameObject refObj in referenceObjects)
                {
                    if ((refObj.transform.position - pos).sqrMagnitude < sqrSpacing)
                    { tooClose = true; break; }
                }
            }

            if (tooClose)
            {
                spacingRejectedCount++;
                continue;
            }

            chosen.Add(pos);
        }

        // Instantiate placed objects
        foreach (Vector3 pos in chosen)
        {
#if UNITY_EDITOR
            GameObject obj = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab);
            UnityEditor.Undo.RegisterCreatedObjectUndo(obj, "SimilarPlacement Place Object");
#else
            GameObject obj = Instantiate(prefab);
#endif
            obj.transform.position = pos;
            obj.transform.rotation = Quaternion.Euler(0, UnityEngine.Random.Range(0f, 360f), 0);
            placedObjects.Add(obj);
        }

        if (metrics != null)
        {
            metrics.spacingRejectedCount = spacingRejectedCount;
            metrics.placedCount = chosen.Count;
        }
        RecordStage(metrics, "poisson_spacing_and_instantiate", stageTimer);

        Debug.Log($"SimilarPlacement: placed {chosen.Count} objects.");
    }

    GenerationMetricsReport CreateGenerationMetricsReport(string algorithm)
    {
        if (!collectGenerationMetrics)
            return null;

        return new GenerationMetricsReport
        {
            timestamp = System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"),
            status = "running",
            algorithm = algorithm,
            sceneName = gameObject.scene.name,
            prefabName = prefab != null ? prefab.name : string.Empty,
            referenceTagName = referenceTag != null ? referenceTag.name : string.Empty,
            distanceFieldBackend = this.distanceFieldBackend.ToString(),
            distanceTagCount = distanceTags != null ? distanceTags.Count : 0,
            candidateGridResolution = candidateGridResolution,
            featureMapResolution = TEX_SIZE,
            gridCellsTotal = candidateGridResolution * candidateGridResolution,
            boundsSizeX = bounds != null ? bounds.size.x : 0f,
            boundsSizeZ = bounds != null ? bounds.size.y : 0f,
            minSpacing = minSpacing
        };
    }

    void RecordStage(GenerationMetricsReport metrics, string stageName, Stopwatch timer)
    {
        if (metrics == null)
            return;

        timer.Stop();
        metrics.stages.Add(new GenerationStageMetric
        {
            name = stageName,
            milliseconds = timer.Elapsed.TotalMilliseconds
        });
    }

    void FinishGenerationMetrics(GenerationMetricsReport metrics, Stopwatch totalTimer, string status)
    {
        if (metrics == null)
            return;

        totalTimer.Stop();
        metrics.status = status;
        metrics.totalMilliseconds = totalTimer.Elapsed.TotalMilliseconds;

        lastGenerationMetricsSummary =
            $"Status: {metrics.status}\n" +
            $"Total: {metrics.totalMilliseconds:F2} ms\n" +
            $"Candidates: {metrics.candidateCount}/{metrics.gridCellsTotal}\n" +
            $"Placed: {metrics.placedCount}/{countToPlace}";

        Debug.Log(
            $"[SimilarPlacement Metrics] status={metrics.status}, total={metrics.totalMilliseconds:F2} ms, " +
            $"grid={metrics.gridCellsTotal}, terrain_hits={metrics.terrainHitCount}, " +
            $"candidates={metrics.candidateCount}, placed={metrics.placedCount}");

        if (saveGenerationMetrics)
            SaveGenerationMetrics(metrics);

#if UNITY_EDITOR
        UnityEditor.EditorUtility.SetDirty(this);
#endif
    }

    void SaveGenerationMetrics(GenerationMetricsReport metrics)
    {
        string directory = GetGenerationMetricsDirectory();
        string stamp = System.DateTime.Now.ToString("yyyyMMdd_HHmmss_fff");
        string baseName = MakeSafeFileName($"{algorithmOrDefault(metrics.algorithm)}_{referenceTagNameOrDefault(metrics.referenceTagName)}_{stamp}");
        string jsonPath = Path.Combine(directory, baseName + ".json");
        string csvPath = GetGenerationMetricsCsvPath(directory);

        File.WriteAllText(jsonPath, JsonUtility.ToJson(metrics, true));
        AppendGenerationMetricsCsv(csvPath, metrics, jsonPath);

        Debug.Log($"[SimilarPlacement Metrics] Saved: {jsonPath}");

#if UNITY_EDITOR
        UnityEditor.AssetDatabase.Refresh();
#endif
    }

    string GetGenerationMetricsDirectory()
    {
        string relativeFolder = string.IsNullOrWhiteSpace(generationMetricsFolder)
            ? string.Empty
            : generationMetricsFolder.Trim().Replace('\\', '/').Trim('/');

        if (relativeFolder.StartsWith("Assets/"))
            relativeFolder = relativeFolder.Substring("Assets/".Length).Trim('/');

        string fullPath = string.IsNullOrEmpty(relativeFolder)
            ? Application.dataPath
            : Path.GetFullPath(Path.Combine(Application.dataPath, relativeFolder));

        string assetsPath = Path.GetFullPath(Application.dataPath).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        string normalizedFullPath = fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        bool isInsideAssets = string.Equals(normalizedFullPath, assetsPath, System.StringComparison.OrdinalIgnoreCase) ||
            normalizedFullPath.StartsWith(assetsPath + Path.DirectorySeparatorChar, System.StringComparison.OrdinalIgnoreCase) ||
            normalizedFullPath.StartsWith(assetsPath + Path.AltDirectorySeparatorChar, System.StringComparison.OrdinalIgnoreCase);

        if (!isInsideAssets)
        {
            Debug.LogWarning("SimilarPlacement: generationMetricsFolder must be relative to Assets. Saving metrics to Assets instead.");
            fullPath = Application.dataPath;
        }

        if (!Directory.Exists(fullPath))
            Directory.CreateDirectory(fullPath);

        return fullPath;
    }

    void AppendGenerationMetricsCsv(string csvPath, GenerationMetricsReport metrics, string jsonPath)
    {
        bool writeHeader = !File.Exists(csvPath);
        using (StreamWriter writer = new StreamWriter(csvPath, true))
        {
            if (writeHeader)
            {
                writer.WriteLine("timestamp,status,algorithm,scene,prefab,reference_tag,distance_field_backend,grid_resolution,feature_map_resolution,grid_cells,terrain_hits,candidates,placed,reference_count,feature_count,distance_tag_count,min_spacing,bounds_x,bounds_z,feature_sample_rejected,range_rejected,spacing_rejected,total_ms,json_path");
            }

            writer.WriteLine(string.Join(",",
                Csv(metrics.timestamp),
                Csv(metrics.status),
                Csv(metrics.algorithm),
                Csv(metrics.sceneName),
                Csv(metrics.prefabName),
                Csv(metrics.referenceTagName),
                Csv(metrics.distanceFieldBackend),
                metrics.candidateGridResolution,
                metrics.featureMapResolution,
                metrics.gridCellsTotal,
                metrics.terrainHitCount,
                metrics.candidateCount,
                metrics.placedCount,
                metrics.referenceObjectCount,
                metrics.featureCount,
                metrics.distanceTagCount,
                FloatCsv(metrics.minSpacing),
                FloatCsv(metrics.boundsSizeX),
                FloatCsv(metrics.boundsSizeZ),
                metrics.featureSampleRejectedCount,
                metrics.rangeRejectedCount,
                metrics.spacingRejectedCount,
                DoubleCsv(metrics.totalMilliseconds),
                Csv(jsonPath)));
        }
    }

    string GetGenerationMetricsCsvPath(string directory)
    {
        string csvPath = Path.Combine(directory, "generation_metrics_summary.csv");
        if (!File.Exists(csvPath))
            return csvPath;

        using (StreamReader reader = new StreamReader(csvPath))
        {
            string header = reader.ReadLine();
            if (header != null && header.Contains("feature_map_resolution") && header.Contains("distance_field_backend"))
                return csvPath;
        }

        return Path.Combine(directory, "generation_metrics_summary_v3.csv");
    }

    string Csv(string value)
    {
        if (value == null)
            value = string.Empty;
        return "\"" + value.Replace("\"", "\"\"") + "\"";
    }

    string FloatCsv(float value)
    {
        return value.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);
    }

    string DoubleCsv(double value)
    {
        return value.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);
    }

    string MakeSafeFileName(string value)
    {
        foreach (char invalid in Path.GetInvalidFileNameChars())
            value = value.Replace(invalid, '_');
        return value;
    }

    string algorithmOrDefault(string value) => string.IsNullOrWhiteSpace(value) ? "generation" : value;
    string referenceTagNameOrDefault(string value) => string.IsNullOrWhiteSpace(value) ? "untagged" : value;

    List<GameObject> FindObjectsWithTag(MapTag tag)
    {
        List<GameObject> result = new();
        TagComponent[] allTagged = FindObjectsByType<TagComponent>(FindObjectsSortMode.None);

        foreach (TagComponent tc in allTagged)
        {
            if (tc.tags.Contains(tag))
                result.Add(tc.gameObject);
        }

        return result;
    }

    Vector2Int WorldToTexCoord(Vector3 worldPos, Vector3 boundsOrigin)
    {
        float nx = (worldPos.x - boundsOrigin.x) / bounds.size.x;
        float nz = (worldPos.z - boundsOrigin.z) / bounds.size.y;
        int tx = Mathf.Clamp(Mathf.FloorToInt(nx * TEX_SIZE), 0, TEX_SIZE - 1);
        int tz = Mathf.Clamp(Mathf.FloorToInt(nz * TEX_SIZE), 0, TEX_SIZE - 1);
        return new Vector2Int(tx, tz);
    }

    void ShuffleList<T>(List<T> list)
    {
        for (int i = list.Count - 1; i > 0; i--)
        {
            int j = UnityEngine.Random.Range(0, i + 1);
            (list[i], list[j]) = (list[j], list[i]);
        }
    }
}

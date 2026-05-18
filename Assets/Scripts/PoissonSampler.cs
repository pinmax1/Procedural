using System.Collections.Generic;
using UnityEngine;

[ExecuteInEditMode]
public class PoissonSpawner : MonoBehaviour
{
    public GameObject[] prefabs;
    public Vector2 areaSize;
    public float radius;
    public int pointsPerIteration;

    public float heightOffset;
    public LayerMask terrainMask;

    public List<GameObject> spawnedObjects = new List<GameObject>();

    public void Clear()
    {
        for (int i = spawnedObjects.Count - 1; i >= 0; i--)
        {
            if (spawnedObjects[i] != null)
                DestroyImmediate(spawnedObjects[i]);
        }
        spawnedObjects.Clear();
    }

    public void Generate()
    {
        Clear();
        List<Vector2> points = GeneratePoints(radius, areaSize, pointsPerIteration);

        foreach (Vector2 point in points)
        {
            Vector3 pos = new Vector3(transform.position.x + point.x,
                                      transform.position.y,
                                      transform.position.z + point.y);

            if (Physics.Raycast(new Vector3(pos.x, 500, pos.z), Vector3.down, out RaycastHit hit, 2000, terrainMask))
            {
                pos.y = hit.point.y + heightOffset;
            }

            GameObject prefab = prefabs[Random.Range(0, prefabs.Length)];
            GameObject obj = (GameObject)UnityEditor.PrefabUtility.InstantiatePrefab(prefab);
            obj.transform.position = pos;
            obj.transform.rotation = Quaternion.Euler(0, Random.Range(0, 360), 0);
            spawnedObjects.Add(obj);
        }
    }

    public static List<Vector2> GeneratePoints(float radius, Vector2 sampleRegionSize, int numSamplesBeforeRejection = 30)
    {
        float cellSize = radius / Mathf.Sqrt(2);

        int[,] grid = new int[Mathf.CeilToInt(sampleRegionSize.x / cellSize),Mathf.CeilToInt(sampleRegionSize.y / cellSize)];

        List<Vector2> points = new List<Vector2>();
        List<Vector2> spawnPoints = new List<Vector2>();

        spawnPoints.Add(sampleRegionSize / 2);

        while (spawnPoints.Count > 0)
        {
            int spawnIndex = Random.Range(0, spawnPoints.Count);
            Vector2 spawnCenter = spawnPoints[spawnIndex];
            bool accepted = false;

            for (int i = 0; i < numSamplesBeforeRejection; i++)
            {
                float angle = Random.value * Mathf.PI * 2;
                Vector2 dir = new Vector2(Mathf.Cos(angle), Mathf.Sin(angle));
                float dist = Random.Range(radius, 2 * radius);
                Vector2 candidate = spawnCenter + dir * dist;

                if (IsValid(candidate, sampleRegionSize, cellSize, radius, points, grid))
                {
                    points.Add(candidate);
                    spawnPoints.Add(candidate);
                    grid[(int)(candidate.x / cellSize), (int)(candidate.y / cellSize)] = points.Count;
                    accepted = true;
                    break;
                }
            }

            if (!accepted)
                spawnPoints.RemoveAt(spawnIndex);
        }

        return points;
    }

    static bool IsValid(Vector2 candidate, Vector2 regionSize, float cellSize, float radius, List<Vector2> points, int[,] grid)
    {
        if (candidate.x >= 0 && candidate.x < regionSize.x &&
            candidate.y >= 0 && candidate.y < regionSize.y)
        {
            int cellX = (int)(candidate.x / cellSize);
            int cellY = (int)(candidate.y / cellSize);

            int searchStartX = Mathf.Max(0, cellX - 2);
            int searchEndX = Mathf.Min(grid.GetLength(0) - 1, cellX + 2);
            int searchStartY = Mathf.Max(0, cellY - 2);
            int searchEndY = Mathf.Min(grid.GetLength(1) - 1, cellY + 2);

            for (int x = searchStartX; x <= searchEndX; x++)
            {
                for (int y = searchStartY; y <= searchEndY; y++)
                {
                    int pointIndex = grid[x, y] - 1;
                    if (pointIndex != -1)
                    {
                        float sqrDist = (candidate - points[pointIndex]).sqrMagnitude;
                        if (sqrDist < radius * radius)
                            return false;
                    }
                }
            }
            return true;
        }
        return false;
    }
}
using UnityEngine;

[ExecuteInEditMode]
public class HeightMapWithPerlin : MonoBehaviour
{
    public float scale1 = 50f;
    public float scale2 = 1f;
    public float height = 50f;

    void Start()
    {
        Generate();
    }

    public void Generate()
    {
        Terrain terrain = GetComponent<Terrain>();
        TerrainData data = terrain.terrainData;

        int width = data.heightmapResolution;
        int heightmap = data.heightmapResolution;

        float[,] heights = new float[width, heightmap];

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < heightmap; y++)
            {
                float xCoord1 = (float)x / width * scale1;
                float yCoord1 = (float)y / heightmap * scale1;

                float perlin1 = Mathf.PerlinNoise(xCoord1, yCoord1);

                heights[x,y] = perlin1 * (height/data.size.y);
            }
        }

        data.SetHeights(0, 0, heights);
    }
}
using System.IO;
using UnityEngine;
using Unity.Collections;
public static class MapoverlayGenerator
{
    public static Texture2D GenerateFromTaggedObjectPositions(
        MapBounds bounds,
        MapTag tag,
        int resolution = 1024,
        bool saveDebug = false,
        string debugDirectory = null,
        int seedRadiusPixels = 1)
    {
        Texture2D tex = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false);
        Color32[] pixels = new Color32[resolution * resolution];

        if (bounds != null && tag != null)
        {
            Vector3 origin = bounds.transform.position - new Vector3(bounds.size.x / 2f, 0f, bounds.size.y / 2f);
            TagComponent[] allTagged = Object.FindObjectsByType<TagComponent>(FindObjectsSortMode.None);
            Color seedColor = tag.color;
            if (seedColor.a <= 0.5f)
                seedColor.a = 1f;
            Color32 seedColor32 = seedColor;

            foreach (TagComponent tc in allTagged)
            {
                if (tc == null || !tc.tags.Contains(tag))
                    continue;

                Vector3 pos = tc.transform.position;
                float nx = (pos.x - origin.x) / bounds.size.x;
                float nz = (pos.z - origin.z) / bounds.size.y;
                if (nx < 0f || nx > 1f || nz < 0f || nz > 1f)
                    continue;

                int px = Mathf.Clamp(Mathf.FloorToInt(nx * resolution), 0, resolution - 1);
                int pz = Mathf.Clamp(Mathf.FloorToInt(nz * resolution), 0, resolution - 1);

                for (int dx = -seedRadiusPixels; dx <= seedRadiusPixels; dx++)
                {
                    for (int dz = -seedRadiusPixels; dz <= seedRadiusPixels; dz++)
                    {
                        if (dx * dx + dz * dz > seedRadiusPixels * seedRadiusPixels)
                            continue;

                        int x = px + dx;
                        int z = pz + dz;
                        if (x < 0 || x >= resolution || z < 0 || z >= resolution)
                            continue;

                        pixels[z * resolution + x] = seedColor32;
                    }
                }
            }
        }

        tex.SetPixels32(pixels);
        tex.Apply();

        SaveDebugOverlay(tex, tag, saveDebug, debugDirectory);
        return tex;
    }

    public static Texture2D Generate(MapBounds bounds, MapTag tag, int resolution = 1024, bool saveDebug = false, string debugDirectory = null)
    {
        float stepX = bounds.size.x / resolution;
        float stepZ = bounds.size.y / resolution;
        float topY = bounds.transform.position.y + bounds.maxHeight + 1f;
        Vector3 origin = bounds.transform.position - new Vector3(bounds.size.x / 2f, 0, bounds.size.y / 2f);

        int total = resolution * resolution;

        var commands = new NativeArray<RaycastCommand>(total, Allocator.TempJob);
        var results  = new NativeArray<RaycastHit>(total, Allocator.TempJob);

        QueryParameters qp = new QueryParameters(~0);
        for (int x = 0; x < resolution; x++)
        for (int z = 0; z < resolution; z++)
        {
            Vector3 rayOrigin = origin + new Vector3(x * stepX, topY, z * stepZ);
            commands[z * resolution + x] = new RaycastCommand(rayOrigin, Vector3.down, qp, bounds.maxHeight * 2f);
        }

        RaycastCommand.ScheduleBatch(commands, results, 64).Complete();

        Color[] pixels = new Color[total];

        for (int x = 0; x < resolution; x++)
        for (int z = 0; z < resolution; z++)
        {
            var hit = results[z * resolution + x];

            if (hit.collider != null)
            {
                TagComponent tc = hit.collider.gameObject.GetComponent<TagComponent>();
                pixels[z * resolution + x] = (tc != null && tc.tags.Contains(tag))
                    ? tag.color
                    : Color.clear;
            }
            else
            {
                pixels[x * resolution + z] = Color.clear;
            }
        }

        commands.Dispose();
        results.Dispose();

        Texture2D tex = new Texture2D(resolution, resolution, TextureFormat.RGBA32, false);
        tex.SetPixels(pixels);
        tex.Apply();

        SaveDebugOverlay(tex, tag, saveDebug, debugDirectory);
        return tex;
    }

    static void SaveDebugOverlay(Texture2D tex, MapTag tag, bool saveDebug, string debugDirectory)
    {
        if (!saveDebug)
            return;

        string safeName = tag != null ? tag.name : "unknown";
        byte[] png = tex.EncodeToPNG();
        string directory = string.IsNullOrEmpty(debugDirectory) ? Application.dataPath : debugDirectory;
        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        string path = Path.Combine(directory, $"debug_overlay_{safeName}.png");
        File.WriteAllBytes(path, png);
        Debug.Log($"[MapoverlayGenerator] Saved debug: {path}");
    }
}

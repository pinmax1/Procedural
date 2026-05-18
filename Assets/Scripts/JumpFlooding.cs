using System.IO;
using UnityEngine;
using Stopwatch = System.Diagnostics.Stopwatch;

public static class JumpFlooding
{
    public struct Result
    {
        public RenderTexture distanceField;
        public RenderTexture seedMap;
    }
    private static void ForceSyncTiny(RenderTexture rt)
    {
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;
        Texture2D tiny = new Texture2D(1, 1, TextureFormat.RGBAFloat, false);
        tiny.ReadPixels(new Rect(0, 0, 1, 1), 0, 0);
        Object.DestroyImmediate(tiny);
        RenderTexture.active = prev;
    }
    public static Result Compute(ComputeShader jfaShader, Texture2D input, bool saveDebug = false, string debugPrefix = "jf", string debugDirectory = null)
    {
        int width = input.width;
        int height = input.height;
        int dispatchX = Mathf.CeilToInt(width / 16f);
        int dispatchY = Mathf.CeilToInt(height / 16f);

        RenderTexture rtA = CreateRT(width, height);
        RenderTexture rtB = CreateRT(width, height);
        RenderTexture rtDist = CreateRT(width, height);

        // Init
        int initKernel = jfaShader.FindKernel("Init");
        jfaShader.SetTexture(initKernel, "Input", input);
        jfaShader.SetTexture(initKernel, "Dst", rtA);
        jfaShader.SetInt("Width", width);
        jfaShader.SetInt("Height", height);
        jfaShader.Dispatch(initKernel, dispatchX, dispatchY, 1);


        // Jump Flood
        int jumpKernel = jfaShader.FindKernel("Jump");
        int step = Mathf.NextPowerOfTwo(Mathf.Max(width, height)) / 2;
        bool ping = true;
        int jumpCount = 0;
        while (step > 0)
        {
            RenderTexture src = ping ? rtA : rtB;
            RenderTexture dst = ping ? rtB : rtA;

            jfaShader.SetTexture(jumpKernel, "Src", src);
            jfaShader.SetTexture(jumpKernel, "Dst", dst);
            jfaShader.SetInt("Step", step);
            jfaShader.SetInt("Width", width);
            jfaShader.SetInt("Height", height);
            jfaShader.Dispatch(jumpKernel, dispatchX, dispatchY, 1);

            ping = !ping;
            step /= 2;
            ++jumpCount;
        }


        RenderTexture finalSeeds = ping ? rtA : rtB;

        // Distance
        int distKernel = jfaShader.FindKernel("Distance");
        jfaShader.SetTexture(distKernel, "Src", finalSeeds);
        jfaShader.SetTexture(distKernel, "Dst", rtDist);
        jfaShader.SetInt("Width", width);
        jfaShader.SetInt("Height", height);
        jfaShader.SetFloat("MaxDistance", Mathf.Max(width, height));
        jfaShader.Dispatch(distKernel, dispatchX, dispatchY, 1);
        ForceSyncTiny(rtDist);
        RenderTexture rtVis = CreateRT(width, height);
        int visKernel = jfaShader.FindKernel("VisualizeCoords");
        jfaShader.SetTexture(visKernel, "Src", finalSeeds);
        jfaShader.SetTexture(visKernel, "Dst", rtVis);
        jfaShader.SetInt("Width", width);
        jfaShader.SetInt("Height", height);
        jfaShader.Dispatch(visKernel, dispatchX, dispatchY, 1);

        if (saveDebug)
        {
            SaveRT(rtDist, $"{debugPrefix}_distance.png", debugDirectory);
            SaveRT(rtVis, $"{debugPrefix}_seeds.png", debugDirectory);
        }

        RenderTexture seedResult = rtVis;

        if (ping)
            rtB.Release();
        else
            rtA.Release();

        finalSeeds.Release();

        return new Result
        {
            distanceField = rtDist,
            seedMap = seedResult
        };
    }

    public static RenderTexture ComputeDistanceField(ComputeShader jfaShader, Texture2D input, bool saveDebug = false, string debugPrefix = "jf", string debugDirectory = null)
    {
        Result result = Compute(jfaShader, input, saveDebug, debugPrefix, debugDirectory);
        result.seedMap.Release();
        return result.distanceField;
    }

    public static float[,] ComputeDistanceFieldCpu(Texture2D input, bool saveDebug = false, string debugPrefix = "jf_cpu", string debugDirectory = null)
    {
        int width = input.width;
        int height = input.height;
        int length = width * height;
        int[] srcX = new int[length];
        int[] srcY = new int[length];
        int[] dstX = new int[length];
        int[] dstY = new int[length];

        Color32[] pixels = input.GetPixels32();
        for (int i = 0; i < length; i++)
        {
            if (pixels[i].a > 127)
            {
                srcX[i] = i % width;
                srcY[i] = i / width;
            }
            else
            {
                srcX[i] = -1;
                srcY[i] = -1;
            }
        }

        int step = Mathf.NextPowerOfTwo(Mathf.Max(width, height)) / 2;
        while (step > 0)
        {
            for (int y = 0; y < height; y++)
            {
                int row = y * width;
                for (int x = 0; x < width; x++)
                {
                    int index = row + x;
                    int bestX = srcX[index];
                    int bestY = srcY[index];
                    int bestDist = bestX >= 0 ? SqrDistance(x, y, bestX, bestY) : int.MaxValue;

                    for (int oy = -1; oy <= 1; oy++)
                    {
                        int sy = y + oy * step;
                        if (sy < 0 || sy >= height) continue;

                        for (int ox = -1; ox <= 1; ox++)
                        {
                            if (ox == 0 && oy == 0) continue;

                            int sx = x + ox * step;
                            if (sx < 0 || sx >= width) continue;

                            int sampleIndex = sy * width + sx;
                            int seedX = srcX[sampleIndex];
                            if (seedX < 0) continue;

                            int seedY = srcY[sampleIndex];
                            int dist = SqrDistance(x, y, seedX, seedY);
                            if (dist < bestDist)
                            {
                                bestDist = dist;
                                bestX = seedX;
                                bestY = seedY;
                            }
                        }
                    }

                    dstX[index] = bestX;
                    dstY[index] = bestY;
                }
            }

            (srcX, dstX) = (dstX, srcX);
            (srcY, dstY) = (dstY, srcY);
            step /= 2;
        }

        float maxDistance = Mathf.Max(width, height);
        float[,] result = new float[width, height];
        Color32[] distancePixels = saveDebug ? new Color32[length] : null;
        Color32[] seedPixels = saveDebug ? new Color32[length] : null;

        for (int y = 0; y < height; y++)
        {
            int row = y * width;
            for (int x = 0; x < width; x++)
            {
                int index = row + x;
                int seedX = srcX[index];
                int seedY = srcY[index];

                if (seedX < 0)
                {
                    result[x, y] = 0f;
                    if (saveDebug)
                    {
                        distancePixels[index] = new Color32(0, 0, 0, 255);
                        seedPixels[index] = new Color32(0, 0, 0, 255);
                    }
                    continue;
                }

                float d = Mathf.Clamp01(Mathf.Sqrt(SqrDistance(x, y, seedX, seedY)) / maxDistance);
                result[x, y] = d;

                if (saveDebug)
                {
                    byte v = (byte)Mathf.RoundToInt(d * 255f);
                    distancePixels[index] = new Color32(v, v, v, 255);
                    seedPixels[index] = new Color32(
                        (byte)Mathf.RoundToInt((seedX / (float)width) * 255f),
                        (byte)Mathf.RoundToInt((seedY / (float)height) * 255f),
                        0,
                        255);
                }
            }
        }

        if (saveDebug)
        {
            SavePixels(distancePixels, width, height, $"{debugPrefix}_distance.png", debugDirectory);
            SavePixels(seedPixels, width, height, $"{debugPrefix}_seeds.png", debugDirectory);
        }

        return result;
    }

    static int SqrDistance(int x0, int y0, int x1, int y1)
    {
        int dx = x1 - x0;
        int dy = y1 - y0;
        return dx * dx + dy * dy;
    }

    public static float[,] ReadDistanceField(RenderTexture rt)
    {
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;

        int width = rt.width;
        int height = rt.height;
        Texture2D tex = new Texture2D(width, height, TextureFormat.RGBAFloat, false);
        tex.ReadPixels(new Rect(0, 0, width, height), 0, 0);
        tex.Apply();

        RenderTexture.active = prev;

        float[,] result = new float[width, height];
        Color[] pixels = tex.GetPixels();

        for (int x = 0; x < width; x++)
        {
            for (int y = 0; y < height; y++)
            {
                result[x, y] = pixels[y * width + x].r;
            }
        }

        Object.DestroyImmediate(tex);
        return result;
    }

    public static RenderTexture CreateRT(int width, int height)
    {
        RenderTexture rt = new RenderTexture(width, height, 0, RenderTextureFormat.ARGBFloat);
        rt.enableRandomWrite = true;
        rt.filterMode = FilterMode.Point;
        rt.Create();
        return rt;
    }

    static void SaveRT(RenderTexture rt, string fileName, string debugDirectory)
    {
        RenderTexture prev = RenderTexture.active;
        RenderTexture.active = rt;

        Texture2D tex = new Texture2D(rt.width, rt.height, TextureFormat.RGBAFloat, false);
        tex.ReadPixels(new Rect(0, 0, rt.width, rt.height), 0, 0);
        tex.Apply();

        RenderTexture.active = prev;

        byte[] png = tex.EncodeToPNG();
        string directory = string.IsNullOrEmpty(debugDirectory) ? Application.dataPath : debugDirectory;
        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        string fullPath = Path.Combine(directory, fileName);
        File.WriteAllBytes(fullPath, png);
        Debug.Log($"[JumpFlooding] Saved debug: {fullPath}");

        Object.DestroyImmediate(tex);
    }

    static void SavePixels(Color32[] pixels, int width, int height, string fileName, string debugDirectory)
    {
        Texture2D tex = new Texture2D(width, height, TextureFormat.RGBA32, false);
        tex.SetPixels32(pixels);
        tex.Apply();

        byte[] png = tex.EncodeToPNG();
        string directory = string.IsNullOrEmpty(debugDirectory) ? Application.dataPath : debugDirectory;
        if (!Directory.Exists(directory))
            Directory.CreateDirectory(directory);

        string fullPath = Path.Combine(directory, fileName);
        File.WriteAllBytes(fullPath, png);
        Debug.Log($"[JumpFlooding CPU] Saved debug: {fullPath}");

        Object.DestroyImmediate(tex);
    }
}

using UnityEngine;
using UnityEngine.UI;

public class ComputeGradient : MonoBehaviour
{
    [Header("Assign in Inspector")] public ComputeShader compute;
    public                                 RawImage      targetUI; // Optional: aufs Canvas ziehen
    public                                 int           textureSize = 512;

    RenderTexture rt;
    int           kernel;
    const int     THREADS = 8;

    void OnEnable()
    {
        // RenderTexture vorbereiten (wichtig: enableRandomWrite!)
        rt                   = new RenderTexture(textureSize, textureSize, 0, RenderTextureFormat.ARGB32);
        rt.enableRandomWrite = true;
        rt.filterMode        = FilterMode.Point;
        rt.wrapMode          = TextureWrapMode.Clamp;
        rt.Create();

        kernel = compute.FindKernel("CSMain");
        compute.SetTexture(kernel, "Result", rt);

        if (targetUI) targetUI.texture = rt; // in UI anzeigen
    }

    void Update()
    {
        int gx = Mathf.CeilToInt(rt.width  / (float)THREADS);
        int gy = Mathf.CeilToInt(rt.height / (float)THREADS);
        compute.Dispatch(kernel, gx, gy, 1);
    }

    void OnDestroy()
    {
        if (rt != null) rt.Release();
    }
}
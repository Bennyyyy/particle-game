using UnityEngine;

public class ParticleLife2D : MonoBehaviour
{
    [Header("Counts")] public int count = 100_000;

    [Header("Forces (live-tunable)")] [Range(0, 5f)]
    public float attract = 1.5f;

    [Range(0, 5f)]       public float repel          = 2.0f;
    [Range(0.05f, 2f)]   public float interactRadius = 0.6f;
    [Range(0.90f, 1.0f)] public float damping        = 0.99f;

    [Header("2D World (XY)")] public Vector2 worldMin = new Vector2(-10f, -6f);
    public                           Vector2 worldMax = new Vector2(10f, 6f);

    [Header("Rendering")] public float    particleSize = 0.02f;
    public                       Material material; // Shader "Unlit/Particle2D"
    public                       Mesh     quadMesh; // optional; Built-in Quad wenn null

    [Header("Grid (Nachbarschaft)")]
    // Faustregel: cellSize ≈ 0.6–0.9 * interactRadius (r ≈ 1–2)
    public float cellSize = 0.6f;

    [Header("Compute")] public ComputeShader compute; // Datei "ParticleLife2D.compute"

    // --- intern ---
    int           kClear,    kClearNext, kHash,    kForces,   kIntegrate;
    ComputeBuffer posBuffer, velBuffer,  cellHead, nextIndex, argsBuffer;
    Bounds        bigBounds;

    struct Int2
    {
        public int x, y;

        public Int2(int X, int Y)
        {
            x = X;
            y = Y;
        }
    }

    Int2 GridRes2D()
    {
        Vector2 size = worldMax - worldMin;
        return new Int2(
            Mathf.Max(1, Mathf.FloorToInt(size.x / Mathf.Max(0.0001f, cellSize))),
            Mathf.Max(1, Mathf.FloorToInt(size.y / Mathf.Max(0.0001f, cellSize)))
        );
    }

    void OnEnable()
    {
        if (material == null)
        {
            Debug.LogError("Bitte Material mit Shader 'Unlit/Particle2D' zuweisen.");
            enabled = false;
            return;
        }

        if (compute == null)
        {
            Debug.LogError("Bitte Compute Shader 'ParticleLife2D.compute' zuweisen.");
            enabled = false;
            return;
        }

        if (quadMesh == null) quadMesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");

        material.enableInstancing = true;

        // Kernel IDs
        kClear     = compute.FindKernel("clear_grid");
        kClearNext = compute.FindKernel("clear_next");
        kHash      = compute.FindKernel("hash_particles");
        kForces    = compute.FindKernel("compute_forces");
        kIntegrate = compute.FindKernel("integrate");
        if (kClear < 0 || kClearNext < 0 || kHash < 0 || kForces < 0 || kIntegrate < 0)
        {
            Debug.LogError("Compute-Kernel nicht gefunden.");
            enabled = false;
            return;
        }

        // Buffers
        posBuffer = new ComputeBuffer(count, sizeof(float) * 4); // xy pos, z=0, w=size
        velBuffer = new ComputeBuffer(count, sizeof(float) * 4); // xy vel
        nextIndex = new ComputeBuffer(count, sizeof(int));

        var res       = GridRes2D();
        int cellCount = res.x * res.y;
        cellHead = new ComputeBuffer(cellCount, sizeof(int));

        // Partikel init
        var rnd = new System.Random(123);
        var pos = new Vector4[count];
        var vel = new Vector4[count];
        for (int i = 0; i < count; i++)
        {
            float rx = Mathf.Lerp(worldMin.x, worldMax.x, (float)rnd.NextDouble());
            float ry = Mathf.Lerp(worldMin.y, worldMax.y, (float)rnd.NextDouble());
            pos[i] = new Vector4(rx, ry, 0f, particleSize);
            vel[i] = Vector4.zero;
        }

        posBuffer.SetData(pos);
        velBuffer.SetData(vel);

        // Indirect Draw Args
        argsBuffer = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
        uint[] args = { quadMesh.GetIndexCount(0), (uint)count, 0, 0, 0 };
        argsBuffer.SetData(args);

        // Große Bounds (Z groß genug)
        var sizeBounds = new Vector3(worldMax.x - worldMin.x, worldMax.y - worldMin.y, 20f) + Vector3.one * 10f;
        bigBounds = new Bounds(Vector3.zero, sizeBounds);

        // Statische Uniforms
        compute.SetInts("_GridRes2D", res.x, res.y);
        compute.SetFloats("_WorldMin", worldMin.x, worldMin.y);
        compute.SetFloats("_WorldMax", worldMax.x, worldMax.y);
        compute.SetFloats("_CellSize2D", cellSize, cellSize);
        compute.SetInt("_ParticleCount", count);
        compute.SetInt("_CellCount", cellCount);

        // Buffer Bindings
        compute.SetBuffer(kClear, "_CellHead", cellHead);
        compute.SetBuffer(kClearNext, "_Next", nextIndex);

        compute.SetBuffer(kHash, "_Pos", posBuffer);
        compute.SetBuffer(kHash, "_CellHead", cellHead);
        compute.SetBuffer(kHash, "_Next", nextIndex);

        compute.SetBuffer(kForces, "_Pos", posBuffer);
        compute.SetBuffer(kForces, "_Vel", velBuffer);
        compute.SetBuffer(kForces, "_CellHead", cellHead);
        compute.SetBuffer(kForces, "_Next", nextIndex);

        compute.SetBuffer(kIntegrate, "_Pos", posBuffer);
        compute.SetBuffer(kIntegrate, "_Vel", velBuffer);

        // Material → Pos-Buffer
        material.SetBuffer("_Pos", posBuffer);

        Debug.Log($"ParticleLife2D: Grid {res.x}x{res.y}={cellCount} cells, Particles={count}");
    }

    void Update()
    {
        // Live-Parameter → Compute
        compute.SetFloat("_Attract", attract);
        compute.SetFloat("_Repel", repel);
        compute.SetFloat("_InteractRadius", interactRadius);
        compute.SetFloat("_Damping", damping);
        compute.SetFloat("_DeltaTime", Time.deltaTime);

        // Dispatch (TG = 256 wie gewünscht)
        const int TG          = 256;
        var       res         = GridRes2D();
        int       cellCount   = res.x * res.y;
        int       tgParticles = Mathf.CeilToInt(count     / (float)TG);
        int       tgCells     = Mathf.CeilToInt(cellCount / (float)TG);

        compute.Dispatch(kClear, tgCells, 1, 1);
        compute.Dispatch(kClearNext, tgParticles, 1, 1);
        compute.Dispatch(kHash, tgParticles, 1, 1);
        compute.Dispatch(kForces, tgParticles, 1, 1);
        compute.Dispatch(kIntegrate, tgParticles, 1, 1);

        // Render — pro Frame neu binden (URP/Metal)
        material.SetBuffer("_Pos", posBuffer);
        material.SetFloat("_Size", particleSize);
        Graphics.DrawMeshInstancedIndirect(quadMesh, 0, material, bigBounds, argsBuffer);
    }

    void OnDisable()
    {
        posBuffer?.Release();
        posBuffer = null;
        velBuffer?.Release();
        velBuffer = null;
        cellHead?.Release();
        cellHead = null;
        nextIndex?.Release();
        nextIndex = null;
        argsBuffer?.Release();
        argsBuffer = null;
    }
}
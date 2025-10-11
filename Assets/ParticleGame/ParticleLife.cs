using UnityEngine;

public class ParticleLife2D : MonoBehaviour
{
    [Header("Counts")] public int count = 100_000;

    [Header("Forces (live-tunable)")] [Range(-1, 1f)]
    public float attract = 0.5f;

    [Range(0.01f, 0.1f)] public float minDistance = 0.05f;

    [Range(0.05f, 2f)]   public float interactRadius = 0.6f;
    [Range(0.90f, 1.0f)] public float damping        = 0.99f;

    [Header("Species")] public int typeCount = 3; // K

    [Tooltip("Flattened row-major KxK (i*K + j)")]
    public float[] attractMat; // Länge K*K

    public Color[] typeColors; // Länge K

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
    int           kClearGrid, kClearNext, kAddParticlesToGrid, kForces,   kIntegrate;
    ComputeBuffer posBuffer,  velBuffer,  cellHead,            nextIndex, argsBuffer;
    Bounds        bigBounds;

    ComputeBuffer typeBuffer;      // uint per particle
    ComputeBuffer attractBuffer;   // float K*K
    ComputeBuffer typeColorBuffer; // float4 K

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

    void Start()
    {
        Application.targetFrameRate = 60; // z. B. 60 FPS
        QualitySettings.vSyncCount  = 0;  // VSync ausschalten, sonst hat es Vorrang!
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

        if (typeCount <= 0)
        {
            typeCount = 1;
        }

        if (attractMat == null || attractMat.Length != typeCount * typeCount)
            attractMat = new float[typeCount * typeCount];
        if (typeColors == null || typeColors.Length != typeCount)
        {
            typeColors = new Color[typeCount];
            for (int k = 0; k < typeCount; k++) typeColors[k] = Color.HSVToRGB(k / (float)typeCount, 0.7f, 1f);
        }

        typeBuffer = new ComputeBuffer(count, sizeof(uint));
        uint[] types = new uint[count];
        
        for (int i = 0; i < count; i++)
            types[i] = (uint)(i % typeCount);
        
        typeBuffer.SetData(types);


        attractBuffer   = new ComputeBuffer(typeCount                * typeCount, sizeof(float));
        typeColorBuffer = new ComputeBuffer(typeCount, sizeof(float) * 4);

        attractBuffer.SetData(attractMat);

        var cols = new Vector4[typeCount];
        for (int k = 0; k < typeCount; k++)
        {
            var c = typeColors[k];
            cols[k] = new Vector4(c.r, c.g, c.b, c.a);
        }

        typeColorBuffer.SetData(cols);


        // Kernel IDs
        kClearGrid          = compute.FindKernel("clear_grid");
        kClearNext          = compute.FindKernel("clear_next");
        kAddParticlesToGrid = compute.FindKernel("add_particles_to_grid");
        kForces             = compute.FindKernel("compute_forces");
        kIntegrate          = compute.FindKernel("integrate");
        if (kClearGrid < 0 || kClearNext < 0 || kAddParticlesToGrid < 0 || kForces < 0 || kIntegrate < 0)
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
        var cellCount = res.x * res.y;
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
        compute.SetInt("_TypeCount", typeCount);

        // Buffer Bindings
        compute.SetBuffer(kClearGrid, "_CellHead", cellHead);
        compute.SetBuffer(kClearNext, "_Next", nextIndex);

        compute.SetBuffer(kAddParticlesToGrid, "_Pos", posBuffer);
        compute.SetBuffer(kAddParticlesToGrid, "_CellHead", cellHead);
        compute.SetBuffer(kAddParticlesToGrid, "_Next", nextIndex);
        compute.SetBuffer(kAddParticlesToGrid, "_Type", typeBuffer);

        compute.SetBuffer(kForces, "_Pos", posBuffer);
        compute.SetBuffer(kForces, "_Vel", velBuffer);
        compute.SetBuffer(kForces, "_CellHead", cellHead);
        compute.SetBuffer(kForces, "_Next", nextIndex);
        compute.SetBuffer(kForces, "_Type", typeBuffer);
        compute.SetBuffer(kForces, "_AttractMat", attractBuffer);

        compute.SetBuffer(kIntegrate, "_Pos", posBuffer);
        compute.SetBuffer(kIntegrate, "_Vel", velBuffer);

        // Material → Pos-Buffer
        material.SetBuffer("_Pos", posBuffer);
        material.SetBuffer("_Type", typeBuffer);
        material.SetBuffer("_TypeColor", typeColorBuffer);

        Debug.Log($"ParticleLife2D: Grid {res.x}x{res.y}={cellCount} cells, Particles={count}");
    }

    void Update()
    {
        // Live-Parameter → Compute
        compute.SetFloat("_Attract", attract);
        compute.SetFloat("_InteractRadius", interactRadius);
        compute.SetFloat("_Damping", damping);
        compute.SetFloat("_DeltaTime", Time.deltaTime);
        compute.SetFloat("_MinDistance", minDistance);

        attractBuffer.SetData(attractMat);

        // Dispatch (TG = 256 wie gewünscht)
        const int TG          = 256;
        var       res         = GridRes2D();
        int       cellCount   = res.x * res.y;
        int       tgParticles = Mathf.CeilToInt(count     / (float)TG);
        int       tgCells     = Mathf.CeilToInt(cellCount / (float)TG);

        compute.Dispatch(kClearGrid, tgCells, 1, 1);
        compute.Dispatch(kClearNext, tgParticles, 1, 1);
        compute.Dispatch(kAddParticlesToGrid, tgParticles, 1, 1);
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

        typeBuffer?.Release();
        typeBuffer = null;
        attractBuffer?.Release();
        attractBuffer = null;
        typeColorBuffer?.Release();
        typeColorBuffer = null;
    }
}
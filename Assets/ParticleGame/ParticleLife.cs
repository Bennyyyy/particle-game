using UnityEngine;

public class ParticleRender2D : MonoBehaviour
{
    [Header("Count & World")]
    public int count = 100_000;
    public Vector2 worldMin = new Vector2(-10, -6);
    public Vector2 worldMax = new Vector2( 10,  6);

    [Header("Rendering")]
    public float particleSize = 0.025f;
    public Material material;        // Shader: "Unlit/Particle2D"
    public Mesh quadMesh;            // optional; Built-in Quad wenn null

    [Header("Optional GPU-Drift (kein Physik)")]
    public bool gpuDrift = false;    // nur fürs Bewegtbild
    public float speed = 1.0f;       // Basisgeschwindigkeit für Drift

    [Header("Compute (nur für Drift)")]
    public ComputeShader compute;    // Datei "ParticleRender2D.compute"

    ComputeBuffer posBuffer;         // float4: xy pos, z=0, w=size
    ComputeBuffer velBuffer;         // float4: xy vel, z,w=0
    ComputeBuffer argsBuffer;        // indirect args
    int kMove = -1;

    Bounds bigBounds;

    void OnEnable()
    {
        if (material == null)
        {
            Debug.LogError("Bitte ein Material mit Shader 'Unlit/Particle2D' zuweisen.");
            enabled = false; return;
        }
        if (quadMesh == null)
            quadMesh = Resources.GetBuiltinResource<Mesh>("Quad.fbx");

        posBuffer = new ComputeBuffer(count, sizeof(float) * 4);
        velBuffer = new ComputeBuffer(count, sizeof(float) * 4);

        // Init Daten (CPU einmalig)
        var rnd = new System.Random(123);
        var pos = new Vector4[count];
        var vel = new Vector4[count];
        for (int i = 0; i < count; i++)
        {
            float rx = Mathf.Lerp(worldMin.x, worldMax.x, (float)rnd.NextDouble());
            float ry = Mathf.Lerp(worldMin.y, worldMax.y, (float)rnd.NextDouble());
            pos[i] = new Vector4(rx, ry, 0f, particleSize);

            // kleine Zufallsdrift (nur genutzt, wenn gpuDrift=true)
            float ang = (float)(rnd.NextDouble() * Mathf.PI * 2.0);
            float spd = speed * (0.5f + (float)rnd.NextDouble()); // 0.5..1.5
            vel[i] = new Vector4(Mathf.Cos(ang) * spd, Mathf.Sin(ang) * spd, 0f, 0f);
        }
        posBuffer.SetData(pos);
        velBuffer.SetData(vel);

        // Indirect Draw Args
        argsBuffer = new ComputeBuffer(1, 5 * sizeof(uint), ComputeBufferType.IndirectArguments);
        uint[] args = { quadMesh.GetIndexCount(0), (uint)count, 0, 0, 0 };
        argsBuffer.SetData(args);

        // Große Bounds (XY-Welt + bisschen Spielraum)
        var size = new Vector3(worldMax.x - worldMin.x, worldMax.y - worldMin.y, 2f) + Vector3.one * 10f;
        bigBounds = new Bounds(Vector3.zero, size);

        // Material binden
        material.SetBuffer("_Pos", posBuffer);
        material.SetFloat("_Size", particleSize);

        // Compute vorbereiten (nur wenn Drift aktivierbar ist)
        if (compute != null)
        {
            kMove = compute.FindKernel("Move");
            // Statische Uniforms
            compute.SetInt("_ParticleCount", count);
            compute.SetFloats("_WorldMin", worldMin.x, worldMin.y);
            compute.SetFloats("_WorldMax", worldMax.x, worldMax.y);
            compute.SetBuffer(kMove, "_Pos", posBuffer);
            compute.SetBuffer(kMove, "_Vel", velBuffer);
        }

        Debug.Log($"Render-Perf-Test: Particles={count}, World=({worldMin})..({worldMax}), Drift={(gpuDrift ? "ON" : "OFF")}");
    }

    void Update()
    {
        Debug.Log("Update: " + Time.frameCount);
        Debug.Log("Count: " + count);
        
        if (gpuDrift && compute != null && kMove >= 0)
        {
            compute.SetFloat("_DeltaTime", Time.deltaTime);
            compute.Dispatch(kMove, Mathf.CeilToInt(count / 256f), 1, 1);
        }

        // zeichnen (ein Draw-Call)
        Graphics.DrawMeshInstancedIndirect(quadMesh, 0, material, bigBounds, argsBuffer);
    }

    void OnDisable()
    {
        posBuffer?.Release(); posBuffer = null;
        velBuffer?.Release(); velBuffer = null;
        argsBuffer?.Release(); argsBuffer = null;
    }
}

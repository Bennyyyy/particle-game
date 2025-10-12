using System;
using UnityEngine;
using System.Globalization;

public class AttractionUI : MonoBehaviour
{
    public ParticleLife2D sim; // auf dasselbe GameObject hängen oder hier referenzieren
    public Rect           windowRect    = new Rect(20, 20, 560, 420);
    public bool           forceSymmetry = true; // A[i,j] = A[j,i]
    public float          minValue      = -3f;
    public float          maxValue      = 3f;
    public int            decimals      = 2;                                  // Anzeige-Genauigkeit
    public bool           autoApply     = true;                               // sofort in den GPU-Buffer schreiben (nur Attraction-Matrix)
    public Color          windowColor   = new Color(0.10f, 0.10f, 0.10f, 1f); // undurchsichtig

    // Resize
    public Vector2 minWindowSize    = new Vector2(380f, 260f);
    const  float   resizeHandleSize = 16f;
    bool           resizing         = false;
    Vector2        resizeStartMouse;
    Vector2        resizeStartSize;

    Vector2  scroll;     // Grid-Scroll
    string[] fieldCache; // Textfeld-Cache pro Zelle
    GUIStyle labelC, fieldC, sliderC, windowSolid;
    string   minStr, maxStr;

    // Texturen für Vollflächen / Farbfelder
    Texture2D texWhite, texWindowBg;

    System.Random rng = new System.Random(12345);

    // Slider-Grenzen für die globalen Parameter
    const float PMin = 0.01f;
    const float PMax = 1.00f;

    void Awake()
    {
        if (sim == null) sim = GetComponent<ParticleLife2D>();
        // KEIN GUI-Zugriff hier
    }

    void OnEnable()
    {
        minStr = minValue.ToString(CultureInfo.InvariantCulture);
        maxStr = maxValue.ToString(CultureInfo.InvariantCulture);

        if (sim == null) sim = GetComponent<ParticleLife2D>();
        SyncFieldCache();
    }

    void Update()
    {
        // falls sich K ändert, Cache neu aufbauen
        int need = (sim != null && sim.attractMat != null) ? sim.attractMat.Length : 0;
        if (fieldCache == null || fieldCache.Length != need) SyncFieldCache();
    }

    void OnGUI()
    {
        if (sim == null || sim.attractMat == null || sim.typeCount <= 0) return;

        // Styles/Texturen lazy & sicher im GUI-Kontext anlegen
        if (labelC == null) InitStyles();

        // Undurchsichtiges Fenster per eigenem Style
        windowRect = GUI.Window(GetInstanceID(), windowRect, DrawWindow, "Attraction Matrix (K×K)", windowSolid);
    }

    void DrawWindow(int id)
    {
        if (sim == null)
        {
            GUI.DragWindow();
            return;
        }

        // Kopfzeile
        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        // --- Globale Parameter (0.01 .. 1.00) ---
        GUILayout.Space(4);
        GUILayout.Label("Parameter", labelC);
        
        // Counts
        SliderRowInt("particles", ref sim.count, 10, 10_000, () => sim.UpdateCount());
        SliderRowInt("species", ref sim.typeCount, 1, 16, () => sim.UpdateSpecies());
        
        // Interaction
        SliderRow("minDistance", ref sim.minDistance, PMin, 2.0f);
        SliderRow("interactRadius", ref sim.interactRadius, PMin, 2.0f, () => sim.UpdateGrid());
        
        // Energy
        SliderRow("dampingFactor", ref sim.dampingFactor, PMin, PMax);
        SliderRow("globalMultipler", ref sim.globalMultipler, PMin, PMax);
        
        // World Size
        SliderRow("particleSize", ref sim.particleSize, PMin, PMax);
        SliderRow("worldSize", ref sim.worldSizeY, 1, 20, () => sim.UpdateWorldSize());

        // Optionen (Matrix)
        GUILayout.Space(6);
        GUILayout.BeginHorizontal();
        forceSymmetry = GUILayout.Toggle(forceSymmetry, "Symmetry", GUILayout.Width(100));
        autoApply     = GUILayout.Toggle(autoApply, "Auto Apply", GUILayout.Width(100));
        if (GUILayout.Button("Apply", GUILayout.Width(80))) ApplyToGPU();
        GUILayout.FlexibleSpace();
        GUILayout.EndHorizontal();

        // Min/Max + Random (Matrix)
        GUILayout.BeginHorizontal();
        GUILayout.Label("Min", GUILayout.Width(30));
        FloatFieldUI(ref minValue, ref minStr, -10f, 10f, 60);

        GUILayout.Label("Max", GUILayout.Width(30));
        FloatFieldUI(ref maxValue, ref maxStr, -10f, 10f, 60);

        GUILayout.FlexibleSpace();
        if (GUILayout.Button("Random", GUILayout.Width(150)))
        {
            RandomizeMatrix(minValue, maxValue);
            MaybeApply();
        }

        if (GUILayout.Button("Reset", GUILayout.Width(150)))
        {
            SetMatrix(0f);
            MaybeApply();
        }

        GUILayout.EndHorizontal();

        GUILayout.Space(6);

        int   K        = sim.typeCount;
        float colWidth = 140f; // Spaltenbreite
        float colorBox = 14f;  // Größe der kleinen Farbfelder

        // Kopfzeile der Matrix: j-Labels mit Farbfeldern
        GUILayout.BeginHorizontal();
        GUILayout.Space(120);
        for (int j = 0; j < K; j++)
        {
            GUILayout.BeginHorizontal(GUILayout.Width(colWidth));
            DrawTypeColorBox(sim.typeColors, j, colorBox);
            GUILayout.Space(4);
            GUILayout.Label($"→ j {j}", labelC);
            GUILayout.EndHorizontal();
        }

        GUILayout.EndHorizontal();

        // Scrollbarer Bereich für KxK-Matrix
        float scrollHeight = Mathf.Min(220f, 24f + K * 38f);
        scroll = GUILayout.BeginScrollView(scroll, false, true, GUILayout.Height(scrollHeight));

        for (int i = 0; i < K; i++)
        {
            GUILayout.BeginHorizontal();

            // linke Seitenleiste: i-Label mit Farbfeld
            GUILayout.BeginHorizontal(GUILayout.Width(120));
            DrawTypeColorBox(sim.typeColors, i, colorBox);
            GUILayout.Space(4);
            GUILayout.Label($"i {i} →", labelC);
            GUILayout.EndHorizontal();

            // K Spalten
            for (int j = 0; j < K; j++)
            {
                int idx = i * K + j;
                GUILayout.BeginVertical(GUILayout.Width(colWidth));

                // Slider
                float cur    = sim.attractMat[idx];
                float newVal = GUILayout.HorizontalSlider(cur, minValue, maxValue);
                if (!Mathf.Approximately(newVal, cur))
                    SetValue(i, j, newVal);

                // Textfeld
                GUILayout.BeginHorizontal();
                GUILayout.Label("", GUILayout.Width(2));
                string prev = fieldCache[idx];
                string next = GUILayout.TextField(prev, fieldC, GUILayout.Width(colWidth - 4));
                if (!ReferenceEquals(prev, next))
                {
                    fieldCache[idx] = next;
                    if (TryParseField(next, out float parsed))
                        SetValue(i, j, Mathf.Clamp(parsed, minValue, maxValue), updateFieldText: false);
                }

                GUILayout.EndHorizontal();

                GUILayout.EndVertical();
            }

            GUILayout.EndHorizontal();
        }

        GUILayout.EndScrollView();

        // Resize-Handle unten rechts
        HandleResize(id);

        // Fenster verschieben (Titelbereich + Oberkante greifen)
        GUI.DragWindow();
    }

    // ---------- UI Helpers ----------

    void SliderRowInt(string label, ref int value, int min, int max, Action onChange = null)
    {
        // clamp und anzeigen
        value = Mathf.Clamp(value, min, max);

        GUILayout.BeginHorizontal();
        GUILayout.Label(label, labelC, GUILayout.Width(120));
        float newVal = GUILayout.HorizontalSlider(value, min, max, GUILayout.MinWidth(180));
        // Zahl daneben
        GUILayout.Label(newVal.ToString("F0"), labelC, GUILayout.Width(40));
        GUILayout.EndHorizontal();

        if (!Mathf.Approximately(newVal, value))
        {
            value = (int)newVal;
            onChange?.Invoke();
            SyncFieldCache();
            GUIUtility.ExitGUI();  
        }
    }

    void SliderRow(string label, ref float value, float min, float max, Action onChange = null)
    {
        // clamp und anzeigen
        value = Mathf.Clamp(value, min, max);

        GUILayout.BeginHorizontal();
        GUILayout.Label(label, labelC, GUILayout.Width(120));
        float newVal = GUILayout.HorizontalSlider(value, min, max, GUILayout.MinWidth(180));
        // Zahl daneben
        GUILayout.Label(newVal.ToString("F2"), labelC, GUILayout.Width(40));
        GUILayout.EndHorizontal();

        if (!Mathf.Approximately(newVal, value))
        {
            value = newVal;
            onChange?.Invoke();
        }
    }

    void FloatFieldUI(ref float val, ref string cache, float min, float max, float width)
    {
        string next = GUILayout.TextField(cache, fieldC, GUILayout.Width(width));
        if (!ReferenceEquals(next, cache))
        {
            cache = next;
            string norm = next.Replace(',', '.');
            if (float.TryParse(norm, NumberStyles.Float, CultureInfo.InvariantCulture, out float parsed))
                val = Mathf.Clamp(parsed, min, max);
        }
    }

    // ---------- Resize-Handle ----------
    void HandleResize(int id)
    {
        Rect r = new Rect(windowRect.width - resizeHandleSize - 4f,
            windowRect.height              - resizeHandleSize - 4f,
            resizeHandleSize, resizeHandleSize);

        var old = GUI.color;
        GUI.color = new Color(1, 1, 1, 0.25f);
        GUI.DrawTexture(r, texWhite);
        GUI.color = old;

        Event   e     = Event.current;
        Vector2 mouse = e.mousePosition;

        switch (e.type)
        {
            case EventType.MouseDown:
                if (r.Contains(mouse))
                {
                    resizing         = true;
                    resizeStartMouse = GUIUtility.GUIToScreenPoint(mouse);
                    resizeStartSize  = new Vector2(windowRect.width, windowRect.height);
                    e.Use();
                }

                break;

            case EventType.MouseDrag:
                if (resizing)
                {
                    Vector2 screenMouse = GUIUtility.GUIToScreenPoint(mouse);
                    Vector2 delta       = screenMouse - resizeStartMouse;
                    windowRect.width  = Mathf.Max(minWindowSize.x, resizeStartSize.x + delta.x);
                    windowRect.height = Mathf.Max(minWindowSize.y, resizeStartSize.y + delta.y);
                    e.Use();
                }

                break;

            case EventType.MouseUp:
                if (resizing)
                {
                    resizing = false;
                    e.Use();
                }

                break;
        }
    }

    void SetValue(int i, int j, float value, bool updateFieldText = true)
    {
        int K   = sim.typeCount;
        int idx = i * K + j;
        value               = Mathf.Clamp(value, minValue, maxValue);
        sim.attractMat[idx] = value;
        if (updateFieldText) fieldCache[idx] = value.ToString("F" + Mathf.Clamp(decimals, 0, 5));

        if (forceSymmetry && i != j)
        {
            int idx2 = j * K + i;
            sim.attractMat[idx2] = value;
            if (updateFieldText) fieldCache[idx2] = value.ToString("F" + Mathf.Clamp(decimals, 0, 5));
        }

        MaybeApply();
    }

    void MaybeApply()
    {
        if (autoApply) ApplyToGPU();
    }

    void ApplyToGPU()
    {
        if (sim != null) sim.ApplyAttractionMatrix();
    }

    void RandomizeMatrix(float a, float b)
    {
        int   K     = sim.typeCount;
        float range = b - a;

        if (forceSymmetry)
        {
            for (int i = 0; i < K; i++)
            {
                for (int j = i; j < K; j++)
                {
                    float v    = a + (float)rng.NextDouble() * range;
                    int   idx1 = i                           * K + j;
                    int   idx2 = j                           * K + i;
                    sim.attractMat[idx1] = v;
                    sim.attractMat[idx2] = v;
                }
            }
        }
        else
        {
            for (int i = 0; i < K; i++)
            for (int j = 0; j < K; j++)
            {
                int idx = i * K + j;
                sim.attractMat[idx] = a + (float)rng.NextDouble() * range;
            }
        }

        for (int n = 0; n < sim.attractMat.Length; n++)
            fieldCache[n] = sim.attractMat[n].ToString("F" + Mathf.Clamp(decimals, 0, 5));
    }

    void SetMatrix(float a)
    {
        int K = sim.typeCount;
        for (int i = 0; i < K; i++)
        for (int j = 0; j < K; j++)
        {
            int idx = i * K + j;
            sim.attractMat[idx] = a;
        }

        for (int n = 0; n < sim.attractMat.Length; n++)
            fieldCache[n] = sim.attractMat[n].ToString("F" + Mathf.Clamp(decimals, 0, 5));
    }

    void SyncFieldCache()
    {
        if (sim == null || sim.attractMat == null)
        {
            fieldCache = null;
            return;
        }

        fieldCache = new string[sim.attractMat.Length];
        for (int n = 0; n < sim.attractMat.Length; n++)
            fieldCache[n] = sim.attractMat[n].ToString("F" + Mathf.Clamp(decimals, 0, 5));
    }

    bool TryParseField(string s, out float val)
    {
        s = s.Replace(',', '.');
        if (float.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out val)) return true;
        val = 0;
        return false;
    }

    void InitStyles()
    {
        // sichere Erstellung im GUI-Kontext
        labelC  = new GUIStyle(GUI.skin.label) { alignment     = TextAnchor.MiddleLeft, fontSize   = 12 };
        fieldC  = new GUIStyle(GUI.skin.textField) { alignment = TextAnchor.MiddleCenter, fontSize = 12 };
        sliderC = new GUIStyle(GUI.skin.horizontalSlider);

        // 1×1-Texturen für Farbfelder & Fenster-Hintergrund
        if (texWhite == null)
        {
            texWhite = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texWhite.SetPixel(0, 0, Color.white);
            texWhite.Apply();
        }

        if (texWindowBg == null)
        {
            texWindowBg = new Texture2D(1, 1, TextureFormat.RGBA32, false);
            texWindowBg.SetPixel(0, 0, windowColor);
            texWindowBg.Apply();
        }

        // undurchsichtiger Fensterstil
        windowSolid                     = new GUIStyle(GUI.skin.window);
        windowSolid.normal.background   = texWindowBg;
        windowSolid.onNormal.background = texWindowBg;
        windowSolid.padding             = new RectOffset(8, 8, 20, 8);
    }

    void DrawTypeColorBox(Color[] palette, int idx, float size)
    {
        if (palette == null || idx < 0 || idx >= palette.Length) return;
        Color c   = palette[idx];
        var   r   = GUILayoutUtility.GetRect(size, size, GUILayout.Width(size), GUILayout.Height(size));
        var   old = GUI.color;
        GUI.color = c;
        GUI.DrawTexture(r, texWhite);
        GUI.color = old;
    }
}
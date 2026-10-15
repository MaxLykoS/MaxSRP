using MaxSRP;
using UnityEngine;

[ExecuteAlways]
public class MaxLightProbeComponent : MonoBehaviour
{
    private MaxLightProbeSkybox skybox;
    private Texture previousDiffuse;
    private Texture ownedDiffuse;
    private readonly Vector4[] previousCoefficients = new Vector4[9];
    public Cubemap cubemap;
    public ComputeShader cs;
    public Cubemap baked;

    private void OnEnable()
    {
        if (cubemap == null || cs == null) return;
        previousDiffuse = Shader.GetGlobalTexture("_IBLDiffuse");
        for (int i = 0; i < previousCoefficients.Length; ++i)
            previousCoefficients[i] = Shader.GetGlobalVector("c" + i);
        skybox = new MaxLightProbeSkybox(cubemap, cs);
        try
        {
            skybox.Bake();
            skybox.Submit();
            ownedDiffuse = Shader.GetGlobalTexture("_IBLDiffuse");
        }
        catch
        {
            OnDisable();
            throw;
        }
    }

    private void OnDisable()
    {
        if (ownedDiffuse != null && Shader.GetGlobalTexture("_IBLDiffuse") == ownedDiffuse)
        {
            Shader.SetGlobalTexture("_IBLDiffuse", previousDiffuse != null ? previousDiffuse : null);
            for (int i = 0; i < previousCoefficients.Length; ++i)
                Shader.SetGlobalVector("c" + i, previousCoefficients[i]);
        }
        skybox?.Dispose();
        skybox = null;
        ownedDiffuse = null;
        previousDiffuse = null;
    }

    private void OnDestroy()
    {
        OnDisable();
    }
}

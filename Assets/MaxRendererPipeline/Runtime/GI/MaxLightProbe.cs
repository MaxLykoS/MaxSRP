using System.Collections;
using System.Collections.Generic;
using UnityEngine;

public struct Surfel
{
    public Vector3 position;
    public Vector3 normal;
    public Vector3 albedo;
    public float skyMask;
}

public class MaxLightProbe : MonoBehaviour
{
    const int TX = 32;
    const int TY = 16;
    const int RAY_NUM = TX * TY;
    const int SURFEL_BYTE_SIZE = sizeof(float) * (3 + 3 + 3 + 1);
    public enum ProbeDebugMode
    {
        None,
        SphereDistribution,
        SampleDirection,
        Surfel,
        SurfelRadience
    }

    private RenderTexture worldPos;
    private RenderTexture normal;
    private RenderTexture albedo;

    private Surfel[] readBackBuffer;
    private ComputeBuffer surfels;

    private void OnDrawGizmos()
    {
        Vector3 probePos = gameObject.transform.position;


    }

    public void TryInit()
    {
        if (surfels == null)
            surfels = new ComputeBuffer(RAY_NUM, SURFEL_BYTE_SIZE);
    }

    public void RenderToCubemap()
    {
        TryInit();

        // create camera
        GameObject go = new GameObject("CubemapCamera");
        go.transform.position = transform.position;
        go.transform.rotation = Quaternion.identity;
        go.AddComponent<Camera>();
        Camera camera = go.GetComponent<Camera>();
        camera.clearFlags = CameraClearFlags.SolidColor;
        camera.backgroundColor = new Color(0.0f, 0.0f, 0.0f, 0.0f);

        // find all objects
        GameObject[] gameObjects = FindObjectsOfType(typeof(GameObject)) as GameObject[];

        // capture gbuffer worldpos
        SetShaderReplacement(gameObjects, Shader.Find("MaxSRP/CubemapWorldPos"));
        //camera.RenderToCubemap(RT_WorldPos);

        // capture gbuffer normal
        SetShaderReplacement(gameObjects, Shader.Find("MaxSRP/CubemapWorldPos"));
        //camera.RenderToCubemap(RT_Normal);

        // capture gbuffer albedo
        SetShaderReplacement(gameObjects, Shader.Find("MaxSRP/CubemapWorldPos"));
        //camera.RenderToCubemap(RT_Albedo);

        // reset shader
        SetShaderReplacement(gameObjects, Shader.Find("MaxSRP/PBRLit"));

        //SampleSurfels(RT_WorldPos, RT_Normal, RT_Albedo);

        DestroyImmediate(go);
    }

    void SetShaderReplacement(GameObject[] gameObjects, Shader shader)
    {
        foreach (var go in gameObjects)
        {
            MeshRenderer meshRenderer = go.GetComponent<MeshRenderer>();
            if (meshRenderer != null)
            {
                meshRenderer.sharedMaterial.shader = shader;
            }
        }
    }
}

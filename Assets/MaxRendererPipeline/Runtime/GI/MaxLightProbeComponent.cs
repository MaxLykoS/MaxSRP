using MaxSRP;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

[ExecuteAlways]
public class MaxLightProbeComponent : MonoBehaviour
{
    private Cubemap test;
    private MaxLightProbeSkybox skybox;

    public Cubemap cubemap;
    public ComputeShader cs;
    public Cubemap baked;
    void Start()
    {
        skybox = new MaxLightProbeSkybox(cubemap, cs);
        skybox.Bake();
        skybox.Submit();
    }
}

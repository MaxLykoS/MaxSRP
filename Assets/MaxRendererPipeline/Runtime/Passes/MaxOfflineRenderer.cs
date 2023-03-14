using MaxSRP;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using UnityEditor;
using UnityEditor.VersionControl;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.Rendering;

class MaxOfflineRenderer
{
    // 1 - 100
    public int BounceCountOpaque = 5;
    // 1 - 100
    public int BounceCountTransparent = 8;

    private uint m_canvasWidth;
    private uint m_canvasHeight;
    private RayTracingAccelerationStructure m_BVH = null;
    private RenderTexture m_outputRT;
    private Matrix4x4 m_preCameraMatrix;
    private int m_prevBounceCountOpaque = 0;
    private int m_prevBounceCountTransparent = 0;

    private int m_convergenceStep = 0;
    private RayTracingShader m_shader = null;
    private Cubemap m_envMap = null;

    private CommandBuffer m_cmd = new CommandBuffer();

    private void CreateBVH()
    {
        if (m_BVH == null)
        {
            RayTracingAccelerationStructure.RASSettings ss = new RayTracingAccelerationStructure.RASSettings();
            ss.rayTracingModeMask = RayTracingAccelerationStructure.RayTracingModeMask.Everything;
            ss.managementMode = RayTracingAccelerationStructure.ManagementMode.Automatic;
            ss.layerMask = 255;

            m_BVH = new RayTracingAccelerationStructure(ss);
            m_BVH.Build();

            m_shader.SetAccelerationStructure(ShaderProperty.BVHID, m_BVH);
        }
    }

    ~MaxOfflineRenderer()
    {
        m_BVH?.Release();

        m_outputRT.Release();
        m_cmd.Release();

        m_canvasWidth = 0;
        m_canvasHeight = 0;
    }

    public MaxOfflineRenderer()
    {
        m_shader = (RayTracingShader)AssetDatabase.LoadAssetAtPath("Assets/MaxRendererPipeline/ShaderLibrary/Offline/RayGenerator.raytrace", typeof(RayTracingShader));
        m_envMap = (Cubemap)AssetDatabase.LoadAssetAtPath("Assets/Testing/herkulessaulen_4k.hdr", typeof(Cubemap));
        m_notUploaded = true;

        CreateBVH();
    }

    private void Update(Camera cam)
    {
        if (m_canvasWidth != cam.pixelWidth || m_canvasHeight != cam.pixelHeight)
        {
            m_outputRT?.Release();

            RenderTextureDescriptor rtDesc = new RenderTextureDescriptor()
            {
                dimension = UnityEngine.Rendering.TextureDimension.Tex2D,
                width = cam.pixelWidth,
                height = cam.pixelHeight,
                depthBufferBits = 0,
                volumeDepth = 1,
                msaaSamples = 1,
                vrUsage = VRTextureUsage.None,
                graphicsFormat = GraphicsFormat.R32G32B32A32_SFloat,
                enableRandomWrite = true,
            };
            m_outputRT = new RenderTexture(rtDesc);
            m_outputRT.Create();

            m_canvasWidth = (uint)cam.pixelWidth;
            m_canvasHeight = (uint)cam.pixelHeight;

            m_convergenceStep = 0;
        }
    }

    private bool m_notUploaded;
    public void Render(ScriptableRenderContext context, Camera cam, bool dynamic = false)
    {
        Update(cam);
        
        if (!SystemInfo.supportsRayTracing)
        {
            Debug.LogError("硬件不支持");
            return;
        }

        // view changed
        if (m_preCameraMatrix != cam.cameraToWorldMatrix)
        {
            m_convergenceStep = 0;
        }
        if (m_prevBounceCountOpaque != BounceCountOpaque)
            m_convergenceStep = 0;
        if (m_prevBounceCountTransparent != BounceCountTransparent)
            m_convergenceStep = 0;

        m_cmd.SetRayTracingShaderPass(m_shader, ShaderProperty.PathTracingPassName);

        m_cmd.SetGlobalInteger(ShaderProperty.BounceCountOpaqueID, BounceCountOpaque);
        m_cmd.SetGlobalInteger(ShaderProperty.BounceCountTransparentID, BounceCountTransparent);

        // Input
        if (m_notUploaded | dynamic)
        {
            m_cmd.BuildRayTracingAccelerationStructure(m_BVH);
            m_cmd.SetRayTracingAccelerationStructure(m_shader, ShaderProperty.BVHID, m_BVH);
            m_cmd.SetRayTracingTextureParam(m_shader, ShaderProperty.EnvMapID, m_envMap);

            m_notUploaded = false;
        }
        m_cmd.SetRayTracingFloatParam(m_shader, ShaderProperty.ZoomID, Mathf.Tan(Mathf.Deg2Rad * Camera.main.fieldOfView * 0.5f));
        m_cmd.SetRayTracingFloatParam(m_shader, ShaderProperty.AspectRatioID, m_canvasWidth / (float)m_canvasHeight);
        m_cmd.SetRayTracingIntParam(m_shader, ShaderProperty.ConvergenceStepID, m_convergenceStep);
        m_cmd.SetRayTracingIntParam(m_shader, ShaderProperty.FrameIndexID, Time.frameCount);

        // output
        m_cmd.SetRayTracingTextureParam(m_shader, ShaderProperty.RadianceID, m_outputRT);

        m_cmd.DispatchRays(m_shader, "MainRayGenShader", m_canvasWidth, m_canvasHeight, 1, cam);

        m_cmd.Blit(m_outputRT, BuiltinRenderTextureType.CameraTarget);

        ++m_convergenceStep;

        context.ExecuteCommandBuffer(m_cmd);
        m_cmd.Clear();

        m_preCameraMatrix = cam.cameraToWorldMatrix;
        m_prevBounceCountOpaque = BounceCountOpaque;
        m_prevBounceCountTransparent = BounceCountTransparent;
    }

    private class ShaderProperty
    {
        public static string PathTracingPassName = "PathTracing";

        public static int BounceCountOpaqueID = Shader.PropertyToID("_BounceCountOpaque");
        public static int BounceCountTransparentID = Shader.PropertyToID("_BounceCountTransparent");

        public static int BVHID = Shader.PropertyToID("_AccelStruct");
        public static int ZoomID = Shader.PropertyToID("_Zoom");
        public static int AspectRatioID = Shader.PropertyToID("_AspectRatio");
        public static int ConvergenceStepID = Shader.PropertyToID("_ConvergenceStep");
        public static int FrameIndexID = Shader.PropertyToID("_FrameIndex");
        public static int EnvMapID = Shader.PropertyToID("_EnvMap");

        public static int RadianceID = Shader.PropertyToID("_RadianceMap");
    }
}

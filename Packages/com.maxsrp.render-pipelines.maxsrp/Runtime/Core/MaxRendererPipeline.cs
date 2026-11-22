using System.Collections.Generic;
#if UNITY_EDITOR
using UnityEditor;
#endif
using UnityEngine;
using UnityEngine.Rendering;

namespace MaxSRP
{
    public class MaxRenderPipeline : RenderPipeline
    {
        private readonly MaxLightConfigurator m_lightConfigurator = new MaxLightConfigurator();
        private readonly Plane[] m_cameraCullingPlanes = new Plane[6];
        private readonly MaxRenderTargets m_targets = new MaxRenderTargets();
        private readonly MaxTimeOfDay m_timeOfDay;
        private MaxRenderPasses m_passes;
        private readonly MaxRenderConfiguration m_configuration;

        /// <summary>Queue the world clock for the next render batch.</summary>
        public bool SetTimeOfDayHour(float hour) => m_timeOfDay.SetHour(hour);

        /// <summary>Read the latest requested clock, including an update queued for the next batch.</summary>
        public bool TryGetTimeOfDayHour(out float hour) => m_timeOfDay.TryGetHour(out hour);

        internal MaxTimeOfDayState TimeOfDay => m_timeOfDay.Current;
        internal bool IsTimeOfDayAuthor(int authorId) => m_timeOfDay.IsAuthor(authorId);

        // 切镜头或发生突变时，使该相机的阴影、TAA 和曝光历史失效。
        public void ResetTemporalHistory(Camera camera)
        {
            m_passes.Shadows.Invalidate(camera);
            m_passes.TemporalAA.ResetHistory(camera);
            m_passes.PostProcess.ResetHistory(camera);
        }

        // 初始化渲染通道和光照贴图支持，并注册共享历史重置回调。
        public MaxRenderPipeline(MaxRendererPipelineAsset setting)
        {
            GraphicsSettings.useScriptableRenderPipelineBatching = true;
            m_configuration = new MaxRenderConfiguration(setting);
            m_passes = new MaxRenderPasses(m_configuration.Current);

            try
            {
                MaxLightmapping.Setup();
            }
            catch
            {
                m_passes.Dispose();
                MaxLightmapping.Reset();
                throw;
            }

            m_timeOfDay = new MaxTimeOfDay();
            MaxRenderBridge.TemporalHistoryInvalidated += ResetTemporalHistory;
#if UNITY_EDITOR
            EditorApplication.update += RequestStreamRender;
            AssemblyReloadEvents.beforeAssemblyReload += m_timeOfDay.DisposeStreams;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
#endif
        }

#if UNITY_EDITOR
        private void RequestStreamRender()
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || !m_timeOfDay.IsLoading) return;
            EditorApplication.QueuePlayerLoopUpdate();
            SceneView.RepaintAll();
        }

        private void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingEditMode || state == PlayModeStateChange.ExitingPlayMode)
                m_timeOfDay.DisposeStreams();
            if (state == PlayModeStateChange.EnteredEditMode)
                m_passes.TemporalAA.ClearHistories();
        }
#endif

        // 在渲染批次开始时，应用影响渲染通道构造的配置变更。
        private void RefreshPasses()
        {
            if (!m_configuration.TryRefresh(out var next, out var changes)) return;
            if (changes != MaxPassChanges.None)
            {
                MaxRenderPasses previous = m_passes;
                var replacement = new MaxRenderPasses(next, previous, changes);
                replacement.TakeOwnershipFrom(previous);
                m_passes = replacement;
                previous.Dispose();
            }
            m_configuration.Commit(next);
        }

        // 处理 Unity 传入相机数组的渲染入口。
        protected override void Render(ScriptableRenderContext context, Camera[] cameras)
            => RenderCameras(context, cameras);

        // 直接处理 Unity 传入的相机列表，省去转为数组的复制。
        protected override void Render(ScriptableRenderContext context, List<Camera> cameras)
            => RenderCameras(context, cameras);

        // 准备共享资源，再按顺序渲染并提交每个可用相机。
        private void RenderCameras(ScriptableRenderContext context, IReadOnlyList<Camera> cameras)
        {
            using var renderScope = MaxRenderProfiler.RenderFrame.Auto();
            MaxTimeOfDayState timeOfDay;
            CelestialLightData celestial;
            MaxRenderConfiguration.Snapshot configuration;
            using (MaxRenderProfiler.FrameSetup.Auto())
            {
                // 根据配置文件更新管线配置
                RefreshPasses();
                configuration = m_configuration.Current;
                m_timeOfDay.ApplyPending();

                // 每个渲染通道回收已销毁相机的资源。
                m_passes.PruneDestroyedCameras();
                m_targets.PruneDestroyedCameras();

                timeOfDay = m_timeOfDay.Current;
                m_passes.DDGI.Execute(timeOfDay);
                SunLightData sun = SunLightData.Capture(timeOfDay?.Sun);
                if (!sun.HasSun) sun = SunLightData.Capture();
                celestial = timeOfDay != null ? timeOfDay.EvaluateLights(sun) : CelestialLightData.FromSun(sun);
                ShaderBindings.SetPerFrameShaderVariables(context);
            }

            for (int cameraIndex = 0; cameraIndex < cameras.Count; ++cameraIndex)
            {
                Camera camera = cameras[cameraIndex];
                if (camera == null || camera.pixelWidth <= 0 || camera.pixelHeight <= 0)
                    continue;

#if UNITY_EDITOR
                if (camera.cameraType == CameraType.SceneView)
                    ScriptableRenderContext.EmitWorldGeometryForSceneView(camera);
#endif
                try
                {
                    RenderCamera(context, camera, timeOfDay, celestial, configuration);
                }
                catch
                {
                    m_passes.TemporalAA.ResetHistory(camera);
                    m_passes.PostProcess.ResetHistory(camera);
                    throw;
                }
                finally
                {
                    // 在下一个相机改写共享材质和状态前，先提交当前相机的命令。
                    using (MaxRenderProfiler.ContextSubmit.Auto()) context.Submit();
                }
            }
        }

        // 为一台相机编排从可见性剔除到最终输出的各个 Pass。
        // RenderCameras 在批次开始时统一应用配置与 TOD 更新，并计算太阳、月亮及当前主光。
        // 每台相机消费同一份批次输入，同时管理自己的视口、渲染目标和时间历史。
        // 先画阴影和几何，再计算并合成线性 HDR 光照。TAA 解析完成后测光、生成 Bloom，
        // 最后写入相机目标并安排参与者的帧末工作。单相机的命令由外层 RenderCameras 提交。
        private void RenderCamera(ScriptableRenderContext context, Camera camera,
            MaxTimeOfDayState timeOfDay, CelestialLightData celestial, MaxRenderConfiguration.Snapshot configuration)
        {
            using var cameraScope = (camera.cameraType == CameraType.Game ? MaxRenderProfiler.GameCamera :
                camera.cameraType == CameraType.SceneView ? MaxRenderProfiler.SceneViewCamera : MaxRenderProfiler.OtherCamera).Auto();

            MaxShadowCasterPass.ResetReceiverState(context);

#if UNITY_EDITOR
            bool isLightingDebugMode = timeOfDay?.IsLightingDebugMode ?? false;
#endif
            // 准备相机的光照输入与时间历史。当前主光用于方向阴影，完整的日月数据还供大气与延迟光照使用。
            SunLightData mainLight = celestial.MainLight;

            // 解析本相机的 AO 设置并取得烘焙天空可见度贴图。AO 在几何绘制后计算，贴图在 GBuffer 写入前绑定。
            var ao = configuration.ResolveAmbientOcclusion(camera, timeOfDay);
            Texture2DArray skyVisibility = MaxBakedLighting.GetSkyVisibility(camera, timeOfDay);

            // Begin 准备当前 jitter 和上一帧重投影数据。返回 null 时，相机走无 jitter 路径并跳过运动矢量和 TAA。
            MaxTemporalAAPass.CameraHistory temporal;
            using (MaxRenderProfiler.TemporalHistory.Auto())
                temporal = m_passes.TemporalAA.Begin(camera, configuration.TemporalAA);

            // 按相机视锥剔除不可见对象，得到后续绘制要用的可见物体和灯光。
            ScriptableCullingParameters cullingParams;
            using (MaxRenderProfiler.CullingSetup.Auto())
            {
                context.SetupCameraProperties(camera);
                if (!camera.TryGetCullingParameters(out cullingParams))
                    return;

                // Game 相机的剔除使用实际视口对应的无 jitter 投影。启用 TAA 时将剔除范围四边各扩一像素，
                // 让后续带 jitter 的几何绘制仍能覆盖画面边缘。级联阴影也以无 jitter 投影为基础。
                if (camera.cameraType == CameraType.Game)
                {
                    Matrix4x4 projection = CameraViewport.GetProjection(camera);
                    if (temporal != null)
                    {
                        Vector2Int cullSize = CameraViewport.GetRenderSize(camera);
                        projection.SetRow(0, projection.GetRow(0) * (cullSize.x / (cullSize.x + 2f)));
                        projection.SetRow(1, projection.GetRow(1) * (cullSize.y / (cullSize.y + 2f)));
                    }

                    Matrix4x4 cullingMatrix = projection * camera.worldToCameraMatrix;
                    cullingParams.cullingMatrix = cullingMatrix;
                    GeometryUtility.CalculateFrustumPlanes(cullingMatrix, m_cameraCullingPlanes);
                    cullingParams.cullingPlaneCount = m_cameraCullingPlanes.Length;
                    for (int plane = 0; plane < m_cameraCullingPlanes.Length; ++plane)
                        cullingParams.SetCullingPlane(plane, m_cameraCullingPlanes[plane]);
                }

                cullingParams.shadowDistance = Mathf.Min(configuration.CascadeSetting.ShadowDistance,
                    camera.farClipPlane - camera.nearClipPlane);
            }

            CullingResults cullingResults;
            using (MaxRenderProfiler.Cull.Auto()) cullingResults = context.Cull(ref cullingParams);

            // 准备地形等通过渲染桥接入的参与者。地形按相机选择网格块，更新高度 Clipmap 和虚拟纹理状态。
            // 在阴影绘制前完成准备，使阴影、GBuffer 和运动矢量 Pass 都能使用本次选出的地形。
            using (MaxRenderProfiler.PrepareParticipants.Auto()) MaxRenderBridge.PrepareCamera(context, camera);

            using (MaxRenderProfiler.LightSetup.Auto()) m_lightConfigurator.SetupMultiShaderLightingParams(context, ref cullingResults, mainLight);

            // 远级联会复用先前画过的深度。日月主光交接时将该相机的阴影缓存标记为失效，避免复用旧方向的阴影。
            var previousResources = m_targets.FindCamera(camera);
            if (previousResources != null && previousResources.IsMoonMain != celestial.IsMoonMain)
                m_passes.Shadows.Invalidate(camera);
            using (MaxRenderProfiler.Shadows.Auto())
                m_passes.Shadows.Execute(context, camera, mainLight);

            // 切回相机视角。接下来的 GBuffer 和 MotionVectors 使用同一套光栅投影，启用 TAA 时都带 jitter。
            context.SetupCameraProperties(camera);
            MaxTemporalAAPass.Bind(context, camera, temporal);

            // 每台相机按有效视口尺寸持有 GBuffer、几何深度与线性 HDR 颜色和深度。
            // 离屏 Pass 统一从 (0, 0) 开始，最终输出才应用相机在目标上的视口偏移。
            Vector2Int renderSize = CameraViewport.GetRenderSize(camera);
            int width = renderSize.x, height = renderSize.y;
            Rect viewport = new Rect(0, 0, width, height);
            MaxRenderTargets.CameraResources resources;
            using (MaxRenderProfiler.EnsureRenderTargets.Auto())
                resources = m_targets.GetCamera(camera, width, height, configuration.Platform);
            MaxRenderTargets.OutputResources output = resources.Output;
            resources.IsMoonMain = celestial.IsMoonMain;

            using (var gbuffer = new GpuPassScope(context, "GBuffer"))
            {
                CommandBuffer cmd = gbuffer.Buffer;
                using (MaxRenderProfiler.GBufferSetup.Auto())
                {
                    // 先清场景颜色，再清材质和供后续通道复用的几何深度。
                    // 后一次绑定保留为不透明绘制的目标，后续 Pass 通过全局纹理读取这些 GBuffer。
                    cmd.SetRenderTarget(output.Color);
                    cmd.SetViewport(viewport);
                    cmd.ClearRenderTarget(false, true, Color.clear);

                    cmd.SetRenderTarget(resources.BufferIDs, resources.Depth);
                    cmd.SetViewport(viewport);
                    cmd.ClearRenderTarget(true, true, Color.clear);
                    resources.BindGBuffer(cmd);

#if UNITY_EDITOR
                    // 为每个相机同步 GBuffer 和延迟光照的诊断模式。
                    MaxTimeOfDayState.BindIndirectLightingDebug(cmd, timeOfDay);
                    cmd.SetGlobalFloat("_MaxLightingDiagnosticMode", MaxLightPass.GetDiagnosticMode(camera));
#endif
                    // 材质在写 GBuffer 时采样烘焙天空可见度，延迟光照再从 GBuffer 读取结果。
                    MaxBakedLighting.Bind(cmd, skyVisibility);

                    gbuffer.Enqueue();
                }

                // 先让 Unity 把可见的不透明物体画入 GBuffer，再通过渲染桥提交程序化地形的 GBuffer 绘制和虚拟纹理反馈写入。
                using (MaxRenderProfiler.DrawOpaque.Auto()) m_passes.Opaque.Execute(context, camera, ref cullingResults);
                if (MaxRenderBridge.HasParticipants(camera))
                {
                    using var gbufferScope = MaxRenderProfiler.ParticipantGBuffer.Auto();
                    CommandBuffer terrain = CommandBufferPool.Get("MaxTerrain GBuffer + VT Feedback");
                    try
                    {
                        terrain.SetRenderTarget(resources.BufferIDs, resources.Depth);
                        terrain.SetViewport(viewport);
                        MaxRenderBridge.RecordGBuffer(terrain, camera);
                        context.ExecuteCommandBuffer(terrain);
                    }
                    finally { CommandBufferPool.Release(terrain); }
                }
            }

            // 启用 TAA 时用几何深度约束运动矢量绘制，使速度对应 GBuffer 最终覆盖的表面。
            // 顶点覆盖使用带 jitter 的投影，速度记录的是去掉 jitter 后的屏幕位移。
            if (temporal != null)
                using (MaxRenderProfiler.MotionVectors.Auto())
                    m_passes.TemporalAA.DrawMotion(context, camera, temporal, resources.Depth, ref cullingResults);

            // 几何和速度绘制结束后，恢复当前相机的离屏渲染状态，供后续 Pass 使用。
            context.SetupCameraProperties(camera);
            MaxTemporalAAPass.Bind(context, camera, temporal);

            // 选定最终输出的颜色和深度附件。显式目标纹理缺少深度时，使用为它准备的独立深度附件。
            // 当前帧仍在相机自己的 HDR 目标内完成，直到 ColorOutput 才写入这里选定的目标。
            RenderTargetIdentifier target = camera.targetTexture != null
                ? new RenderTargetIdentifier(camera.targetTexture)
                : new RenderTargetIdentifier(BuiltinRenderTextureType.CameraTarget);
            RenderTargetIdentifier outputDepth = output.OutputDepth != null
                ? new RenderTargetIdentifier(output.OutputDepth) : target;

            // 根据日月光和观察点准备大气查找表。透射率影响表面光照，空气透视用于场景雾，
            // 相机天空和环境天空分别供背景绘制和 IBL 使用。天空与空气透视按相机和配置所需准备。
            using (MaxRenderProfiler.Atmosphere.Auto())
                m_passes.Atmosphere.PrepareAtmosphere(context, camera, celestial, configuration.SkyIBL.ReferenceHeight);

            // 六向云准备屏幕绘制用的云卡片，采样大气透射率和环境天空计算云的光照。
            bool suppressClouds = timeOfDay != null && !timeOfDay.RenderClouds;
#if UNITY_EDITOR
            suppressClouds |= isLightingDebugMode;
#endif
            using (MaxRenderProfiler.Clouds.Auto())
                m_passes.SixWayCloud.Prepare(context, camera, mainLight, configuration.AtmosphereScatteringSetting,
                    m_passes.Atmosphere.TransmittanceLUT, m_passes.Atmosphere.EnvironmentSky,
                    temporal == null ? camera.transform.position : temporal.Position,
                    m_passes.Atmosphere.EnvironmentViewHeight, m_passes.Atmosphere.SkyViewSizeAndInvSize, suppressClouds);

            // 大气查找表按无 jitter 投影生成。回到场景绘制前重新绑定光栅矩阵，
            // 保证延迟光照反推的世界位置和程序化天空的观察射线与几何深度对齐。
            MaxTemporalAAPass.Bind(context, camera, temporal);

            // IBL 的环境来源是大气天空 LUT。大气版本用于判断是否要重新生成漫反射和镜面 IBL。
            using (MaxRenderProfiler.SkyIBL.Auto()) m_passes.SkyIBL.Execute(context, m_passes.Atmosphere.EnvironmentSky,
                m_passes.Atmosphere.EnvironmentRevision, configuration.SkyIBL, configuration.AtmosphereScatteringSetting._PlanetRadius,
                m_passes.Atmosphere.EnvironmentViewHeight, m_passes.Atmosphere.SkyViewSizeAndInvSize);

            // 建立本相机的 Cluster 灯表，让延迟光照按像素位置找到相关的局部灯。
            MaxClusteredLightingPass.CameraLightResources localLights;
            using (MaxRenderProfiler.ClusteredLights.Auto())
                localLights = m_passes.ClusteredLighting.Prepare(context, camera, width, height,
                    m_lightConfigurator, configuration.ClusteredLighting);

            // 读取几何深度和法线，生成当前平台的屏幕空间 AO 可见度。
            RenderTexture aoVisibility;
            using (MaxRenderProfiler.AmbientOcclusion.Auto())
                aoVisibility = resources.RenderAmbientOcclusion(context, ao, output.Color, temporal);

            // 到这里材质、阴影、环境光、局部灯和 AO 都已就绪。延迟光照从 GBuffer 重建表面位置，
            // Lighting 采样几何深度时仅绑定颜色目标，后续天空和 Gizmos 直接复用几何深度。
            using (MaxRenderProfiler.DeferredLighting.Auto()) m_passes.Lighting.Execute(context, camera, output.Color, BuiltinRenderTextureType.None, viewport, m_passes.SkyIBL, localLights,
                timeOfDay, celestial, ao, aoVisibility);

            // 天空和云在场景光照之后填充背景，已有的场景深度保证前景能遮住天空。
            // AO 独显及编辑器光照独显有自己的背景规则。
            bool showAOOnly = ao.AOEnabled && ao.AOOnly;
            bool drawSky = camera.clearFlags == CameraClearFlags.Skybox;
#if UNITY_EDITOR
            showAOOnly &= !isLightingDebugMode && MaxLightPass.GetDiagnosticMode(camera) == 0.0f;
            drawSky &= !isLightingDebugMode;
#endif
            if (drawSky && !showAOOnly)
            {
                using var skyScope = MaxRenderProfiler.DrawSky.Auto();
                CommandBuffer cmd = CommandBufferPool.Get("Procedural Skybox");
                try
                {
                    m_passes.Atmosphere.DrawSkybox(context, camera, cmd, output.Color, resources.Depth, viewport);
                    m_passes.SixWayCloud.Draw(context, output.Color, resources.Depth, viewport);
                }
                finally
                {
                    CommandBufferPool.Release(cmd);
                }
            }

            // TAA 处理的是已合成几何与天空的线性 HDR。此时尚未统计本帧曝光、生成 Bloom 或绘制叠加。
            TemporalAADebugView taaDebug = temporal == null ? TemporalAADebugView.Off : configuration.TemporalDebugView;
            // AO、烘焙天空可见度和材质诊断是线性遮罩，最终须保持它们的 0 到 1 数值。
            bool sceneIsLinearMask = showAOOnly ||
#if UNITY_EDITOR
                (timeOfDay?.IsSkyVisibilityOnly ?? false) ||
#endif
                MaxRenderBridge.HasMaterialDiagnostics(camera);
            bool showTaaDebug = taaDebug != TemporalAADebugView.Off;
            bool showLinearMask = showTaaDebug || sceneIsLinearMask;
            bool drawGizmos = false;
#if UNITY_EDITOR
            drawGizmos = Handles.ShouldRenderGizmos();
#endif
            RenderTexture resolvedColor = output.Color;
            MaxPostProcessSettings postConfiguration = configuration.PostProcess;
            if (temporal != null)
            {
                using var temporalScope = MaxRenderProfiler.TemporalResolve.Auto();
                // TAA 用曝光估算亮度权重。自动曝光沿用上次有效的测光结果，手动曝光使用当前配置。
                // 解析和历史都保存固定缩放的线性 HDR，最终输出时才把曝光应用到画面。
                GraphicsBuffer temporalExposure = null;
                float temporalExposureMultiplier = 1f;
                if (!sceneIsLinearMask)
                {
                    if (postConfiguration.Active)
                        temporalExposure = m_passes.PostProcess.GetTemporalExposure(camera, output.Color, viewport,
                            postConfiguration, out temporalExposureMultiplier);
                    else
                        temporalExposureMultiplier = MaxColorOutputPass.GetLegacyExposureMultiplier(camera, timeOfDay, configuration.ColorOutput);
                }

                // 用运动和几何深度对齐上一帧。六向云提供天空响应标记和内容版本，分别控制混合与历史有效性。
                resolvedColor = m_passes.TemporalAA.ResolveOutput(context, camera, temporal, output.Color, resources.Depth,
                    configuration.TemporalAA, drawGizmos, m_passes.SixWayCloud.ContentRevision, taaDebug, temporalExposure,
                    temporalExposureMultiplier);
            }

            MaxPostProcessPass.Output postOutput;
            using (MaxRenderProfiler.PostProcess.Auto())
            {
                // 从 TAA 之后的画面测光和提取 Bloom。TAA 调试视图会把场景色替换为遮罩，
                // 此时从真正的解析历史测光并跳过 Bloom，切换诊断时曝光仍跟随正常画面。
                RenderTexture meterSource = showTaaDebug ? temporal.Colors[temporal.Index] : resolvedColor;
                postOutput = m_passes.PostProcess.Prepare(context, camera, meterSource, viewport,
                    postConfiguration, configuration.ColorOutput, sceneIsLinearMask, exposureOnly: showTaaDebug);
            }

#if UNITY_EDITOR
            // PreImageEffects Gizmos 在 TAA、曝光统计和 Bloom 准备之后绘制到 HDR 目标。
            if (drawGizmos)
            {
                using var gizmosScope = MaxRenderProfiler.EditorGizmos.Auto();
                context.SetupCameraProperties(camera);
                ShaderBindings.SetPerCameraShaderVariables(context, camera, true);
                using var gizmos = new GpuPassScope(context, "Gizmos After Temporal AA");
                gizmos.Buffer.SetRenderTarget(resolvedColor, resources.Depth);
                gizmos.Buffer.SetViewport(viewport);
                gizmos.Enqueue();
                context.DrawGizmos(camera, GizmoSubset.PreImageEffects);
            }
#endif

            // 从离屏 HDR 目标拷出颜色和深度，按配置合成 Bloom、应用曝光和色调映射。
            // 输出变换在此执行一次。使用目标上的相机视口，保留共享目标中其他相机的画面。
            using (MaxRenderProfiler.ColorOutput.Auto()) m_passes.ColorOutput.Execute(context, camera, resolvedColor, resources.Depth, target, outputDepth,
                configuration.ColorOutput, showLinearMask, postOutput, timeOfDay, viewport);
#if UNITY_EDITOR
            if (drawGizmos)
            {
                using var gizmosScope = MaxRenderProfiler.EditorGizmos.Auto();
                context.SetupCameraProperties(camera);
                ShaderBindings.SetPerCameraShaderVariables(context, camera, MaxColorOutputPass.IsTextureDestination(camera));
                // PostImageEffects Gizmos 直接绘制在最终目标上，使用该相机的输出视口。
                using var gizmos = new GpuPassScope(context, "Gizmos After Color Output");
                gizmos.Buffer.SetViewport(CameraViewport.GetViewport(camera));
                gizmos.Enqueue();
                context.DrawGizmos(camera, GizmoSubset.PostImageEffects);
            }
#endif

            // 收尾时让参与者安排虚拟纹理反馈回读。回读与写入反馈的 GBuffer 绘制使用同一渲染上下文，
            // 因此回读排在写入之后。外层在这些命令入队后提交，再开始处理下一台相机。
            using (MaxRenderProfiler.EndParticipants.Auto()) MaxRenderBridge.EndCamera(context, camera);
        }

        // 取消订阅历史重置事件，并释放管线资源和光照贴图状态。
        protected override void Dispose(bool disposing)
        {
            MaxRenderBridge.TemporalHistoryInvalidated -= ResetTemporalHistory;
#if UNITY_EDITOR
            EditorApplication.update -= RequestStreamRender;
            AssemblyReloadEvents.beforeAssemblyReload -= m_timeOfDay.DisposeStreams;
            EditorApplication.playModeStateChanged -= OnPlayModeChanged;
#endif

            m_timeOfDay.Dispose();
            m_targets.Dispose();
            m_passes.Dispose();
            MaxLightmapping.Reset();
            base.Dispose(disposing);
        }
    }
}

using System;
using System.Runtime.CompilerServices;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using UnityEngine.Experimental.Rendering;

namespace Unagi.Dofs
{
    // TODO: when msaa disabled, doesn't work
    // TODO: support 2021
    [DisallowMultipleRendererFeature("Depth Of Fields Renderer Feature")]
    public class DepthOfFieldsRendererFeature : ScriptableRendererFeature
    {
        [NonSerialized] private DepthOfFieldsRenderPass _depthOfFieldsRenderPass;

        public override void Create()
        {
            _depthOfFieldsRenderPass = new DepthOfFieldsRenderPass();
            _depthOfFieldsRenderPass.renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing;
        }

        public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
        {
            if (!renderingData.cameraData.camera.TryGetComponent(out DepthOfFieldsComponent component))
                return;

            if (!_depthOfFieldsRenderPass.Setup(renderer, renderingData.cameraData.cameraTargetDescriptor, component))
                return;

            renderer.EnqueuePass(_depthOfFieldsRenderPass);
        }

        protected override void Dispose(bool disposing)
        {
            _depthOfFieldsRenderPass?.Dispose();
            _depthOfFieldsRenderPass = null;
        }
    }

    internal class DepthOfFieldsRenderPass : ScriptableRenderPass
    {
        RTHandle m_FullCoCTexture;
        RTHandle m_PingTexture;
        RTHandle m_PongTexture;

        private ProfilingSampler m_ProfilingSampler = new ProfilingSampler(nameof(DepthOfFieldsRenderPass));
        private ProfilingSampler m_DofProfileSampler;

        ScriptableRenderer m_Renderer;
        RenderTextureDescriptor m_Descriptor;

        private DepthOfFieldsComponent m_Component;

        Material bokehDepthOfField;

        Vector4[] m_BokehKernel;
        int m_BokehHash;
        // Needed if the device changes its render target width/height (ex, Mobile platform allows change of orientation)
        float m_BokehMaxRadius;
        float m_BokehRCPAspect;

        public DepthOfFieldsRenderPass()
        {
            m_DofProfileSampler = new ProfilingSampler("Depth Of Field");

            Shader dofShader = Shader.Find("Hidden/Universal Render Pipeline/BokehDepthOfField");

            if (dofShader != null)
                bokehDepthOfField = CoreUtils.CreateEngineMaterial(dofShader);
        }

        public bool Setup(ScriptableRenderer renderer, in RenderTextureDescriptor baseDescriptor, DepthOfFieldsComponent component)
        {
            m_Renderer = renderer;
            m_Descriptor = baseDescriptor;
            m_Descriptor.useMipMap = false;
            m_Descriptor.autoGenerateMips = false;

            m_Component = component;

            return m_Component == null ? false : m_Component.active;
        }

        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            if (bokehDepthOfField == null)
                return;

            var cmd = CommandBufferPool.Get();
            using (new ProfilingScope(cmd, m_ProfilingSampler))
            {
                Render(cmd, ref renderingData);
            }
            context.ExecuteCommandBuffer(cmd);
            CommandBufferPool.Release(cmd);
        }

        void Render(CommandBuffer cmd, ref RenderingData renderingData)
        {
            ref CameraData cameraData = ref renderingData.cameraData;
            ref ScriptableRenderer renderer = ref cameraData.renderer;

            RTHandle source = m_Renderer.cameraColorTargetHandle;
            RTHandle GetSource() => source;

            using (new ProfilingScope(cmd, m_DofProfileSampler))
            {
                if (UniversalRenderPipeline.asset.msaaSampleCount > 1)
                    DoDepthOfField(cameraData.camera, cmd, GetSource(), GetSource(), cameraData.camera.pixelRect);
            }
        }

        void DoDepthOfField(Camera camera, CommandBuffer cmd, RTHandle source, RTHandle destination, Rect pixelRect)
        {
            DoBokehDepthOfField(cmd, source, destination, camera);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float GetMaxBokehRadiusInPixels(float viewportHeight)
        {
            // Estimate the maximum radius of bokeh (empirically derived from the ring count)
            const float kRadiusInPixels = 14f;
            return Mathf.Min(0.05f, kRadiusInPixels / viewportHeight);
        }

        void PrepareBokehKernel(float maxRadius, float rcpAspect)
        {
            const int kRings = 4;
            const int kPointsPerRing = 7;

            // Check the existing array
            if (m_BokehKernel == null)
                m_BokehKernel = new Vector4[42];

            // Fill in sample points (concentric circles transformed to rotated N-Gon)
            int idx = 0;
            float bladeCount = m_Component.bladeCount;
            float curvature = 1f - m_Component.bladeCurvature;
            float rotation = m_Component.bladeRotation * Mathf.Deg2Rad;
            const float PI = Mathf.PI;
            const float TWO_PI = Mathf.PI * 2f;

            for (int ring = 1; ring < kRings; ring++)
            {
                float bias = 1f / kPointsPerRing;
                float radius = (ring + bias) / (kRings - 1f + bias);
                int points = ring * kPointsPerRing;

                for (int point = 0; point < points; point++)
                {
                    // Angle on ring
                    float phi = 2f * PI * point / points;

                    // Transform to rotated N-Gon
                    // Adapted from "CryEngine 3 Graphics Gems" [Sousa13]
                    float nt = Mathf.Cos(PI / bladeCount);
                    float dt = Mathf.Cos(phi - (TWO_PI / bladeCount) * Mathf.Floor((bladeCount * phi + Mathf.PI) / TWO_PI));
                    float r = radius * Mathf.Pow(nt / dt, curvature);
                    float u = r * Mathf.Cos(phi - rotation);
                    float v = r * Mathf.Sin(phi - rotation);

                    float uRadius = u * maxRadius;
                    float vRadius = v * maxRadius;
                    float uRadiusPowTwo = uRadius * uRadius;
                    float vRadiusPowTwo = vRadius * vRadius;
                    float kernelLength = Mathf.Sqrt((uRadiusPowTwo + vRadiusPowTwo));
                    float uRCP = uRadius * rcpAspect;

                    m_BokehKernel[idx] = new Vector4(uRadius, vRadius, kernelLength, uRCP);
                    idx++;
                }
            }
        }

        void DoBokehDepthOfField(CommandBuffer cmd, RTHandle source, RTHandle destination, Camera camera)
        {
            int downSample = m_Component.downSample;
            var material = bokehDepthOfField;
            int wh = m_Descriptor.width / downSample;
            int hh = m_Descriptor.height / downSample;

            // "A Lens and Aperture Camera Model for Synthetic Image Generation" [Potmesil81]
            float F = camera.focalLength / 1000f;
            float A = camera.focalLength / m_Component.aperture;
            float P = m_Component.autoFocus ?
                        m_Component.focusTransform != null ?
                        Mathf.Abs(camera.worldToCameraMatrix.MultiplyPoint(m_Component.focusTransform.position).z) :
                        m_Component.focusDistance :
                        m_Component.focusDistance;
            float maxCoC = (A * F) / (P - F);
            float maxRadius = GetMaxBokehRadiusInPixels(m_Descriptor.height);
            float rcpAspect = 1f / (wh / (float)hh);

            //CoreUtils.SetKeyword(material, ShaderKeywordStrings.UseFastSRGBLinearConversion, m_UseFastSRGBLinearConversion);
            cmd.SetGlobalVector(ShaderConstants._CoCParams, new Vector4(P, maxCoC, maxRadius, rcpAspect));

            // Prepare the bokeh kernel constant buffer
            int hash = m_Component.GetHashCode();
            if (hash != m_BokehHash || maxRadius != m_BokehMaxRadius || rcpAspect != m_BokehRCPAspect)
            {
                m_BokehHash = hash;
                m_BokehMaxRadius = maxRadius;
                m_BokehRCPAspect = rcpAspect;
                PrepareBokehKernel(maxRadius, rcpAspect);
            }

            cmd.SetGlobalVectorArray(ShaderConstants._BokehKernel, m_BokehKernel);

            RenderingUtils.ReAllocateIfNeeded(ref m_FullCoCTexture, GetCompatibleDescriptor(m_Descriptor.width, m_Descriptor.height, GraphicsFormat.R8_UNorm), FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_FullCoCTexture");
            RenderingUtils.ReAllocateIfNeeded(ref m_PingTexture, GetCompatibleDescriptor(wh, hh, GraphicsFormat.R16G16B16A16_SFloat), FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_PingTexture");
            RenderingUtils.ReAllocateIfNeeded(ref m_PongTexture, GetCompatibleDescriptor(wh, hh, GraphicsFormat.R16G16B16A16_SFloat), FilterMode.Bilinear, TextureWrapMode.Clamp, name: "_PongTexture");

            SetSourceSize(cmd, m_Descriptor);
            cmd.SetGlobalVector(ShaderConstants._DownSampleScaleFactor, new Vector4(1.0f / downSample, 1.0f / downSample, downSample, downSample));
            float uvMargin = (1.0f / m_Descriptor.height) * downSample;
            cmd.SetGlobalVector(ShaderConstants._BokehConstants, new Vector4(uvMargin, uvMargin * 2.0f));

            // Compute CoC
            Blitter.BlitCameraTexture(cmd, source, m_FullCoCTexture, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, material, 0);
            cmd.SetGlobalTexture(ShaderConstants._FullCoCTexture, m_FullCoCTexture.nameID);

            // Downscale & prefilter color + coc
            Blitter.BlitCameraTexture(cmd, source, m_PingTexture, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, material, 1);

            // Bokeh blur
            Blitter.BlitCameraTexture(cmd, m_PingTexture, m_PongTexture, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, material, 2);

            // Post-filtering
            Blitter.BlitCameraTexture(cmd, m_PongTexture, m_PingTexture, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, material, 3);

            // Composite
            cmd.SetGlobalTexture(ShaderConstants._DofTexture, m_PingTexture.nameID);
            Blitter.BlitCameraTexture(cmd, source, destination, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, material, 4);
        }

        public void Dispose()
        {
            m_FullCoCTexture?.Release();
            m_PingTexture?.Release();
            m_PongTexture?.Release();

            if (bokehDepthOfField != null)
            {
                CoreUtils.Destroy(bokehDepthOfField);
                bokehDepthOfField = null;
            }
        }

        RenderTextureDescriptor GetCompatibleDescriptor(int width, int height, GraphicsFormat format, DepthBits depthBufferBits = DepthBits.None)
            => GetCompatibleDescriptor(m_Descriptor, width, height, format, depthBufferBits);

        internal static RenderTextureDescriptor GetCompatibleDescriptor(RenderTextureDescriptor desc, int width, int height, GraphicsFormat format, DepthBits depthBufferBits = DepthBits.None)
        {
            desc.depthBufferBits = (int)depthBufferBits;
            desc.msaaSamples = 1;
            desc.width = width;
            desc.height = height;
            desc.graphicsFormat = format;
            return desc;
        }

        internal static void SetSourceSize(CommandBuffer cmd, RenderTextureDescriptor desc)
        {
            float width = desc.width;
            float height = desc.height;
            if (desc.useDynamicScale)
            {
                width *= ScalableBufferManager.widthScaleFactor;
                height *= ScalableBufferManager.heightScaleFactor;
            }
            cmd.SetGlobalVector(ShaderConstants._SourceSize, new Vector4(width, height, 1.0f / width, 1.0f / height));
        }

        static class ShaderConstants
        {
            public static readonly int _FullCoCTexture = Shader.PropertyToID("_FullCoCTexture");
            public static readonly int _HalfCoCTexture = Shader.PropertyToID("_HalfCoCTexture");
            public static readonly int _DofTexture = Shader.PropertyToID("_DofTexture");
            public static readonly int _CoCParams = Shader.PropertyToID("_CoCParams");
            public static readonly int _BokehKernel = Shader.PropertyToID("_BokehKernel");
            public static readonly int _BokehConstants = Shader.PropertyToID("_BokehConstants");
            public static readonly int _PongTexture = Shader.PropertyToID("_PongTexture");
            public static readonly int _PingTexture = Shader.PropertyToID("_PingTexture");

            public static readonly int _DownSampleScaleFactor = Shader.PropertyToID("_DownSampleScaleFactor");

            public static readonly int _SourceSize = Shader.PropertyToID("_SourceSize");
        }
    }
}

using System.Runtime.CompilerServices;
using UnityEngine.Experimental.Rendering;

namespace UnityEngine.Rendering.Universal
{
    // TODO: xmldoc
    public interface IPostProcessComponent
    {
        bool IsActive();
        bool IsTileCompatible();
    }
}

namespace UnityEngine.Rendering.Universal.Internal
{
    // TODO: TAA
    // TODO: Motion blur
    /// <summary>
    /// Renders the post-processing effect stack.
    /// </summary>
    public class PostProcessPass : ScriptableRenderPass
    {
        static readonly RTHandle k_CameraTarget = RTHandles.Alloc(BuiltinRenderTextureType.CameraTarget);
        static readonly RTHandle k_CurrentActive = RTHandles.Alloc(BuiltinRenderTextureType.CurrentActive);

        RenderTextureDescriptor m_Descriptor;
        RTHandle m_Source;
        RTHandle m_Destination;
        RTHandle m_Depth;
        RTHandle m_InternalLut;
        RTHandle m_TempTarget;
        RTHandle m_TempTarget2;
        RTHandle[] m_BloomMipDown;
        RTHandle[] m_BloomMipUp;
        RTHandle m_FullCoCTextureGaussian;
        RTHandle m_HalfCoCTextureGaussian;
        RTHandle m_PingTextureGaussian;
        RTHandle m_PongTextureGaussian;
        RTHandle m_FullCoCTextureBokeh;
        RTHandle m_PingTextureBokeh;
        RTHandle m_PongTextureBokeh;

        const string k_RenderPostProcessingTag = "Render PostProcessing Effects";
        const string k_RenderFinalPostProcessingTag = "Render Final PostProcessing Pass";
        private static readonly ProfilingSampler m_ProfilingRenderPostProcessing = new ProfilingSampler(k_RenderPostProcessingTag);
        private static readonly ProfilingSampler m_ProfilingRenderFinalPostProcessing = new ProfilingSampler(k_RenderFinalPostProcessingTag);

        MaterialLibrary m_Materials;
        PostProcessData m_Data;

        // Builtin effects settings
        DepthOfField m_DepthOfField;
        MotionBlur m_MotionBlur;
        PaniniProjection m_PaniniProjection;
        Bloom m_Bloom;
        LensDistortion m_LensDistortion;
        ChromaticAberration m_ChromaticAberration;
        Vignette m_Vignette;
        ColorLookup m_ColorLookup;
        ColorAdjustments m_ColorAdjustments;
        Tonemapping m_Tonemapping;
        FilmGrain m_FilmGrain;

        // Misc
        const int k_MaxPyramidSize = 16;
        readonly GraphicsFormat m_DefaultHDRFormat;
        bool m_UseRGBM;
        readonly GraphicsFormat m_SMAAEdgeFormat;
        readonly GraphicsFormat m_GaussianCoCFormat;
        Matrix4x4[] m_PrevViewProjM = new Matrix4x4[2];
        bool m_ResetHistory;
        int m_DitheringTextureIndex;
        RenderTargetIdentifier[] m_MRT2;
        Vector4[] m_BokehKernel;
        int m_BokehHash;
        // Needed if the device changes its render target width/height (ex, Mobile platform allows change of orientation)
        float m_BokehMaxRadius;
        float m_BokehRCPAspect;

        // True when this is the very last pass in the pipeline
        bool m_IsFinalPass;

        // If there's a final post process pass after this pass.
        // If yes, Film Grain and Dithering are setup in the final pass, otherwise they are setup in this pass.
        bool m_HasFinalPass;

        // Some Android devices do not support sRGB backbuffer
        // We need to do the conversion manually on those
        bool m_EnableSRGBConversionIfNeeded;

        // Option to use procedural draw instead of cmd.blit
        bool m_UseDrawProcedural;

        // Use Fast conversions between SRGB and Linear
        bool m_UseFastSRGBLinearConversion;

        // Render final blit to screen instead of color backbuffer
        private bool m_ResolveToScreen;

        Material m_BlitMaterial;

        public PostProcessPass(RenderPassEvent evt, PostProcessData data, Material blitMaterial)
        {
            base.profilingSampler = new ProfilingSampler(nameof(PostProcessPass));
            renderPassEvent = evt;
            m_Data = data;
            m_Materials = new MaterialLibrary(data);
            m_BlitMaterial = blitMaterial;

            // Texture format pre-lookup
            if (SystemInfo.IsFormatSupported(GraphicsFormat.B10G11R11_UFloatPack32, FormatUsage.Linear | FormatUsage.Render))
            {
                m_DefaultHDRFormat = GraphicsFormat.B10G11R11_UFloatPack32;
                m_UseRGBM = false;
            }
            else
            {
                m_DefaultHDRFormat = QualitySettings.activeColorSpace == ColorSpace.Linear
                    ? GraphicsFormat.R8G8B8A8_SRGB
                    : GraphicsFormat.R8G8B8A8_UNorm;
                m_UseRGBM = true;
            }

            // Only two components are needed for edge render texture, but on some vendors four components may be faster.
            if (SystemInfo.IsFormatSupported(GraphicsFormat.R8G8_UNorm, FormatUsage.Render) && SystemInfo.graphicsDeviceVendor.ToLowerInvariant().Contains("arm"))
                m_SMAAEdgeFormat = GraphicsFormat.R8G8_UNorm;
            else
                m_SMAAEdgeFormat = GraphicsFormat.R8G8B8A8_UNorm;

            if (SystemInfo.IsFormatSupported(GraphicsFormat.R16_UNorm, FormatUsage.Linear | FormatUsage.Render))
                m_GaussianCoCFormat = GraphicsFormat.R16_UNorm;
            else if (SystemInfo.IsFormatSupported(GraphicsFormat.R16_SFloat, FormatUsage.Linear | FormatUsage.Render))
                m_GaussianCoCFormat = GraphicsFormat.R16_SFloat;
            else // Expect CoC banding
                m_GaussianCoCFormat = GraphicsFormat.R8_UNorm;

            // Bloom pyramid shader ids - can't use a simple stackalloc in the bloom function as we
            // unfortunately need to allocate strings
            ShaderConstants._BloomMipUp = new int[k_MaxPyramidSize];
            ShaderConstants._BloomMipDown = new int[k_MaxPyramidSize];

            for (int i = 0; i < k_MaxPyramidSize; i++)
            {
                ShaderConstants._BloomMipUp[i] = Shader.PropertyToID("_BloomMipUp" + i);
                ShaderConstants._BloomMipDown[i] = Shader.PropertyToID("_BloomMipDown" + i);
            }

            m_MRT2 = new RenderTargetIdentifier[2];
            m_ResetHistory = true;
            base.useNativeRenderPass = false;

            m_TempTarget = RTHandles.Alloc(
                Vector2.one,
                depthBufferBits: DepthBits.None,
                colorFormat: m_DefaultHDRFormat,
                filterMode: FilterMode.Bilinear,
                wrapMode: TextureWrapMode.Clamp,
                dimension: TextureDimension.Tex2D,
                enableRandomWrite: false,
                useMipMap: false,
                autoGenerateMips: false,
                bindTextureMS: false,
                useDynamicScale: false,
                memoryless: RenderTextureMemoryless.None,
                name: "_TempTarget");
            m_TempTarget2 = RTHandles.Alloc(
                Vector2.one,
                depthBufferBits: DepthBits.None,
                colorFormat: m_DefaultHDRFormat,
                filterMode: FilterMode.Bilinear,
                wrapMode: TextureWrapMode.Clamp,
                dimension: TextureDimension.Tex2D,
                enableRandomWrite: false,
                useMipMap: false,
                autoGenerateMips: false,
                bindTextureMS: false,
                useDynamicScale: false,
                memoryless: RenderTextureMemoryless.None,
                name: "_TempTarget2");

            Vector2 mipScale = Vector2.one;
            var mipCount = 16;
            m_BloomMipDown = new RTHandle[mipCount];
            m_BloomMipUp = new RTHandle[mipCount];
            for (uint i = 0; i < mipCount; ++i)
            {
                mipScale *= 0.5f;
                m_BloomMipDown[i] = RTHandles.Alloc(
                    mipScale,
                    depthBufferBits: DepthBits.None,
                    colorFormat: m_DefaultHDRFormat,
                    filterMode: FilterMode.Bilinear,
                    wrapMode: TextureWrapMode.Clamp,
                    dimension: TextureDimension.Tex2D,
                    enableRandomWrite: false,
                    useMipMap: false,
                    autoGenerateMips: false,
                    bindTextureMS: false,
                    useDynamicScale: false,
                    memoryless: RenderTextureMemoryless.None,
                    name: $"_BloomMipDown{i}");
                m_BloomMipUp[i] = RTHandles.Alloc(
                    mipScale,
                    depthBufferBits: DepthBits.None,
                    colorFormat: m_DefaultHDRFormat,
                    filterMode: FilterMode.Bilinear,
                    wrapMode: TextureWrapMode.Clamp,
                    dimension: TextureDimension.Tex2D,
                    enableRandomWrite: false,
                    useMipMap: false,
                    autoGenerateMips: false,
                    bindTextureMS: false,
                    useDynamicScale: false,
                    memoryless: RenderTextureMemoryless.None,
                    name: $"_BloomMipUp{i}");
            }
        }

        public void Cleanup()
        {
            m_Materials.Cleanup();
            m_TempTarget.Release();
            m_TempTarget2.Release();
            foreach (var handle in m_BloomMipDown)
            {
                handle.Release();
            }
            foreach (var handle in m_BloomMipUp)
            {
                handle.Release();
            }
            m_FullCoCTextureGaussian?.Release();
            m_HalfCoCTextureGaussian?.Release();
            m_PingTextureGaussian?.Release();
            m_PongTextureGaussian?.Release();
            m_FullCoCTextureBokeh?.Release();
            m_PingTextureBokeh?.Release();
            m_PongTextureBokeh?.Release();

        }

        public void Setup(in RenderTextureDescriptor baseDescriptor, in RTHandle depth, in RTHandle internalLut, bool hasFinalPass, bool resolvePostProcessingToCameraTarget)
        {
            m_Descriptor = baseDescriptor;
            m_Descriptor.useMipMap = false;
            m_Descriptor.autoGenerateMips = false;
            m_ResolveToScreen = resolvePostProcessingToCameraTarget;
            m_Depth = depth;
            m_InternalLut = internalLut;
            m_IsFinalPass = false;
            m_HasFinalPass = hasFinalPass;
            m_EnableSRGBConversionIfNeeded = resolvePostProcessingToCameraTarget;
        }

        public void SetupFinalPass(in RTHandle source, bool resolvePostProcessingToCameraTarget)
        {
            m_Source = source;
            m_Destination = k_CameraTarget;
            m_ResolveToScreen = true;
            m_IsFinalPass = true;
            m_HasFinalPass = false;
            m_EnableSRGBConversionIfNeeded = true;
            m_ResolveToScreen = resolvePostProcessingToCameraTarget;
        }

        /// <inheritdoc/>
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
            // If RenderTargetHandle already has a valid internal render target identifier, we shouldn't request a temp
        }

        /// <inheritdoc/>
        public override void OnCameraCleanup(CommandBuffer cmd)
        {
            // Logic here matches the if check in OnCameraSetup
        }

        public void ResetHistory()
        {
            m_ResetHistory = true;
        }

        public bool CanRunOnTile()
        {
            // Check builtin & user effects here
            return false;
        }

        /// <inheritdoc/>
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            var renderer = renderingData.cameraData.renderer;
            m_Source = renderer.cameraColorTarget;
            m_Destination = renderer.cameraColorFrontBuffer;

            // Start by pre-fetching all builtin effect settings we need
            // Some of the color-grading settings are only used in the color grading lut pass
            var stack = VolumeManager.instance.stack;
            m_DepthOfField        = stack.GetComponent<DepthOfField>();
            m_MotionBlur          = stack.GetComponent<MotionBlur>();
            m_PaniniProjection    = stack.GetComponent<PaniniProjection>();
            m_Bloom               = stack.GetComponent<Bloom>();
            m_LensDistortion      = stack.GetComponent<LensDistortion>();
            m_ChromaticAberration = stack.GetComponent<ChromaticAberration>();
            m_Vignette            = stack.GetComponent<Vignette>();
            m_ColorLookup         = stack.GetComponent<ColorLookup>();
            m_ColorAdjustments    = stack.GetComponent<ColorAdjustments>();
            m_Tonemapping         = stack.GetComponent<Tonemapping>();
            m_FilmGrain           = stack.GetComponent<FilmGrain>();
            m_UseDrawProcedural   = renderingData.cameraData.xr.enabled;
            m_UseFastSRGBLinearConversion = renderingData.postProcessingData.useFastSRGBLinearConversion;

            if (m_IsFinalPass)
            {
                var cmd = CommandBufferPool.Get();
                using (new ProfilingScope(cmd, m_ProfilingRenderFinalPostProcessing))
                {
                    RenderFinalPass(cmd, ref renderingData);
                }

                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }
            else if (CanRunOnTile())
            {
                // TODO: Add a fast render path if only on-tile compatible effects are used and we're actually running on a platform that supports it
                // Note: we can still work on-tile if FXAA is enabled, it'd be part of the final pass
            }
            else
            {
                // Regular render path (not on-tile) - we do everything in a single command buffer as it
                // makes it easier to manage temporary targets' lifetime
                var cmd = CommandBufferPool.Get();
                using (new ProfilingScope(cmd, m_ProfilingRenderPostProcessing))
                {
                    Render(cmd, ref renderingData);
                }

                context.ExecuteCommandBuffer(cmd);
                CommandBufferPool.Release(cmd);
            }

            m_ResetHistory = false;
        }

        RenderTextureDescriptor GetCompatibleDescriptor()
            => GetCompatibleDescriptor(m_Descriptor.width, m_Descriptor.height, m_Descriptor.graphicsFormat);

        RenderTextureDescriptor GetCompatibleDescriptor(int width, int height, GraphicsFormat format, int depthBufferBits = 0)
        {
            var desc = m_Descriptor;
            desc.depthBufferBits = depthBufferBits;
            desc.msaaSamples = 1;
            desc.width = width;
            desc.height = height;
            desc.graphicsFormat = format;
            return desc;
        }

        bool RequireSRGBConversionBlitToBackBuffer(CameraData cameraData)
        {
            return cameraData.requireSrgbConversion && m_EnableSRGBConversionIfNeeded;
        }

        private new void Blit(CommandBuffer cmd, RTHandle source, RTHandle destination, Material material, int passIndex = 0)
        {
            cmd.SetGlobalTexture(ShaderPropertyId.sourceTex, source);
            if (m_UseDrawProcedural)
            {
                Vector4 scaleBias = new Vector4(1, 1, 0, 0);
                cmd.SetGlobalVector(ShaderPropertyId.scaleBias, scaleBias);

                CoreUtils.SetRenderTarget(cmd, destination, ClearFlag.None);
                cmd.DrawProcedural(Matrix4x4.identity, material, passIndex, MeshTopology.Quads, 4, 1, null);
            }
            else
            {
                cmd.Blit(source.nameID, destination.nameID, material, passIndex);
            }
        }

        private void Blit(CommandBuffer cmd, RenderTargetIdentifier source, RenderTargetIdentifier destination, Material material, int passIndex = 0)
        {
            cmd.SetGlobalTexture(ShaderPropertyId.sourceTex, source);
            if (m_UseDrawProcedural)
            {
                Vector4 scaleBias = new Vector4(1, 1, 0, 0);
                cmd.SetGlobalVector(ShaderPropertyId.scaleBias, scaleBias);

                cmd.SetRenderTarget(new RenderTargetIdentifier(destination, 0, CubemapFace.Unknown, -1),
                    RenderBufferLoadAction.Load, RenderBufferStoreAction.Store, RenderBufferLoadAction.Load, RenderBufferStoreAction.Store);
                cmd.DrawProcedural(Matrix4x4.identity, material, passIndex, MeshTopology.Quads, 4, 1, null);
            }
            else
            {
                cmd.Blit(source, destination, material, passIndex);
            }
        }

        private void DrawFullscreenMesh(CommandBuffer cmd, Material material, int passIndex)
        {
            if (m_UseDrawProcedural)
            {
                Vector4 scaleBias = new Vector4(1, 1, 0, 0);
                cmd.SetGlobalVector(ShaderPropertyId.scaleBias, scaleBias);
                cmd.DrawProcedural(Matrix4x4.identity, material, passIndex, MeshTopology.Quads, 4, 1, null);
            }
            else
            {
                cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity, material, 0, passIndex);
            }
        }

        void Render(CommandBuffer cmd, ref RenderingData renderingData)
        {
            ref var cameraData = ref renderingData.cameraData;
            var renderer = cameraData.renderer;
            // Don't use these directly unless you have a good reason to, use GetSource() and
            // GetDestination() instead
            RTHandle source = m_Source;
            RTHandle destination = m_Destination;
            bool isSceneViewCamera = cameraData.isSceneViewCamera;

            // Utilities to simplify intermediate target management
            RTHandle GetSource() => source;

            RTHandle GetDestination()
            {
                return m_Destination;
            }

            void Swap()
            {
                renderer.SwapColorBuffer();
                source = renderer.cameraColorTarget;
                m_Destination = renderer.cameraColorFrontBuffer;
            }

            // Setup projection matrix for cmd.DrawMesh()
            cmd.SetGlobalMatrix(ShaderConstants._FullscreenProjMat, GL.GetGPUProjectionMatrix(Matrix4x4.identity, true));

            //Automatic resolve of MSAA targets results in a buffer that is completely filled in the right size.
            //This means that we want to ignore the RTHandleScaling when we are actually using the resolved buffer as input
            if(cameraData.cameraTargetDescriptor.msaaSamples == (int)MSAASamples.None)
            {
                cmd.SetGlobalVector(ShaderConstants._RTHandleScale, RTHandles.rtHandleProperties.rtHandleScale);
            }
            else
            {
                cmd.SetGlobalVector(ShaderConstants._RTHandleScale, Vector4.one);
            }

            // Optional NaN killer before post-processing kicks in
            // stopNaN may be null on Adreno 3xx. It doesn't support full shader level 3.5, but SystemInfo.graphicsShaderLevel is 35.
            if (cameraData.isStopNaNEnabled && m_Materials.stopNaN != null)
            {
                using (new ProfilingScope(cmd, ProfilingSampler.Get(URPProfileId.StopNaNs)))
                {
                    RenderingUtils.Blit(cmd, GetSource(), GetDestination(), m_Materials.stopNaN, 0, m_UseDrawProcedural);

                    Swap();
                }
            }

            // Anti-aliasing
            if (cameraData.antialiasing == AntialiasingMode.SubpixelMorphologicalAntiAliasing && SystemInfo.graphicsDeviceType != GraphicsDeviceType.OpenGLES2)
            {
                using (new ProfilingScope(cmd, ProfilingSampler.Get(URPProfileId.SMAA)))
                {
                    DoSubpixelMorphologicalAntialiasing(ref cameraData, cmd, GetSource(), GetDestination());
                    Swap();
                }
            }

            // Depth of Field
            if (m_DepthOfField.IsActive() && !isSceneViewCamera)
            {
                var markerName = m_DepthOfField.mode.value == DepthOfFieldMode.Gaussian
                    ? URPProfileId.GaussianDepthOfField
                    : URPProfileId.BokehDepthOfField;

                using (new ProfilingScope(cmd, ProfilingSampler.Get(markerName)))
                {
                    DoDepthOfField(cameraData.camera, cmd, GetSource(), GetDestination(), cameraData.pixelRect);
                    Swap();
                }
            }

            // Motion blur
            if (m_MotionBlur.IsActive() && !isSceneViewCamera)
            {
                using (new ProfilingScope(cmd, ProfilingSampler.Get(URPProfileId.MotionBlur)))
                {
                    DoMotionBlur(cameraData, cmd, GetSource(), GetDestination());
                    Swap();
                }
            }

            // Panini projection is done as a fullscreen pass after all depth-based effects are done
            // and before bloom kicks in
            if (m_PaniniProjection.IsActive() && !isSceneViewCamera)
            {
                using (new ProfilingScope(cmd, ProfilingSampler.Get(URPProfileId.PaniniProjection)))
                {
                    DoPaniniProjection(cameraData.camera, cmd, GetSource(), GetDestination());
                    Swap();
                }
            }

            // Combined post-processing stack
            using (new ProfilingScope(cmd, ProfilingSampler.Get(URPProfileId.UberPostProcess)))
            {
                // Reset uber keywords
                m_Materials.uber.shaderKeywords = null;

                // Bloom goes first
                bool bloomActive = m_Bloom.IsActive();
                if (bloomActive)
                {
                    using (new ProfilingScope(cmd, ProfilingSampler.Get(URPProfileId.Bloom)))
                        SetupBloom(cmd, GetSource(), m_Materials.uber);
                }

                // Setup other effects constants
                SetupLensDistortion(m_Materials.uber, isSceneViewCamera);
                SetupChromaticAberration(m_Materials.uber);
                SetupVignette(m_Materials.uber);
                SetupColorGrading(cmd, ref renderingData, m_Materials.uber);

                // Only apply dithering & grain if there isn't a final pass.
                SetupGrain(cameraData, m_Materials.uber);
                SetupDithering(cameraData, m_Materials.uber);

                if (RequireSRGBConversionBlitToBackBuffer(cameraData))
                    m_Materials.uber.EnableKeyword(ShaderKeywordStrings.LinearToSRGBConversion);

                if (m_UseFastSRGBLinearConversion)
                {
                    m_Materials.uber.EnableKeyword(ShaderKeywordStrings.UseFastSRGBLinearConversion);
                }

                // Done with Uber, blit it
                cmd.SetGlobalTexture(ShaderPropertyId.sourceTex, GetSource());

                var colorLoadAction = RenderBufferLoadAction.DontCare;
                if (m_Destination.nameID == BuiltinRenderTextureType.CameraTarget && !cameraData.isDefaultViewport)
                    colorLoadAction = RenderBufferLoadAction.Load;

                // Note: We rendering to "camera target" we need to get the cameraData.targetTexture as this will get the targetTexture of the camera stack.
                // Overlay cameras need to output to the target described in the base camera while doing camera stack.
                RenderTargetIdentifier cameraTarget;
                //We want to stay in the scaled version unless we are blitting to screen
                if (m_ResolveToScreen)
                {
                    cameraTarget = (cameraData.camera.targetTexture != null) ? new RenderTargetIdentifier(cameraData.targetTexture) : BuiltinRenderTextureType.CameraTarget;
                    cmd.SetGlobalVector(ShaderConstants._RTHandleScale, RTHandles.rtHandleProperties.rtHandleScale);
                }
                else
                {
                    cameraTarget = m_Destination;
                    cmd.SetGlobalVector(ShaderConstants._RTHandleScale, Vector4.one);
                }

#if ENABLE_VR && ENABLE_XR_MODULE
                if (cameraData.xr.enabled)
                {
                    CoreUtils.SetRenderTarget(cmd, cameraTarget, colorLoadAction, RenderBufferStoreAction.Store, ClearFlag.None, Color.black);

                    if (m_Destination.nameID == BuiltinRenderTextureType.CameraTarget)
                        cmd.SetViewport(cameraData.pixelRect);
                    bool isRenderToBackBufferTarget = cameraTarget == cameraData.xr.renderTarget && !cameraData.xr.renderTargetIsRenderTexture;
                    if (isRenderToBackBufferTarget)
                        cmd.SetViewport(cameraData.pixelRect);
                    // We y-flip if
                    // 1) we are bliting from render texture to back buffer and
                    // 2) renderTexture starts UV at top
                    bool yflip = isRenderToBackBufferTarget && SystemInfo.graphicsUVStartsAtTop;
                    Vector4 scaleBias = yflip ? new Vector4(1, -1, 0, 1) : new Vector4(1, 1, 0, 0);
                    cmd.SetGlobalVector(ShaderPropertyId.scaleBias, scaleBias);
                    cmd.DrawProcedural(Matrix4x4.identity, m_Materials.uber, 0, MeshTopology.Quads, 4, 1, null);
                }
                else
#endif
                {
                    cmd.SetRenderTarget(cameraTarget, colorLoadAction, RenderBufferStoreAction.Store, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.DontCare);
                    cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                    
                    cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity, m_Materials.uber);
                    cmd.SetViewProjectionMatrices(cameraData.camera.worldToCameraMatrix, cameraData.camera.projectionMatrix);
                }

                //If not resolving to screen then swap the color buffers
                if (!m_ResolveToScreen)
                {
                    renderer.SwapColorBuffer();
                }
            }
        }

        private RTHandle BlitDstDiscardContent(CommandBuffer cmd, RTHandle rt)
        {
            // We set depth to DontCare because rt might be the source of PostProcessing used as a temporary target
            // Source typically comes with a depth buffer and right now we don't have a way to only bind the color attachment of a RenderTargetIdentifier
            CoreUtils.SetRenderTarget(cmd, rt, ClearFlag.None);
            return rt;
        }

        private BuiltinRenderTextureType BlitDstDiscardContent(CommandBuffer cmd, RenderTargetIdentifier rt)
        {
            // We set depth to DontCare because rt might be the source of PostProcessing used as a temporary target
            // Source typically comes with a depth buffer and right now we don't have a way to only bind the color attachment of a RenderTargetIdentifier
            cmd.SetRenderTarget(new RenderTargetIdentifier(rt, 0, CubemapFace.Unknown, -1),
                RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store,
                RenderBufferLoadAction.DontCare, RenderBufferStoreAction.DontCare);
            return BuiltinRenderTextureType.CurrentActive;
        }

        #region Sub-pixel Morphological Anti-aliasing

        void DoSubpixelMorphologicalAntialiasing(ref CameraData cameraData, CommandBuffer cmd, RenderTargetIdentifier source, RTHandle destination)
        {
            var camera = cameraData.camera;
            var pixelRect = cameraData.pixelRect;
            var material = m_Materials.subpixelMorphologicalAntialiasing;
            const int kStencilBit = 64;

            // Globals
            material.SetVector(ShaderConstants._Metrics, new Vector4(1f / m_Descriptor.width, 1f / m_Descriptor.height, m_Descriptor.width, m_Descriptor.height));
            material.SetTexture(ShaderConstants._AreaTexture, m_Data.textures.smaaAreaTex);
            material.SetTexture(ShaderConstants._SearchTexture, m_Data.textures.smaaSearchTex);
            material.SetFloat(ShaderConstants._StencilRef, (float)kStencilBit);
            material.SetFloat(ShaderConstants._StencilMask, (float)kStencilBit);

            // Quality presets
            material.shaderKeywords = null;

            switch (cameraData.antialiasingQuality)
            {
                case AntialiasingQuality.Low: material.EnableKeyword(ShaderKeywordStrings.SmaaLow);
                    break;
                case AntialiasingQuality.Medium: material.EnableKeyword(ShaderKeywordStrings.SmaaMedium);
                    break;
                case AntialiasingQuality.High: material.EnableKeyword(ShaderKeywordStrings.SmaaHigh);
                    break;
            }

            // Intermediate targets
            RenderTargetIdentifier stencil; // We would only need stencil, no depth. But Unity doesn't support that.
            int tempDepthBits;
            if (m_Depth == (RenderTargetIdentifier)BuiltinRenderTextureType.CameraTarget || m_Descriptor.msaaSamples > 1)
            {
                // In case m_Depth is CameraTarget it may refer to the backbuffer and we can't use that as an attachment on all platforms
                stencil = ShaderConstants._EdgeTexture;
                tempDepthBits = 24;
            }
            else
            {
                stencil = m_Depth;
                tempDepthBits = 0;
            }
            cmd.GetTemporaryRT(ShaderConstants._EdgeTexture, GetCompatibleDescriptor(m_Descriptor.width, m_Descriptor.height, m_SMAAEdgeFormat, tempDepthBits), FilterMode.Point);
            cmd.GetTemporaryRT(ShaderConstants._BlendTexture, GetCompatibleDescriptor(m_Descriptor.width, m_Descriptor.height, GraphicsFormat.R8G8B8A8_UNorm), FilterMode.Point);

            // Prepare for manual blit
            cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
            cmd.SetViewport(pixelRect);

            // Pass 1: Edge detection
            cmd.SetRenderTarget(new RenderTargetIdentifier(ShaderConstants._EdgeTexture, 0, CubemapFace.Unknown, -1),
                RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, stencil,
                RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store);
            cmd.ClearRenderTarget(RTClearFlags.ColorStencil, Color.clear, 1.0f, 0);
            cmd.SetGlobalTexture(ShaderConstants._ColorTexture, source);
            DrawFullscreenMesh(cmd, material, 0);

            // Pass 2: Blend weights
            cmd.SetRenderTarget(new RenderTargetIdentifier(ShaderConstants._BlendTexture, 0, CubemapFace.Unknown, -1),
                RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store, stencil,
                RenderBufferLoadAction.Load, RenderBufferStoreAction.DontCare);
            cmd.ClearRenderTarget(false, true, Color.clear);
            cmd.SetGlobalTexture(ShaderConstants._ColorTexture, ShaderConstants._EdgeTexture);
            DrawFullscreenMesh(cmd, material, 1);

            // Pass 3: Neighborhood blending
            cmd.SetRenderTarget(new RenderTargetIdentifier(destination, 0, CubemapFace.Unknown, -1),
                RenderBufferLoadAction.DontCare, RenderBufferStoreAction.Store,
                RenderBufferLoadAction.DontCare, RenderBufferStoreAction.DontCare);
            cmd.SetGlobalTexture(ShaderConstants._ColorTexture, source);
            cmd.SetGlobalTexture(ShaderConstants._BlendTexture, ShaderConstants._BlendTexture);
            DrawFullscreenMesh(cmd, material, 2);

            // Cleanup
            cmd.ReleaseTemporaryRT(ShaderConstants._EdgeTexture);
            cmd.ReleaseTemporaryRT(ShaderConstants._BlendTexture);
            cmd.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);
        }

        #endregion

        #region Depth Of Field

        // TODO: CoC reprojection once TAA gets in LW
        // TODO: Proper LDR/gamma support
        void DoDepthOfField(Camera camera, CommandBuffer cmd, RTHandle source, RTHandle destination, Rect pixelRect)
        {
            if (m_DepthOfField.mode.value == DepthOfFieldMode.Gaussian)
                DoGaussianDepthOfField(camera, cmd, source, destination, pixelRect);
            else if (m_DepthOfField.mode.value == DepthOfFieldMode.Bokeh)
                DoBokehDepthOfField(cmd, source, destination, pixelRect);
        }

        void DoGaussianDepthOfField(Camera camera, CommandBuffer cmd, RTHandle source, RTHandle destination, Rect pixelRect)
        {
            int downSample = 2;
            Vector2 scaleFactor = Vector2.one / downSample;
            var material = m_Materials.gaussianDepthOfField;
            int wh = m_Descriptor.width / downSample;
            int hh = m_Descriptor.height / downSample;
            float farStart = m_DepthOfField.gaussianStart.value;
            float farEnd = Mathf.Max(farStart, m_DepthOfField.gaussianEnd.value);

            // Assumes a radius of 1 is 1 at 1080p
            // Past a certain radius our gaussian kernel will look very bad so we'll clamp it for
            // very high resolutions (4K+).
            float maxRadius = m_DepthOfField.gaussianMaxRadius.value * (wh / 1080f);
            maxRadius = Mathf.Min(maxRadius, 2f);

            CoreUtils.SetKeyword(material, ShaderKeywordStrings.HighQualitySampling, m_DepthOfField.highQualitySampling.value);
            material.SetVector(ShaderConstants._CoCParams, new Vector3(farStart, farEnd, maxRadius));

            // Lazy Allocated RTHandles
            if (m_FullCoCTextureGaussian == null)
            {
                m_FullCoCTextureGaussian = RTHandles.Alloc(
                    Vector2.one,
                    colorFormat: m_GaussianCoCFormat,
                    filterMode: FilterMode.Bilinear,
                    wrapMode: TextureWrapMode.Clamp,
                    dimension: m_Descriptor.dimension,
                    memoryless: m_Descriptor.memoryless,
                    name: "_FullCoCTexture");
            }
            if (m_HalfCoCTextureGaussian == null)
            {
                m_HalfCoCTextureGaussian = RTHandles.Alloc(
                    scaleFactor,
                    colorFormat: m_GaussianCoCFormat,
                    filterMode: FilterMode.Bilinear,
                    wrapMode: TextureWrapMode.Clamp,
                    dimension: m_Descriptor.dimension,
                    memoryless: m_Descriptor.memoryless,
                    name: "_HalfCoCTexture");
            }
            if (m_PingTextureGaussian == null)
            {
                m_PingTextureGaussian = RTHandles.Alloc(
                    scaleFactor,
                    colorFormat: m_DefaultHDRFormat,
                    filterMode: FilterMode.Bilinear,
                    wrapMode: TextureWrapMode.Clamp,
                    dimension: m_Descriptor.dimension,
                    memoryless: m_Descriptor.memoryless,
                    name: "_PingTexture");
            }
            if (m_PongTextureGaussian == null)
            {
                m_PongTextureGaussian = RTHandles.Alloc(
                    scaleFactor,
                    colorFormat: m_DefaultHDRFormat,
                    filterMode: FilterMode.Bilinear,
                    wrapMode: TextureWrapMode.Clamp,
                    dimension: m_Descriptor.dimension,
                    memoryless: m_Descriptor.memoryless,
                    name: "_PongTexture");
            }

            PostProcessUtils.SetSourceSize(cmd, m_Descriptor);
            cmd.SetGlobalVector(ShaderConstants._DownSampleScaleFactor, new Vector4(scaleFactor.x, scaleFactor.y, downSample, downSample));

            // Compute CoC
            Blit(cmd, source, m_FullCoCTextureGaussian, material, 0);

            // Downscale & prefilter color + coc
            m_MRT2[0] = m_HalfCoCTextureGaussian;
            m_MRT2[1] = m_PingTextureGaussian;

            cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
            cmd.SetViewport(pixelRect);
            cmd.SetGlobalTexture(ShaderConstants._ColorTexture, source);
            cmd.SetGlobalTexture(ShaderConstants._FullCoCTexture, m_FullCoCTextureGaussian);
            cmd.SetRenderTarget(m_MRT2, m_HalfCoCTextureGaussian, 0, CubemapFace.Unknown, -1);
            DrawFullscreenMesh(cmd, material, 1);

            cmd.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);

            // Blur
            cmd.SetGlobalTexture(ShaderConstants._HalfCoCTexture, m_HalfCoCTextureGaussian);
            Blit(cmd, m_PingTextureGaussian, m_PongTextureGaussian, material, 2);
            Blit(cmd, m_PongTextureGaussian, BlitDstDiscardContent(cmd, m_PingTextureGaussian), material, 3);

            // Composite
            cmd.SetGlobalTexture(ShaderConstants._ColorTexture, m_PingTextureGaussian);
            cmd.SetGlobalTexture(ShaderConstants._FullCoCTexture, m_FullCoCTextureGaussian);
            Blit(cmd, source, BlitDstDiscardContent(cmd, destination), material, 4);
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
            float bladeCount = m_DepthOfField.bladeCount.value;
            float curvature = 1f - m_DepthOfField.bladeCurvature.value;
            float rotation = m_DepthOfField.bladeRotation.value * Mathf.Deg2Rad;
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

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        static float GetMaxBokehRadiusInPixels(float viewportHeight)
        {
            // Estimate the maximum radius of bokeh (empirically derived from the ring count)
            const float kRadiusInPixels = 14f;
            return Mathf.Min(0.05f, kRadiusInPixels / viewportHeight);
        }

        void DoBokehDepthOfField(CommandBuffer cmd, RTHandle source, RTHandle destination, Rect pixelRect)
        {
            int downSample = 2;
            Vector2 scaleFactor = Vector2.one / downSample;
            var material = m_Materials.bokehDepthOfField;
            int wh = m_Descriptor.width / downSample;
            int hh = m_Descriptor.height / downSample;

            // "A Lens and Aperture Camera Model for Synthetic Image Generation" [Potmesil81]
            float F = m_DepthOfField.focalLength.value / 1000f;
            float A = m_DepthOfField.focalLength.value / m_DepthOfField.aperture.value;
            float P = m_DepthOfField.focusDistance.value;
            float maxCoC = (A * F) / (P - F);
            float maxRadius = GetMaxBokehRadiusInPixels(m_Descriptor.height);
            float rcpAspect = 1f / (wh / (float)hh);

            CoreUtils.SetKeyword(material, ShaderKeywordStrings.UseFastSRGBLinearConversion, m_UseFastSRGBLinearConversion);
            cmd.SetGlobalVector(ShaderConstants._CoCParams, new Vector4(P, maxCoC, maxRadius, rcpAspect));

            // Prepare the bokeh kernel constant buffer
            int hash = m_DepthOfField.GetHashCode();
            if (hash != m_BokehHash || maxRadius != m_BokehMaxRadius || rcpAspect != m_BokehRCPAspect)
            {
                m_BokehHash = hash;
                m_BokehMaxRadius = maxRadius;
                m_BokehRCPAspect = rcpAspect;
                PrepareBokehKernel(maxRadius, rcpAspect);
            }

            cmd.SetGlobalVectorArray(ShaderConstants._BokehKernel, m_BokehKernel);

            // Lazy Allocated RTHandles
            if (m_FullCoCTextureBokeh == null)
            {
                m_FullCoCTextureBokeh = RTHandles.Alloc(
                    Vector2.one,
                    colorFormat: GraphicsFormat.R8_UNorm,
                    filterMode: FilterMode.Bilinear,
                    wrapMode: TextureWrapMode.Clamp,
                    dimension: m_Descriptor.dimension,
                    memoryless: m_Descriptor.memoryless,
                    name: "_FullCoCTexture");
            }
            if (m_PingTextureBokeh == null)
            {
                m_PingTextureBokeh = RTHandles.Alloc(
                    scaleFactor,
                    colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
                    filterMode: FilterMode.Bilinear,
                    wrapMode: TextureWrapMode.Clamp,
                    dimension: m_Descriptor.dimension,
                    memoryless: m_Descriptor.memoryless,
                    name: "_PingTexture");
            }
            if (m_PongTextureBokeh == null)
            {
                m_PongTextureBokeh = RTHandles.Alloc(
                    scaleFactor,
                    colorFormat: GraphicsFormat.R16G16B16A16_SFloat,
                    filterMode: FilterMode.Bilinear,
                    wrapMode: TextureWrapMode.Clamp,
                    dimension: m_Descriptor.dimension,
                    memoryless: m_Descriptor.memoryless,
                    name: "_PongTexture");
            }

            PostProcessUtils.SetSourceSize(cmd, m_Descriptor);
            cmd.SetGlobalVector(ShaderConstants._DownSampleScaleFactor, new Vector4(scaleFactor.x, scaleFactor.y, downSample, downSample));
            float uvMargin = (1.0f / m_Descriptor.height) * downSample;
            cmd.SetGlobalVector(ShaderConstants._BokehConstants, new Vector4(uvMargin, uvMargin * 2.0f));

            // Compute CoC
            RenderingUtils.Blit(cmd, source, m_FullCoCTextureBokeh, material, 0);
            cmd.SetGlobalTexture(ShaderConstants._FullCoCTexture, m_FullCoCTextureBokeh);

            // Downscale & prefilter color + coc
            Blit(cmd, source, m_PingTextureBokeh, material, 1);

            // Bokeh blur
            Blit(cmd, m_PingTextureBokeh, m_PongTextureBokeh, material, 2);

            // Post-filtering
            Blit(cmd, m_PongTextureBokeh, BlitDstDiscardContent(cmd, m_PingTextureBokeh), material, 3);

            // Composite
            cmd.SetGlobalTexture(ShaderConstants._DofTexture, m_PingTextureBokeh);
            Blit(cmd, source, BlitDstDiscardContent(cmd, destination), material, 4);
        }

        #endregion

        #region Motion Blur
#if ENABLE_VR && ENABLE_XR_MODULE
        // Hold the stereo matrices to avoid allocating arrays every frame
        internal static readonly Matrix4x4[] viewProjMatrixStereo = new Matrix4x4[2];
#endif
        void DoMotionBlur(CameraData cameraData, CommandBuffer cmd, RTHandle source, RTHandle destination)
        {
            var material = m_Materials.cameraMotionBlur;

#if ENABLE_VR && ENABLE_XR_MODULE
            if (cameraData.xr.enabled && cameraData.xr.singlePassEnabled)
            {
                var viewProj0 = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(0), true) * cameraData.GetViewMatrix(0);
                var viewProj1 = GL.GetGPUProjectionMatrix(cameraData.GetProjectionMatrix(1), true) * cameraData.GetViewMatrix(1);
                if (m_ResetHistory)
                {
                    viewProjMatrixStereo[0] = viewProj0;
                    viewProjMatrixStereo[1] = viewProj1;
                    material.SetMatrixArray("_PrevViewProjMStereo", viewProjMatrixStereo);
                }
                else
                    material.SetMatrixArray("_PrevViewProjMStereo", m_PrevViewProjM);

                m_PrevViewProjM[0] = viewProj0;
                m_PrevViewProjM[1] = viewProj1;
            }
            else
#endif
            {
                int prevViewProjMIdx = 0;
#if ENABLE_VR && ENABLE_XR_MODULE
                if (cameraData.xr.enabled)
                    prevViewProjMIdx = cameraData.xr.multipassId;
#endif
                // This is needed because Blit will reset viewproj matrices to identity and UniversalRP currently
                // relies on SetupCameraProperties instead of handling its own matrices.
                // TODO: We need get rid of SetupCameraProperties and setup camera matrices in Universal
                var proj = cameraData.GetProjectionMatrix();
                var view = cameraData.GetViewMatrix();
                var viewProj = proj * view;

                material.SetMatrix("_ViewProjM", viewProj);

                if (m_ResetHistory)
                    material.SetMatrix("_PrevViewProjM", viewProj);
                else
                    material.SetMatrix("_PrevViewProjM", m_PrevViewProjM[prevViewProjMIdx]);

                m_PrevViewProjM[prevViewProjMIdx] = viewProj;
            }

            material.SetFloat("_Intensity", m_MotionBlur.intensity.value);
            material.SetFloat("_Clamp", m_MotionBlur.clamp.value);

            PostProcessUtils.SetSourceSize(cmd, m_Descriptor);

            Blit(cmd, source, BlitDstDiscardContent(cmd, destination), material, (int)m_MotionBlur.quality.value);
        }

        #endregion

        #region Panini Projection

        // Back-ported & adapted from the work of the Stockholm demo team - thanks Lasse!
        void DoPaniniProjection(Camera camera, CommandBuffer cmd, RTHandle source, RTHandle destination)
        {
            float distance = m_PaniniProjection.distance.value;
            var viewExtents = CalcViewExtents(camera);
            var cropExtents = CalcCropExtents(camera, distance);

            float scaleX = cropExtents.x / viewExtents.x;
            float scaleY = cropExtents.y / viewExtents.y;
            float scaleF = Mathf.Min(scaleX, scaleY);

            float paniniD = distance;
            float paniniS = Mathf.Lerp(1f, Mathf.Clamp01(scaleF), m_PaniniProjection.cropToFit.value);

            var material = m_Materials.paniniProjection;
            material.SetVector(ShaderConstants._Params, new Vector4(viewExtents.x, viewExtents.y, paniniD, paniniS));
            material.EnableKeyword(
                1f - Mathf.Abs(paniniD) > float.Epsilon
                ? ShaderKeywordStrings.PaniniGeneric : ShaderKeywordStrings.PaniniUnitDistance
            );

            Blit(cmd, source, BlitDstDiscardContent(cmd, destination), material);
        }

        Vector2 CalcViewExtents(Camera camera)
        {
            float fovY = camera.fieldOfView * Mathf.Deg2Rad;
            float aspect = m_Descriptor.width / (float)m_Descriptor.height;

            float viewExtY = Mathf.Tan(0.5f * fovY);
            float viewExtX = aspect * viewExtY;

            return new Vector2(viewExtX, viewExtY);
        }

        Vector2 CalcCropExtents(Camera camera, float d)
        {
            // given
            //    S----------- E--X-------
            //    |    `  ~.  /,´
            //    |-- ---    Q
            //    |        ,/    `
            //  1 |      ,´/       `
            //    |    ,´ /         ´
            //    |  ,´  /           ´
            //    |,`   /             ,
            //    O    /
            //    |   /               ,
            //  d |  /
            //    | /                ,
            //    |/                .
            //    P
            //    |              ´
            //    |         , ´
            //    +-    ´
            //
            // have X
            // want to find E

            float viewDist = 1f + d;

            var projPos = CalcViewExtents(camera);
            var projHyp = Mathf.Sqrt(projPos.x * projPos.x + 1f);

            float cylDistMinusD = 1f / projHyp;
            float cylDist = cylDistMinusD + d;
            var cylPos = projPos * cylDistMinusD;

            return cylPos * (viewDist / cylDist);
        }

        #endregion

        #region Bloom

        void SetupBloom(CommandBuffer cmd, RTHandle source, Material uberMaterial)
        {
            // Start at half-res
            int tw = m_Descriptor.width >> 1;
            int th = m_Descriptor.height >> 1;

            // Determine the iteration count
            int maxSize = Mathf.Max(tw, th);
            int iterations = Mathf.FloorToInt(Mathf.Log(maxSize, 2f) - 1);
            iterations -= m_Bloom.skipIterations.value;
            int mipCount = Mathf.Clamp(iterations, 1, k_MaxPyramidSize);

            // Pre-filtering parameters
            float clamp = m_Bloom.clamp.value;
            float threshold = Mathf.GammaToLinearSpace(m_Bloom.threshold.value);
            float thresholdKnee = threshold * 0.5f; // Hardcoded soft knee

            // Material setup
            float scatter = Mathf.Lerp(0.05f, 0.95f, m_Bloom.scatter.value);
            var bloomMaterial = m_Materials.bloom;
            bloomMaterial.SetVector(ShaderConstants._Params, new Vector4(scatter, clamp, threshold, thresholdKnee));
            CoreUtils.SetKeyword(bloomMaterial, ShaderKeywordStrings.BloomHQ, m_Bloom.highQualityFiltering.value);
            CoreUtils.SetKeyword(bloomMaterial, ShaderKeywordStrings.UseRGBM, m_UseRGBM);

            // Prefilter
            Blit(cmd, source, m_BloomMipDown[0], bloomMaterial, 0);

            // Downsample - gaussian pyramid
            var lastDown = m_BloomMipDown[0];
            for (int i = 1; i < mipCount; i++)
            {
                var mipDown = m_BloomMipDown[i];
                var mipUp = m_BloomMipUp[i];

                // Classic two pass gaussian blur - use mipUp as a temporary target
                //   First pass does 2x downsampling + 9-tap gaussian
                //   Second pass does 9-tap gaussian using a 5-tap filter + bilinear filtering
                Blit(cmd, lastDown, mipUp, bloomMaterial, 1);
                Blit(cmd, mipUp, mipDown, bloomMaterial, 2);

                lastDown = mipDown;
            }

            // Upsample (bilinear by default, HQ filtering does bicubic instead
            for (int i = mipCount - 2; i >= 0; i--)
            {
                var lowMip = (i == mipCount - 2) ? m_BloomMipDown[i + 1] : m_BloomMipUp[i + 1];
                var highMip = m_BloomMipDown[i];
                var dst = m_BloomMipUp[i];

                cmd.SetGlobalTexture(ShaderConstants._SourceTexLowMip, lowMip);
                Blit(cmd, highMip, BlitDstDiscardContent(cmd, dst), bloomMaterial, 3);
            }

            // Setup bloom on uber
            var tint = m_Bloom.tint.value.linear;
            var luma = ColorUtils.Luminance(tint);
            tint = luma > 0f ? tint * (1f / luma) : Color.white;

            var bloomParams = new Vector4(m_Bloom.intensity.value, tint.r, tint.g, tint.b);
            uberMaterial.SetVector(ShaderConstants._Bloom_Params, bloomParams);
            uberMaterial.SetFloat(ShaderConstants._Bloom_RGBM, m_UseRGBM ? 1f : 0f);

            cmd.SetGlobalTexture(ShaderConstants._Bloom_Texture, m_BloomMipUp[0]);

            // Setup lens dirtiness on uber
            // Keep the aspect ratio correct & center the dirt texture, we don't want it to be
            // stretched or squashed
            var dirtTexture = m_Bloom.dirtTexture.value == null ? Texture2D.blackTexture : m_Bloom.dirtTexture.value;
            float dirtRatio = dirtTexture.width / (float)dirtTexture.height;
            float screenRatio = m_Descriptor.width / (float)m_Descriptor.height;
            var dirtScaleOffset = new Vector4(1f, 1f, 0f, 0f);
            float dirtIntensity = m_Bloom.dirtIntensity.value;

            if (dirtRatio > screenRatio)
            {
                dirtScaleOffset.x = screenRatio / dirtRatio;
                dirtScaleOffset.z = (1f - dirtScaleOffset.x) * 0.5f;
            }
            else if (screenRatio > dirtRatio)
            {
                dirtScaleOffset.y = dirtRatio / screenRatio;
                dirtScaleOffset.w = (1f - dirtScaleOffset.y) * 0.5f;
            }

            uberMaterial.SetVector(ShaderConstants._LensDirt_Params, dirtScaleOffset);
            uberMaterial.SetFloat(ShaderConstants._LensDirt_Intensity, dirtIntensity);
            uberMaterial.SetTexture(ShaderConstants._LensDirt_Texture, dirtTexture);

            // Keyword setup - a bit convoluted as we're trying to save some variants in Uber...
            if (m_Bloom.highQualityFiltering.value)
                uberMaterial.EnableKeyword(dirtIntensity > 0f ? ShaderKeywordStrings.BloomHQDirt : ShaderKeywordStrings.BloomHQ);
            else
                uberMaterial.EnableKeyword(dirtIntensity > 0f ? ShaderKeywordStrings.BloomLQDirt : ShaderKeywordStrings.BloomLQ);
        }

        #endregion

        #region Lens Distortion

        void SetupLensDistortion(Material material, bool isSceneView)
        {
            float amount = 1.6f * Mathf.Max(Mathf.Abs(m_LensDistortion.intensity.value * 100f), 1f);
            float theta = Mathf.Deg2Rad * Mathf.Min(160f, amount);
            float sigma = 2f * Mathf.Tan(theta * 0.5f);
            var center = m_LensDistortion.center.value * 2f - Vector2.one;
            var p1 = new Vector4(
                center.x,
                center.y,
                Mathf.Max(m_LensDistortion.xMultiplier.value, 1e-4f),
                Mathf.Max(m_LensDistortion.yMultiplier.value, 1e-4f)
            );
            var p2 = new Vector4(
                m_LensDistortion.intensity.value >= 0f ? theta : 1f / theta,
                sigma,
                1f / m_LensDistortion.scale.value,
                m_LensDistortion.intensity.value * 100f
            );

            material.SetVector(ShaderConstants._Distortion_Params1, p1);
            material.SetVector(ShaderConstants._Distortion_Params2, p2);

            if (m_LensDistortion.IsActive() && !isSceneView)
                material.EnableKeyword(ShaderKeywordStrings.Distortion);
        }

        #endregion

        #region Chromatic Aberration

        void SetupChromaticAberration(Material material)
        {
            material.SetFloat(ShaderConstants._Chroma_Params, m_ChromaticAberration.intensity.value * 0.05f);

            if (m_ChromaticAberration.IsActive())
                material.EnableKeyword(ShaderKeywordStrings.ChromaticAberration);
        }

        #endregion

        #region Vignette

        void SetupVignette(Material material)
        {
            var color = m_Vignette.color.value;
            var center = m_Vignette.center.value;
            var aspectRatio = m_Descriptor.width / (float)m_Descriptor.height;

            var v1 = new Vector4(
                color.r, color.g, color.b,
                m_Vignette.rounded.value ? aspectRatio : 1f
            );
            var v2 = new Vector4(
                center.x, center.y,
                m_Vignette.intensity.value * 3f,
                m_Vignette.smoothness.value * 5f
            );

            material.SetVector(ShaderConstants._Vignette_Params1, v1);
            material.SetVector(ShaderConstants._Vignette_Params2, v2);
        }

        #endregion

        #region Color Grading

        void SetupColorGrading(CommandBuffer cmd, ref RenderingData renderingData, Material material)
        {
            ref var postProcessingData = ref renderingData.postProcessingData;
            bool hdr = postProcessingData.gradingMode == ColorGradingMode.HighDynamicRange;
            int lutHeight = postProcessingData.lutSize;
            int lutWidth = lutHeight * lutHeight;

            // Source material setup
            float postExposureLinear = Mathf.Pow(2f, m_ColorAdjustments.postExposure.value);
            cmd.SetGlobalTexture(ShaderConstants._InternalLut, m_InternalLut);
            material.SetVector(ShaderConstants._Lut_Params, new Vector4(1f / lutWidth, 1f / lutHeight, lutHeight - 1f, postExposureLinear));
            material.SetTexture(ShaderConstants._UserLut, m_ColorLookup.texture.value);
            material.SetVector(ShaderConstants._UserLut_Params, !m_ColorLookup.IsActive()
                ? Vector4.zero
                : new Vector4(1f / m_ColorLookup.texture.value.width,
                    1f / m_ColorLookup.texture.value.height,
                    m_ColorLookup.texture.value.height - 1f,
                    m_ColorLookup.contribution.value)
            );

            if (hdr)
            {
                material.EnableKeyword(ShaderKeywordStrings.HDRGrading);
            }
            else
            {
                switch (m_Tonemapping.mode.value)
                {
                    case TonemappingMode.Neutral: material.EnableKeyword(ShaderKeywordStrings.TonemapNeutral); break;
                    case TonemappingMode.ACES: material.EnableKeyword(ShaderKeywordStrings.TonemapACES); break;
                    default: break; // None
                }
            }
        }

        #endregion

        #region Film Grain

        void SetupGrain(in CameraData cameraData, Material material)
        {
            if (!m_HasFinalPass && m_FilmGrain.IsActive())
            {
                material.EnableKeyword(ShaderKeywordStrings.FilmGrain);
                PostProcessUtils.ConfigureFilmGrain(
                    m_Data,
                    m_FilmGrain,
                    cameraData.pixelWidth, cameraData.pixelHeight,
                    material
                );
            }
        }

        #endregion

        #region 8-bit Dithering

        void SetupDithering(in CameraData cameraData, Material material)
        {
            if (!m_HasFinalPass && cameraData.isDitheringEnabled)
            {
                material.EnableKeyword(ShaderKeywordStrings.Dithering);
                m_DitheringTextureIndex = PostProcessUtils.ConfigureDithering(
                    m_Data,
                    m_DitheringTextureIndex,
                    cameraData.pixelWidth, cameraData.pixelHeight,
                    material
                );
            }
        }

        #endregion

        #region Final pass

        void RenderFinalPass(CommandBuffer cmd, ref RenderingData renderingData)
        {
            ref var cameraData = ref renderingData.cameraData;
            var material = m_Materials.finalPass;
            material.shaderKeywords = null;

            // FXAA setup
            if (cameraData.antialiasing == AntialiasingMode.FastApproximateAntialiasing)
                material.EnableKeyword(ShaderKeywordStrings.Fxaa);

            PostProcessUtils.SetSourceSize(cmd, cameraData.cameraTargetDescriptor);

            SetupGrain(cameraData, material);
            SetupDithering(cameraData, material);

            if (RequireSRGBConversionBlitToBackBuffer(cameraData))
                material.EnableKeyword(ShaderKeywordStrings.LinearToSRGBConversion);

            cmd.SetGlobalTexture(ShaderPropertyId.sourceTex, m_Source);
            cmd.SetGlobalVector(ShaderConstants._RTHandleScale, RTHandles.rtHandleProperties.rtHandleScale);

            var colorLoadAction = cameraData.isDefaultViewport ? RenderBufferLoadAction.DontCare : RenderBufferLoadAction.Load;

#if ENABLE_VR && ENABLE_XR_MODULE
            if (cameraData.xr.enabled)
            {
                RenderTargetIdentifier cameraTarget = cameraData.xr.renderTarget;

                //Blit(cmd, m_Source.Identifier(), BuiltinRenderTextureType.CurrentActive, material);
                bool isRenderToBackBufferTarget = cameraTarget == cameraData.xr.renderTarget && !cameraData.xr.renderTargetIsRenderTexture;
                // We y-flip if
                // 1) we are bliting from render texture to back buffer and
                // 2) renderTexture starts UV at top
                bool yflip = isRenderToBackBufferTarget && SystemInfo.graphicsUVStartsAtTop;

                Vector4 scaleBias = yflip ? new Vector4(1, -1, 0, 1) : new Vector4(1, 1, 0, 0);

                cmd.SetRenderTarget(cameraTarget, colorLoadAction, RenderBufferStoreAction.Store, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.DontCare);
                cmd.SetViewport(cameraData.pixelRect);
                cmd.SetGlobalVector(ShaderPropertyId.scaleBias, scaleBias);
                cmd.DrawProcedural(Matrix4x4.identity, material, 0, MeshTopology.Quads, 4, 1, null);
            }
            else
#endif
            {
                // Note: We need to get the cameraData.targetTexture as this will get the targetTexture of the camera stack.
                // Overlay cameras need to output to the target described in the base camera while doing camera stack.
                RenderTargetIdentifier cameraTarget = (cameraData.camera.targetTexture != null) ? new RenderTargetIdentifier(cameraData.targetTexture) : BuiltinRenderTextureType.CameraTarget;

                cmd.SetRenderTarget(cameraTarget, colorLoadAction, RenderBufferStoreAction.Store, RenderBufferLoadAction.DontCare, RenderBufferStoreAction.DontCare);
                cmd.SetViewProjectionMatrices(Matrix4x4.identity, Matrix4x4.identity);
                cmd.SetViewport(cameraData.pixelRect);
                cmd.DrawMesh(RenderingUtils.fullscreenMesh, Matrix4x4.identity, material);
                cmd.SetViewProjectionMatrices(cameraData.camera.worldToCameraMatrix, cameraData.camera.projectionMatrix);
            }
        }

        #endregion

        #region Internal utilities

        class MaterialLibrary
        {
            public readonly Material stopNaN;
            public readonly Material subpixelMorphologicalAntialiasing;
            public readonly Material gaussianDepthOfField;
            public readonly Material bokehDepthOfField;
            public readonly Material cameraMotionBlur;
            public readonly Material paniniProjection;
            public readonly Material bloom;
            public readonly Material uber;
            public readonly Material finalPass;

            public MaterialLibrary(PostProcessData data)
            {
                stopNaN = Load(data.shaders.stopNanPS);
                subpixelMorphologicalAntialiasing = Load(data.shaders.subpixelMorphologicalAntialiasingPS);
                gaussianDepthOfField = Load(data.shaders.gaussianDepthOfFieldPS);
                bokehDepthOfField = Load(data.shaders.bokehDepthOfFieldPS);
                cameraMotionBlur = Load(data.shaders.cameraMotionBlurPS);
                paniniProjection = Load(data.shaders.paniniProjectionPS);
                bloom = Load(data.shaders.bloomPS);
                uber = Load(data.shaders.uberPostPS);
                finalPass = Load(data.shaders.finalPostPassPS);
            }

            Material Load(Shader shader)
            {
                if (shader == null)
                {
                    Debug.LogErrorFormat($"Missing shader. {GetType().DeclaringType.Name} render pass will not execute. Check for missing reference in the renderer resources.");
                    return null;
                }
                else if (!shader.isSupported)
                {
                    return null;
                }

                return CoreUtils.CreateEngineMaterial(shader);
            }

            internal void Cleanup()
            {
                CoreUtils.Destroy(stopNaN);
                CoreUtils.Destroy(subpixelMorphologicalAntialiasing);
                CoreUtils.Destroy(gaussianDepthOfField);
                CoreUtils.Destroy(bokehDepthOfField);
                CoreUtils.Destroy(cameraMotionBlur);
                CoreUtils.Destroy(paniniProjection);
                CoreUtils.Destroy(bloom);
                CoreUtils.Destroy(uber);
                CoreUtils.Destroy(finalPass);
            }
        }

        // Precomputed shader ids to same some CPU cycles (mostly affects mobile)
        static class ShaderConstants
        {
            public static readonly int _TempTarget         = Shader.PropertyToID("_TempTarget");
            public static readonly int _TempTarget2        = Shader.PropertyToID("_TempTarget2");

            public static readonly int _StencilRef         = Shader.PropertyToID("_StencilRef");
            public static readonly int _StencilMask        = Shader.PropertyToID("_StencilMask");

            public static readonly int _FullCoCTexture     = Shader.PropertyToID("_FullCoCTexture");
            public static readonly int _HalfCoCTexture     = Shader.PropertyToID("_HalfCoCTexture");
            public static readonly int _DofTexture         = Shader.PropertyToID("_DofTexture");
            public static readonly int _CoCParams          = Shader.PropertyToID("_CoCParams");
            public static readonly int _BokehKernel        = Shader.PropertyToID("_BokehKernel");
            public static readonly int _BokehConstants     = Shader.PropertyToID("_BokehConstants");
            public static readonly int _PongTexture        = Shader.PropertyToID("_PongTexture");
            public static readonly int _PingTexture        = Shader.PropertyToID("_PingTexture");

            public static readonly int _Metrics            = Shader.PropertyToID("_Metrics");
            public static readonly int _AreaTexture        = Shader.PropertyToID("_AreaTexture");
            public static readonly int _SearchTexture      = Shader.PropertyToID("_SearchTexture");
            public static readonly int _EdgeTexture        = Shader.PropertyToID("_EdgeTexture");
            public static readonly int _BlendTexture       = Shader.PropertyToID("_BlendTexture");

            public static readonly int _ColorTexture       = Shader.PropertyToID("_ColorTexture");
            public static readonly int _Params             = Shader.PropertyToID("_Params");
            public static readonly int _SourceTexLowMip    = Shader.PropertyToID("_SourceTexLowMip");
            public static readonly int _Bloom_Params       = Shader.PropertyToID("_Bloom_Params");
            public static readonly int _Bloom_RGBM         = Shader.PropertyToID("_Bloom_RGBM");
            public static readonly int _Bloom_Texture      = Shader.PropertyToID("_Bloom_Texture");
            public static readonly int _LensDirt_Texture   = Shader.PropertyToID("_LensDirt_Texture");
            public static readonly int _LensDirt_Params    = Shader.PropertyToID("_LensDirt_Params");
            public static readonly int _LensDirt_Intensity = Shader.PropertyToID("_LensDirt_Intensity");
            public static readonly int _Distortion_Params1 = Shader.PropertyToID("_Distortion_Params1");
            public static readonly int _Distortion_Params2 = Shader.PropertyToID("_Distortion_Params2");
            public static readonly int _Chroma_Params      = Shader.PropertyToID("_Chroma_Params");
            public static readonly int _Vignette_Params1   = Shader.PropertyToID("_Vignette_Params1");
            public static readonly int _Vignette_Params2   = Shader.PropertyToID("_Vignette_Params2");
            public static readonly int _Lut_Params         = Shader.PropertyToID("_Lut_Params");
            public static readonly int _UserLut_Params     = Shader.PropertyToID("_UserLut_Params");
            public static readonly int _InternalLut        = Shader.PropertyToID("_InternalLut");
            public static readonly int _UserLut            = Shader.PropertyToID("_UserLut");
            public static readonly int _DownSampleScaleFactor = Shader.PropertyToID("_DownSampleScaleFactor");

            public static readonly int _FullscreenProjMat  = Shader.PropertyToID("_FullscreenProjMat");

            public static readonly int _RTHandleScale  = Shader.PropertyToID("_RTHandleScale");

            public static int[] _BloomMipUp;
            public static int[] _BloomMipDown;
        }

        #endregion
    }
}

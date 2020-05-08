// Crest Ocean System

// This file is subject to the MIT License as seen in the root of this folder structure (LICENSE)

using System.Collections.Generic;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.XR;

namespace Crest
{
    internal static class UnderwaterPostProcessUtils
    {
        public static readonly int sp_CrestOceanMaskTexture = Shader.PropertyToID("_CrestOceanMaskTexture");
        public static readonly int sp_CrestOceanMaskDepthTexture = Shader.PropertyToID("_CrestOceanMaskDepthTexture");

        static readonly int sp_OceanHeight = Shader.PropertyToID("_OceanHeight");
        static readonly int sp_MainTex = Shader.PropertyToID("_MainTex");
        static readonly int sp_InvViewProjection = Shader.PropertyToID("_InvViewProjection");
        static readonly int sp_InvViewProjectionRight = Shader.PropertyToID("_InvViewProjectionRight");
        static readonly int sp_InstanceData = Shader.PropertyToID("_InstanceData");
        static readonly int sp_AmbientLighting = Shader.PropertyToID("_AmbientLighting");
        static readonly int sp_HorizonPosNormal = Shader.PropertyToID("_HorizonPosNormal");
        static readonly int sp_HorizonPosNormalRight = Shader.PropertyToID("_HorizonPosNormalRight");

        internal class UnderwaterSphericalHarmonicsData
        {
            internal Color[] _ambientLighting = new Color[1];
            internal Vector3[] _shDirections = new Vector3[] { new Vector3(0.0f, 0.0f, 0.0f) };
        }

        // This matches const on shader side
        internal const float UNDERWATER_MASK_NO_MASK = 1.0f;
        internal const string FULL_SCREEN_EFFECT = "_FULL_SCREEN_EFFECT";
        internal const string DEBUG_VIEW_OCEAN_MASK = "_DEBUG_VIEW_OCEAN_MASK";

        internal static void InitialiseMaskTextures(ref RenderTexture textureMask, ref RenderTexture depthBuffer, Vector2Int pixelDimensions)
        {
            // Note: we pass-through pixel dimensions explicitly as we have to handle this slightly differently in HDRP
            if (textureMask == null || textureMask.width != pixelDimensions.x || textureMask.height != pixelDimensions.y)
            {
                textureMask = new RenderTexture(pixelDimensions.x, pixelDimensions.y, 0);
                textureMask.name = "Ocean Mask";
                // @Memory: We could investigate making this an 8-bit texture instead to reduce GPU memory usage.
                // We could also potentially try a half res mask as the mensicus could mask res issues.
                textureMask.format = RenderTextureFormat.RHalf;
                textureMask.Create();

                depthBuffer = new RenderTexture(pixelDimensions.x, pixelDimensions.y, 24);
                depthBuffer.enableRandomWrite = false;
                depthBuffer.name = "Ocean Mask Depth";
                depthBuffer.format = RenderTextureFormat.Depth;
                depthBuffer.Create();
            }
        }

        // Populates a screen space mask which will inform the underwater postprocess. As a future optimisation we may
        // be able to avoid this pass completely if we can reuse the camera depth after transparents are rendered.
        internal static void PopulateOceanMask(
            CommandBuffer commandBuffer, Camera camera, List<OceanChunkRenderer> chunksToRender, Plane[] frustumPlanes,
            RenderTexture colorBuffer, RenderTexture depthBuffer,
            Material oceanMaskMaterial
        )
        {
            // Get all ocean chunks and render them using cmd buffer, but with mask shader
            commandBuffer.SetRenderTarget(colorBuffer.colorBuffer, depthBuffer.depthBuffer);
            commandBuffer.ClearRenderTarget(true, true, Color.white * UNDERWATER_MASK_NO_MASK);
            commandBuffer.SetViewProjectionMatrices(camera.worldToCameraMatrix, camera.projectionMatrix);

            // Spends approx 0.2-0.3ms here on dell laptop
            foreach (OceanChunkRenderer chunk in chunksToRender)
            {
                Renderer renderer = chunk.Renderer;
                Bounds bounds = renderer.bounds;
                if (GeometryUtility.TestPlanesAABB(frustumPlanes, bounds))
                {
                    commandBuffer.DrawRenderer(renderer, oceanMaskMaterial);
                }
            }

            commandBuffer.SetGlobalTexture(sp_CrestOceanMaskTexture, colorBuffer);
            commandBuffer.SetGlobalTexture(sp_CrestOceanMaskDepthTexture, depthBuffer);

        }

        internal static void UpdatePostProcessMaterial(
            RenderTexture source,
            Camera camera,
            PropertyWrapperMaterial underwaterPostProcessMaterialWrapper,
            UnderwaterSphericalHarmonicsData sphericalHarmonicsData,
            bool copyParamsFromOceanMaterial,
            bool debugViewOceanMask
        )
        {
            Material underwaterPostProcessMaterial = underwaterPostProcessMaterialWrapper.material;
            if (copyParamsFromOceanMaterial)
            {
                // Measured this at approx 0.05ms on dell laptop
                underwaterPostProcessMaterial.CopyPropertiesFromMaterial(OceanRenderer.Instance.OceanMaterial);
            }

            // Enabling/disabling keywords each frame don't seem to have large measurable overhead
            if (debugViewOceanMask)
            {
                underwaterPostProcessMaterial.EnableKeyword(DEBUG_VIEW_OCEAN_MASK);
            }
            else
            {
                underwaterPostProcessMaterial.DisableKeyword(DEBUG_VIEW_OCEAN_MASK);
            }

            underwaterPostProcessMaterial.SetFloat(LodDataMgr.sp_LD_SliceIndex, 0);
            underwaterPostProcessMaterial.SetVector(sp_InstanceData, new Vector4(OceanRenderer.Instance.ViewerAltitudeLevelAlpha, 0f, 0f, OceanRenderer.Instance.CurrentLodCount));

            OceanRenderer.Instance._lodDataAnimWaves.BindResultData(underwaterPostProcessMaterialWrapper);
            if (OceanRenderer.Instance._lodDataSeaDepths)
            {
                OceanRenderer.Instance._lodDataSeaDepths.BindResultData(underwaterPostProcessMaterialWrapper);
            }
            else
            {
                LodDataMgrSeaFloorDepth.BindNull(underwaterPostProcessMaterialWrapper);
            }

            if (OceanRenderer.Instance._lodDataShadow)
            {
                OceanRenderer.Instance._lodDataShadow.BindResultData(underwaterPostProcessMaterialWrapper);
            }
            else
            {
                LodDataMgrShadow.BindNull(underwaterPostProcessMaterialWrapper);
            }

            float oceanHeight = OceanRenderer.Instance.SeaLevel;
            {
                underwaterPostProcessMaterial.SetFloat(sp_OceanHeight, oceanHeight);

                float maxOceanVerticalDisplacement = OceanRenderer.Instance.MaxVertDisplacement * 0.5f;
                float cameraHeight = camera.transform.position.y;
                bool forceFullShader = (cameraHeight + maxOceanVerticalDisplacement) <= oceanHeight;
                underwaterPostProcessMaterial.SetFloat(sp_OceanHeight, oceanHeight);
                if (forceFullShader)
                {
                    underwaterPostProcessMaterial.EnableKeyword(FULL_SCREEN_EFFECT);
                }
                else
                {
                    underwaterPostProcessMaterial.DisableKeyword(FULL_SCREEN_EFFECT);
                }

            }

            // Have to set these explicitly as the built-in transforms aren't in world-space for the blit function
            if (!XRSettings.enabled || XRSettings.stereoRenderingMode == XRSettings.StereoRenderingMode.MultiPass)
            {

                var inverseViewProjectionMatrix = (camera.projectionMatrix * camera.worldToCameraMatrix).inverse;
                underwaterPostProcessMaterial.SetMatrix(sp_InvViewProjection, inverseViewProjectionMatrix);

                {
                    GetHorizonPosNormal(camera, Camera.MonoOrStereoscopicEye.Mono, oceanHeight, out Vector2 pos, out Vector2 normal);
                    underwaterPostProcessMaterial.SetVector(sp_HorizonPosNormal, new Vector4(pos.x, pos.y, normal.x, normal.y));
                }
            }
            else
            {
                var inverseViewProjectionMatrix = (camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Left) * camera.worldToCameraMatrix).inverse;
                underwaterPostProcessMaterial.SetMatrix(sp_InvViewProjection, inverseViewProjectionMatrix);

                {
                    GetHorizonPosNormal(camera, Camera.MonoOrStereoscopicEye.Left, oceanHeight, out Vector2 pos, out Vector2 normal);
                    underwaterPostProcessMaterial.SetVector(sp_HorizonPosNormal, new Vector4(pos.x, pos.y, normal.x, normal.y));
                }

                var inverseViewProjectionMatrixRightEye = (camera.GetStereoProjectionMatrix(Camera.StereoscopicEye.Right) * camera.worldToCameraMatrix).inverse;
                underwaterPostProcessMaterial.SetMatrix(sp_InvViewProjectionRight, inverseViewProjectionMatrixRightEye);

                {
                    GetHorizonPosNormal(camera, Camera.MonoOrStereoscopicEye.Right, oceanHeight, out Vector2 pos, out Vector2 normal);
                    underwaterPostProcessMaterial.SetVector(sp_HorizonPosNormalRight, new Vector4(pos.x, pos.y, normal.x, normal.y));
                }
            }

            // Not sure why we need to do this - blit should set it...?
            underwaterPostProcessMaterial.SetTexture(sp_MainTex, source);

            // Compute ambient lighting SH
            {
                // We could pass in a renderer which would prime this lookup. However it doesnt make sense to use an existing render
                // at different position, as this would then thrash it and negate the priming functionality. We could create a dummy invis GO
                // with a dummy Renderer which might be enoguh, but this is hacky enough that we'll wait for it to become a problem
                // rather than add a pre-emptive hack.

                UnityEngine.Profiling.Profiler.BeginSample("Underwater sample spherical harmonics");

                LightProbes.GetInterpolatedProbe(OceanRenderer.Instance.Viewpoint.position, null, out SphericalHarmonicsL2 sphericalHarmonicsL2);
                sphericalHarmonicsL2.Evaluate(sphericalHarmonicsData._shDirections, sphericalHarmonicsData._ambientLighting);
                underwaterPostProcessMaterial.SetVector(sp_AmbientLighting, sphericalHarmonicsData._ambientLighting[0]);

                UnityEngine.Profiling.Profiler.EndSample();
            }
        }

        /// <summary>
        /// Compute intersection between the frustum far plane and the ocean plane, and return screen space pos and normal for this horizon line
        /// </summary>
        static void GetHorizonPosNormal(Camera camera, Camera.MonoOrStereoscopicEye eye, float seaLevel, out Vector2 resultPos, out Vector2 resultNormal)
        {
            // Set up back points of frustum
            NativeArray<Vector3> v_screenXY_viewZ = new NativeArray<Vector3>(4, Allocator.Temp);
            NativeArray<Vector3> v_world = new NativeArray<Vector3>(4, Allocator.Temp);
            try
            {

                v_screenXY_viewZ[0] = new Vector3(0f, 0f, camera.farClipPlane);
                v_screenXY_viewZ[1] = new Vector3(0f, 1f, camera.farClipPlane);
                v_screenXY_viewZ[2] = new Vector3(1f, 1f, camera.farClipPlane);
                v_screenXY_viewZ[3] = new Vector3(1f, 0f, camera.farClipPlane);

                // Project out to world
                for (int i = 0; i < v_world.Length; i++)
                {
                    v_world[i] = camera.ViewportToWorldPoint(v_screenXY_viewZ[i], eye);
                }

                NativeArray<Vector2> intersectionsScreen = new NativeArray<Vector2>(2, Allocator.Temp);
                // This is only used to disambiguate the normal later. Could be removed if we were more careful with point order/indices below.
                NativeArray<Vector3> intersectionsWorld = new NativeArray<Vector3>(2, Allocator.Temp);
                try
                {
                    var resultCount = 0;

                    // Iterate over each back point
                    for (int i = 0; i < 4; i++)
                    {
                        // Get next back point, to obtain line segment between them
                        var inext = (i + 1) % 4;

                        // See if one point is above and one point is below sea level - then sign of the two differences
                        // will be different, and multiplying them will give a negative
                        if ((v_world[i].y - seaLevel) * (v_world[inext].y - seaLevel) < 0f)
                        {
                            // Proportion along line segment where intersection occurs
                            var prop = (seaLevel - v_world[i].y) / (v_world[inext].y - v_world[i].y);
                            intersectionsScreen[resultCount] = Vector2.Lerp(v_screenXY_viewZ[i], v_screenXY_viewZ[inext], prop);
                            intersectionsWorld[resultCount] = Vector3.Lerp(v_world[i], v_world[inext], prop);

                            resultCount++;
                        }
                    }

                    // Two distinct results - far plane intersects water
                    if (resultCount == 2 /*&& (props[1] - props[0]).sqrMagnitude > 0.000001f*/)
                    {
                        resultPos = intersectionsScreen[0];
                        var tangent = intersectionsScreen[0] - intersectionsScreen[1];
                        resultNormal.x = -tangent.y;
                        resultNormal.y = tangent.x;

                        if (Vector3.Dot(intersectionsWorld[0] - intersectionsWorld[1], camera.transform.right) > 0f)
                        {
                            resultNormal = -resultNormal;
                        }

                        if (camera.transform.up.y <= 0f)
                        {
                            resultNormal = -resultNormal;
                        }
                    }
                    else
                    {
                        // 1 or 0 results - far plane either touches ocean plane or is completely above/below
                        resultNormal = Vector2.up;
                        for (int i = 0; i < 4; i++)
                        {
                            if (v_world[i].y < seaLevel)
                            {
                                // Underwater
                                resultPos = Vector2.zero;
                                return;
                            }
                            else if (v_world[i].y > seaLevel)
                            {
                                // Underwater
                                resultPos = Vector2.up;
                                return;
                            }
                        }

                        throw new System.Exception("GetHorizonPosNormal: Could not determine if far plane is above or below water.");
                    }
                }
                finally
                {
                    intersectionsScreen.Dispose();
                    intersectionsWorld.Dispose();
                }
            }
            finally
            {
                v_screenXY_viewZ.Dispose();
                v_world.Dispose();
            }
        }
    }
}

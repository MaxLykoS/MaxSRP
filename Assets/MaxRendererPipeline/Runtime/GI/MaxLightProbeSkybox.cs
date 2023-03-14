using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace MaxSRP
{
    public class MaxLightProbeSkybox : MaxProbeBase
    {
        //private Material viewMat;

        Vector4[] coefficients = new Vector4[9];

        private void ComputeSH()
        {
            SphericalHarmonics.GPU_Project_Uniform_9Coeff(capturedCubemap, coefficients);
        }
        private void ComputeSHCustom()
        {
            SphericalHarmonics.GPU_Project_Uniform_9Coeff(GroundTruthCubemap, coefficients);
        }

        private void Submiit()
        {
            for (int i = 0; i < 9; ++i)
            {
                Shader.SetGlobalVector("c" + i.ToString(), coefficients[i]);
            }
            UnityEditor.SceneView.RepaintAll();
        }

        public void Display()
        {
            for (int i = 0; i < 9; ++i)
                Debug.Log(coefficients[i]);
        }

        public override void Clear()
        {
            base.Clear();

            for (int i = 0; i < coefficients.Length; ++i)
                coefficients[i] = Vector3.zero;
        }

        private void UpdateSHDiffuse()
        {
            /*if (viewMat == null)
                viewMat = new Material(Shader.Find("MaxSRP/CoeffVisualizer"));*/

            RenderCubeMap();
            ComputeSH();
            //SaveCubeMap();

            /*for (int i = 0; i < 9; ++i)
            {
                viewMat.SetVector("c" + i.ToString(), coefficients[i]);
                viewMat.SetTexture("input", GroundTruthCubemap);
            }
            viewMat.SetFloat("_Mode", 1.0f);
            RenderSettings.skybox = viewMat;*/

            Submiit();

            for (int i = 0; i < coefficients.Length; ++i)
                coefficients[i] = Vector4.zero;
        }

        public override void ProbeInit()
        {
            base.ProbeInit();

            UpdateSHDiffuse();
        }

        public override bool ProbeUpdate()
        {
            bool result = base.ProbeUpdate();

            if (!result) return false;

            UpdateSHDiffuse();
            return true;
        }

        private void Awake()
        {
            ProbeInit();
        }

        private void FixedUpdate()
        {
            ProbeUpdate();
        }
    }
}
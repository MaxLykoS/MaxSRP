using System.Collections;
using UnityEngine;

public class SphericalHarmonics
{
    public static bool CPU_Project_Uniform_9Coeff(Cubemap cubemap, Vector4[] coefficients)
    {
        if (coefficients.Length != 9)
        {
            Debug.LogWarning("output size must be 9 for 9 coefficients");
            return false;
        }

        if (cubemap.width != cubemap.height)
        {
            Debug.LogWarning("input cubemap must be square");
            return false;
        }

        Color[] input_face;
        int size = cubemap.width;

        //cycle on all 6 faces of the cubemap
        for (int face = 0; face < 6; ++face)
        {
            input_face = cubemap.GetPixels((CubemapFace)face);

            //cycle all the texels
            for (int texel = 0; texel < size * size; ++texel)
            {
                float u = (texel % size) / (float)size;
                float v = ((int)(texel / size)) / (float)size;

                //get the direction vector
                Vector3 dir = DirectionFromCubemapTexel(face, u, v);
                Color radiance = input_face[texel];

                //compute the differential solid angle
                float d_omega = DifferentialSolidAngle(size, u, v);

                //cycle for 9 coefficients
                for (int c = 0; c < 9; ++c)
                {
                    //compute shperical harmonic
                    float sh = SphericalHarmonicsBasis.Eval[c](dir);

                    coefficients[c].x += radiance.r * d_omega * sh;
                    coefficients[c].y += radiance.g * d_omega * sh;
                    coefficients[c].z += radiance.b * d_omega * sh;
                    coefficients[c].w += radiance.a * d_omega * sh;
                }
            }
        }

        return true;
    }
    public static bool GPU_Project_Uniform_9Coeff(Cubemap input, Vector4[] output)
    {
        //the starting number of groups 
        int ceiled_size = Mathf.CeilToInt(input.width / 8.0f);

        ComputeBuffer output_buffer = new ComputeBuffer(9, 16);  //the output is a buffer with 9 float4
        ComputeBuffer ping_buffer = new ComputeBuffer(ceiled_size * ceiled_size * 6, 16);
        ComputeBuffer pong_buffer = new ComputeBuffer(ceiled_size * ceiled_size * 6, 16);

        ComputeShader reduce = Resources.Load<ComputeShader>("Reduce_Uniform");

        //can't have direct access to the cubemap in the compute shader (I think), so i copy the cubemap faces onto a texture2d array
        RenderTextureDescriptor desc = new RenderTextureDescriptor();
        desc.autoGenerateMips = false;
        desc.bindMS = false;
        desc.colorFormat = ConvertRenderFormat(input.format);
        desc.depthBufferBits = 0;
        desc.dimension = UnityEngine.Rendering.TextureDimension.Tex2DArray;
        desc.enableRandomWrite = false;
        desc.height = input.height;
        desc.width = input.width;
        desc.msaaSamples = 1;
        desc.sRGB = true;
        desc.useMipMap = false;
        desc.volumeDepth = 6;
        RenderTexture converted_input = new RenderTexture(desc);
        converted_input.Create();

        for (int face = 0; face < 6; ++face)
            Graphics.CopyTexture(input, face, 0, converted_input, face, 0);

        //cycle 9 coefficients
        for (int c = 0; c < 9; ++c)
        {
            ceiled_size = Mathf.CeilToInt(input.width / 8.0f);

            int kernel = reduce.FindKernel("sh_" + c.ToString());
            reduce.SetInt("coeff", c);

            //first pass, I compute the integral and make a first pass of reduction
            reduce.SetTexture(kernel, "input_data", converted_input);
            reduce.SetBuffer(kernel, "output_buffer", ping_buffer);
            reduce.SetBuffer(kernel, "coefficients", output_buffer);
            reduce.SetInt("ceiled_size", ceiled_size);
            reduce.SetInt("input_size", input.width);
            reduce.SetInt("row_size", ceiled_size);
            reduce.SetInt("face_size", ceiled_size * ceiled_size);
            reduce.Dispatch(kernel, ceiled_size, ceiled_size, 1);

            //second pass, complete reduction
            kernel = reduce.FindKernel("Reduce");

            int index = 0;
            ComputeBuffer[] buffers = { ping_buffer, pong_buffer };
            while (ceiled_size > 1)
            {
                reduce.SetInt("input_size", ceiled_size);
                ceiled_size = Mathf.CeilToInt(ceiled_size / 8.0f);
                reduce.SetInt("ceiled_size", ceiled_size);
                reduce.SetBuffer(kernel, "coefficients", output_buffer);
                reduce.SetBuffer(kernel, "input_buffer", buffers[index]);
                reduce.SetBuffer(kernel, "output_buffer", buffers[(index + 1) % 2]);
                reduce.Dispatch(kernel, ceiled_size, ceiled_size, 1);
                index = (index + 1) % 2;
            }
        }

        Vector4[] data = new Vector4[9];
        output_buffer.GetData(data);
        for (int c = 0; c < 9; ++c)
            output[c] = data[c];

        pong_buffer.Release();
        ping_buffer.Release();
        output_buffer.Release();
        return true;
    }

    public static RenderTextureFormat ConvertRenderFormat(TextureFormat input_format)
    {
        RenderTextureFormat output_format = RenderTextureFormat.ARGB32;

        switch (input_format)
        {
            case TextureFormat.RGBA32:
                output_format = RenderTextureFormat.ARGB32;
                break;

            case TextureFormat.RGBAHalf:
                output_format = RenderTextureFormat.ARGBHalf;
                break;

            case TextureFormat.RGBAFloat:
                output_format = RenderTextureFormat.ARGBFloat;
                break;

            default:
                string format_string = System.Enum.GetName(typeof(TextureFormat), input_format);
                int format_int = (int)System.Enum.Parse(typeof(RenderTextureFormat), format_string);
                output_format = (RenderTextureFormat)format_int;
                break;
        }

        return output_format;
    }

    static float AreaElement(float x, float y)
    {
        return Mathf.Atan2(x * y, Mathf.Sqrt(x * x + y * y + 1));
    }

    static float DifferentialSolidAngle(int textureSize, float U, float V)
    {
        float inv = 1.0f / textureSize;
        float u = 2.0f * (U + 0.5f * inv) - 1;
        float v = 2.0f * (V + 0.5f * inv) - 1;
        float x0 = u - inv;
        float y0 = v - inv;
        float x1 = u + inv;
        float y1 = v + inv;
        return AreaElement(x0, y0) - AreaElement(x0, y1) - AreaElement(x1, y0) + AreaElement(x1, y1);
    }

    static Vector3 DirectionFromCubemapTexel(int face, float u, float v)
    {
        Vector3 dir = Vector3.zero;

        switch (face)
        {
            case 0: //+X
                dir.x = 1;
                dir.y = v * -2.0f + 1.0f;
                dir.z = u * -2.0f + 1.0f;
                break;

            case 1: //-X
                dir.x = -1;
                dir.y = v * -2.0f + 1.0f;
                dir.z = u * 2.0f - 1.0f;
                break;

            case 2: //+Y
                dir.x = u * 2.0f - 1.0f;
                dir.y = 1.0f;
                dir.z = v * 2.0f - 1.0f;
                break;

            case 3: //-Y
                dir.x = u * 2.0f - 1.0f;
                dir.y = -1.0f;
                dir.z = v * -2.0f + 1.0f;
                break;

            case 4: //+Z
                dir.x = u * 2.0f - 1.0f;
                dir.y = v * -2.0f + 1.0f;
                dir.z = 1;
                break;

            case 5: //-Z
                dir.x = u * -2.0f + 1.0f;
                dir.y = v * -2.0f + 1.0f;
                dir.z = -1;
                break;
        }

        return dir.normalized;
    }

    public delegate float SH_Base(Vector3 v);
    public class SphericalHarmonicsBasis
    {
        public static float Y0(Vector3 v)
        {
            return 0.2820947917f;
        }

        public static float Y1(Vector3 v)
        {
            return 0.4886025119f * v.y;
        }

        public static float Y2(Vector3 v)
        {
            return 0.4886025119f * v.z;
        }

        public static float Y3(Vector3 v)
        {
            return 0.4886025119f * v.x;
        }

        public static float Y4(Vector3 v)
        {
            return 1.0925484306f * v.x * v.y;
        }

        public static float Y5(Vector3 v)
        {
            return 1.0925484306f * v.y * v.z;
        }

        public static float Y6(Vector3 v)
        {
            return 0.3153915652f * (3.0f * v.z * v.z - 1.0f);
        }

        public static float Y7(Vector3 v)
        {
            return 1.0925484306f * v.x * v.z;
        }

        public static float Y8(Vector3 v)
        {
            return 0.5462742153f * (v.x * v.x - v.y * v.y);
        }

        public static SH_Base[] Eval = { Y0, Y1, Y2, Y3, Y4, Y5, Y6, Y7, Y8 };
    }
}
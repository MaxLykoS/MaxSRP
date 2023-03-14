using UnityEngine;
using UnityEditor;
using System.Collections;
using System;
using System.Linq;
using System.Reflection;

[InitializeOnLoad]
public class MaxRayCaster
{
    public static Type type_HandleUtility;
    protected static MethodInfo meth_IntersectRayMesh;

	static MaxRayCaster()
	{
		var editorTypes = typeof(Editor).Assembly.GetTypes();

		type_HandleUtility = editorTypes.FirstOrDefault(t => t.Name == "HandleUtility");
		meth_IntersectRayMesh = type_HandleUtility.GetMethod("IntersectRayMesh", (BindingFlags.Static | BindingFlags.NonPublic));
	}

	public static bool IntersectRayMesh(ref Ray ray, MeshFilter meshFilter, out RaycastHit hit)
	{
		return IntersectRayMesh(ref ray, meshFilter.mesh, meshFilter.transform.localToWorldMatrix, out hit);
	}

	public static bool IntersectRayMesh(ref Ray ray, Mesh mesh, Matrix4x4 matrix, out RaycastHit hit)
	{
		var parameters = new object[] { ray, mesh, matrix, null };
		bool result = (bool)meth_IntersectRayMesh.Invoke(null, parameters);
		hit = (RaycastHit)parameters[3];
		return result;
	}

	public static bool IntersectRayMeshScene(Ray ray, MeshFilter[] meshFilters, RaycastHit hit)
	{
		foreach (MeshFilter mf in meshFilters)
		{
			if (IntersectRayMesh(ref ray, mf, out hit))
			{
				return true;
			}
		}
		return false;
	}
}
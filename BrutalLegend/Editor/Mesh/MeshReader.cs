using System;
using System.Collections.Generic;

namespace Sandbox.Mounting.BrutalLegend;

/// <summary>Decoded vertex data for one submesh (raw Buddha coordinate space).</summary>
public class BlSubmesh
{
	public int MatID;
	public int VertCount;
	public int VertBuffType;
	public int FaceCount;
	public int FaceType;
	public byte[] BoneIDS = Array.Empty<byte>();

	public Vector3[] Positions;
	public Vector3[] Normals;
	public Vector3[] Tangents;
	public Vector2[] UVs;
	public int[] Indices;

	// Skinning data (raw), used when building skinned models.
	public byte[][] BlendIndices;
	public byte[][] BlendWeights;

	public bool IsSkinned => VertBuffType == 1024 || VertBuffType == 768;
}

/// <summary>
/// Parses a Brütal Legend <c>Mesh</c> payload: an <c>hsem</c> metadata block (bounding volumes,
/// material table, submesh table) followed by raw interleaved vertex/index data. Layout per the
/// Noesis plugin <c>fmt_brutal_mesh</c>. Little-endian.
/// </summary>
public class BlMesh
{
	public string[] Materials = Array.Empty<string>();
	public List<BlSubmesh> Submeshes = new();

	static readonly int[] Stride = new int[1281]; // index by VertBuffType
	static BlMesh()
	{
		Stride[1024] = 48; Stride[768] = 80; Stride[512] = 40; Stride[256] = 60; Stride[1280] = 48;
	}

	public static BlMesh Parse( byte[] blob )
	{
		var mesh = new BlMesh();
		int p = 0;
		if ( blob.Length < 88 || blob[0] != 'h' || blob[1] != 's' || blob[2] != 'e' || blob[3] != 'm' )
			throw new Exception( "Not a Mesh (hsem) blob" );

		p = 84; // root header (bounding volumes) — skip
		int matCount = ReadU32( blob, ref p );
		mesh.Materials = new string[matCount];
		for ( int i = 0; i < matCount; i++ )
		{
			p += 4; // string length
			mesh.Materials[i] = ReadCString( blob, ref p );
		}

		int subCount = ReadU32( blob, ref p );
		var subs = new List<BlSubmesh>( subCount );
		for ( int i = 0; i < subCount; i++ )
		{
			var s = new BlSubmesh();
			p += 4;               // "tsbs"
			s.MatID = ReadU32( blob, ref p );
			p += 4;               // blank
			p += 4;               // "BVXD"
			s.VertCount = ReadU32( blob, ref p );
			s.VertBuffType = ReadU16( blob, ref p );
			int morphFrames = ReadU32( blob, ref p );

			if ( morphFrames != 0 )
			{
				for ( int m = 0; m < morphFrames; m++ )
				{
					int morphVertCount = ReadU32( blob, ref p );
					p += morphVertCount * 24; // VertID(4) + pos(6) + norm(6) + tangent(8)
				}
				p += morphFrames * 2;         // FrameOrder[]
				p += 2;                       // FinalMorphCount
				ReadU32( blob, ref p );       // MorphCharCount
				ReadCString( blob, ref p );   // MorphName
			}

			if ( s.VertBuffType == 768 || s.VertBuffType == 256 ) p += 3;
			p += 4;               // "BIXD"
			p += 4;               // blank
			s.FaceCount = ReadU32( blob, ref p );
			s.FaceType = ReadU32( blob, ref p );
			int boneIdCount = blob[p++];
			s.BoneIDS = new byte[boneIdCount];
			Array.Copy( blob, p, s.BoneIDS, 0, boneIdCount );
			p += boneIdCount;
			p += 56;              // submesh bbox / LOD
			if ( s.VertBuffType == 1280 ) { p += 2; s.VertBuffType = 1024; }

			subs.Add( s );
		}
		mesh.Submeshes = subs;

		// Geometry occupies the tail of the blob: per submesh [verts][indices].
		int geomSize = 0;
		foreach ( var s in subs )
			geomSize += s.VertCount * Stride[s.VertBuffType] + s.FaceCount * 2;

		if ( geomSize > blob.Length )
			throw new Exception( $"Mesh geometry size {geomSize} exceeds blob {blob.Length}" );

		int g = blob.Length - geomSize;
		foreach ( var s in subs )
		{
			ReadVertices( blob, ref g, s );
			s.Indices = new int[s.FaceCount];
			for ( int k = 0; k < s.FaceCount; k++ )
				s.Indices[k] = ReadU16( blob, ref g );
		}
		return mesh;
	}

	static void ReadVertices( byte[] b, ref int p, BlSubmesh s )
	{
		int n = s.VertCount;
		s.Positions = new Vector3[n];
		s.Normals = new Vector3[n];
		s.Tangents = new Vector3[n];
		s.UVs = new Vector2[n];
		s.BlendIndices = new byte[n][];
		s.BlendWeights = new byte[n][];

		bool half = s.VertBuffType == 1024 || s.VertBuffType == 512;
		for ( int i = 0; i < n; i++ )
		{
			int start = p;
			byte[] bi = null, bw = null;

			if ( s.VertBuffType == 1024 )
			{
				bi = ReadBytes( b, ref p, 4 ); bw = ReadBytes( b, ref p, 4 );
				s.Positions[i] = ReadHalf3( b, ref p, 8 );
				s.UVs[i] = ReadHalf2( b, ref p );
				ReadHalf2( b, ref p );                       // UV1 (unused)
				s.Normals[i] = ReadHalf3( b, ref p, 8 );
				s.Tangents[i] = ReadHalf3( b, ref p, 8 );
				p += 8;                                       // COL0 + COL1
			}
			else if ( s.VertBuffType == 768 )
			{
				s.Positions[i] = ReadFloat3( b, ref p );
				s.Normals[i] = ReadFloat3( b, ref p );
				s.Tangents[i] = ReadFloat3( b, ref p );
				s.UVs[i] = ReadFloat2( b, ref p );
				ReadFloat2( b, ref p );                       // UV1
				p += 8;                                       // COL0 + COL1
				bi = ReadBytes( b, ref p, 4 );
				bw = FloatWeightsToBytes( ReadFloat4( b, ref p ) );
			}
			else if ( s.VertBuffType == 512 )
			{
				s.Positions[i] = ReadHalf3( b, ref p, 8 );
				s.Normals[i] = ReadHalf3( b, ref p, 8 );
				s.Tangents[i] = ReadHalf3( b, ref p, 8 );
				s.UVs[i] = ReadHalf2( b, ref p );
				ReadHalf2( b, ref p );
				p += 8;                                       // COL0 + COL1
			}
			else // 256
			{
				s.Positions[i] = ReadFloat3( b, ref p );
				s.Normals[i] = ReadFloat3( b, ref p );
				s.Tangents[i] = ReadFloat3( b, ref p );
				s.UVs[i] = ReadFloat2( b, ref p );
				ReadFloat2( b, ref p );
				p += 8;                                       // COL0 + COL1
			}

			s.BlendIndices[i] = bi;
			s.BlendWeights[i] = bw;
			int consumed = p - start;
			if ( consumed != Stride[s.VertBuffType] )
				p = start + Stride[s.VertBuffType]; // enforce stride
		}
	}

	// ---- little-endian readers ----
	static int ReadU32( byte[] b, ref int p ) { int v = BitConverter.ToInt32( b, p ); p += 4; return v; }
	static int ReadU16( byte[] b, ref int p ) { int v = BitConverter.ToUInt16( b, p ); p += 2; return v; }
	static byte[] ReadBytes( byte[] b, ref int p, int n ) { var r = new byte[n]; Array.Copy( b, p, r, 0, n ); p += n; return r; }

	static Vector3 ReadFloat3( byte[] b, ref int p )
	{
		float x = BitConverter.ToSingle( b, p ); p += 4;
		float y = BitConverter.ToSingle( b, p ); p += 4;
		float z = BitConverter.ToSingle( b, p ); p += 4;
		return new Vector3( x, y, z );
	}
	static Vector2 ReadFloat2( byte[] b, ref int p )
	{
		float x = BitConverter.ToSingle( b, p ); p += 4;
		float y = BitConverter.ToSingle( b, p ); p += 4;
		return new Vector2( x, y );
	}
	static float[] ReadFloat4( byte[] b, ref int p )
	{
		var r = new float[4];
		for ( int i = 0; i < 4; i++ ) { r[i] = BitConverter.ToSingle( b, p ); p += 4; }
		return r;
	}
	static Vector3 ReadHalf3( byte[] b, ref int p, int strideBytes )
	{
		int start = p;
		float x = Half( ReadU16( b, ref p ) );
		float y = Half( ReadU16( b, ref p ) );
		float z = Half( ReadU16( b, ref p ) );
		p = start + strideBytes; // e.g. f16[4] = 8 bytes
		return new Vector3( x, y, z );
	}
	static Vector2 ReadHalf2( byte[] b, ref int p )
	{
		float x = Half( ReadU16( b, ref p ) );
		float y = Half( ReadU16( b, ref p ) );
		return new Vector2( x, y );
	}
	static float Half( int h ) => (float)BitConverter.UInt16BitsToHalf( (ushort)h );

	static byte[] FloatWeightsToBytes( float[] w )
	{
		var r = new byte[4];
		for ( int i = 0; i < 4; i++ )
			r[i] = (byte)Math.Clamp( (int)(w[i] * 255f + 0.5f), 0, 255 );
		return r;
	}

	static string ReadCString( byte[] b, ref int p )
	{
		int start = p;
		while ( p < b.Length && b[p] != 0 ) p++;
		var s = System.Text.Encoding.ASCII.GetString( b, start, p - start );
		if ( p < b.Length ) p++; // consume NUL
		return s;
	}
}

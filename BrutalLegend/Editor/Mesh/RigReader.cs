using System;

namespace Sandbox.Mounting.BrutalLegend;

/// <summary>
/// Parses Brütal Legend <c>Rig</c> payloads: bone hierarchy (names + parent indices)
/// and 4x4 inverse bind pose matrices. Layout per the Noesis plugin <c>fmt_brutal_mesh</c>.
/// </summary>
public class BlRig
{
	public struct Bone
	{
		public string Name;
		public int ParentIndex;
		public Vector3 Position;
		public Rotation Rotation;
	}

	public Bone[] Bones = Array.Empty<Bone>();

	public static BlRig Parse( byte[] blob )
	{
		if ( blob == null || blob.Length < 112 )
			return null;

		// Offset 0x10: BoneCount (uint32)
		int boneCount = BitConverter.ToInt32( blob, 0x10 );
		if ( boneCount <= 0 || boneCount > 2000 )
			return null;

		// Read offsets table at byte 76
		// Offset 80: Parent indices offset
		// Offset 96: Matrices offset
		int offParents = BitConverter.ToInt32( blob, 80 );
		int offMatrices = BitConverter.ToInt32( blob, 96 );

		int hdrLen = 112;
		int p = hdrLen;

		var names = new string[boneCount];
		for ( int i = 0; i < boneCount; i++ )
		{
			int start = p;
			while ( p < blob.Length && blob[p] != 0 ) p++;
			names[i] = System.Text.Encoding.ASCII.GetString( blob, start, p - start );
			if ( p < blob.Length ) p++;
		}

		var parents = new short[boneCount];
		int pParents = hdrLen + offParents;
		if ( pParents + boneCount * 2 <= blob.Length )
		{
			for ( int i = 0; i < boneCount; i++ )
			{
				parents[i] = BitConverter.ToInt16( blob, pParents + i * 2 );
			}
		}

		int pMatrices = hdrLen + offMatrices;
		var bones = new Bone[boneCount];

		for ( int i = 0; i < boneCount; i++ )
		{
			int mOff = pMatrices + i * 64;
			if ( mOff + 64 > blob.Length ) break;

			// Direct3D 4x4 row-major inverse bind pose matrix
			float r00 = BitConverter.ToSingle( blob, mOff + 0 );
			float r01 = BitConverter.ToSingle( blob, mOff + 4 );
			float r02 = BitConverter.ToSingle( blob, mOff + 8 );

			float r10 = BitConverter.ToSingle( blob, mOff + 16 );
			float r11 = BitConverter.ToSingle( blob, mOff + 20 );
			float r12 = BitConverter.ToSingle( blob, mOff + 24 );

			float r20 = BitConverter.ToSingle( blob, mOff + 32 );
			float r21 = BitConverter.ToSingle( blob, mOff + 36 );
			float r22 = BitConverter.ToSingle( blob, mOff + 40 );

			float tx = BitConverter.ToSingle( blob, mOff + 48 );
			float ty = BitConverter.ToSingle( blob, mOff + 52 );
			float tz = BitConverter.ToSingle( blob, mOff + 56 );

			// Invert rigid transform: R_bind = R_inv^T, T_bind = - (T_inv * R_bind)
			float btx = -(tx * r00 + ty * r01 + tz * r02);
			float bty = -(tx * r10 + ty * r11 + tz * r12);
			float btz = -(tx * r20 + ty * r21 + tz * r22);

			// The stored matrix is the inverse bind pose (row-vector, translation in the last row):
			//   v_bone = v_model * R_inv + T_inv
			// The bind transform s&box wants is column-vector: v_model = R_sbox * v_bone + T_bind,
			// so R_sbox is the stored rotation (R_bind transposed), not its transpose.
			Rotation rot = MatrixToRotation( r00, r01, r02, r10, r11, r12, r20, r21, r22 );

			bones[i] = new Bone
			{
				Name = names[i],
				ParentIndex = parents[i],
				Position = new Vector3( btx, bty, btz ),
				Rotation = rot
			};
		}

		return new BlRig { Bones = bones };
	}

	static Rotation MatrixToRotation( float m00, float m01, float m02, float m10, float m11, float m12, float m20, float m21, float m22 )
	{
		float tr = m00 + m11 + m22;
		float qw, qx, qy, qz;
		if ( tr > 0 )
		{
			float s = MathF.Sqrt( tr + 1.0f ) * 2.0f;
			qw = 0.25f * s;
			qx = (m21 - m12) / s;
			qy = (m02 - m20) / s;
			qz = (m10 - m01) / s;
		}
		else if ( m00 > m11 && m00 > m22 )
		{
			float s = MathF.Sqrt( 1.0f + m00 - m11 - m22 ) * 2.0f;
			qw = (m21 - m12) / s;
			qx = 0.25f * s;
			qy = (m01 + m10) / s;
			qz = (m02 + m20) / s;
		}
		else if ( m11 > m22 )
		{
			float s = MathF.Sqrt( 1.0f + m11 - m00 - m22 ) * 2.0f;
			qw = (m02 - m20) / s;
			qx = (m01 + m10) / s;
			qy = 0.25f * s;
			qz = (m12 + m21) / s;
		}
		else
		{
			float s = MathF.Sqrt( 1.0f + m22 - m00 - m11 ) * 2.0f;
			qw = (m10 - m01) / s;
			qx = (m02 + m20) / s;
			qy = (m12 + m21) / s;
			qz = 0.25f * s;
		}
		return new Rotation( qx, qy, qz, qw );
	}
}

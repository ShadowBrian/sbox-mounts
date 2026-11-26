using System;
using System.Collections.Generic;

namespace Sandbox.Mounting.BrutalLegend;

/// <summary>
/// Decodes a Brütal Legend <c>AnimResource</c> (the <c>dnap</c> chunk) into per-bone local
/// keyframes and appends model-space frames to a <see cref="ModelBuilder"/> animation.
///
/// Layout verified by emulating the game's sampler (see HANDOFF.md):
/// - 16 block streams; rotation/scale/position start arrays, mask streams and control arrays.
/// - Masks are <c>L</c> bits per dynamic track (L = block lastFrameIndex), MSB-first, starting at
///   <c>mask*8 + track*L</c>. A set bit <c>j</c> is a control key at frame <c>j+1</c>.
/// - Keys are [start] + controls + [next block start], slerped/lerped by frame.
/// - 48-bit quats decode to canonical (x,y,z,w); half3 vectors are little-endian IEEE half.
/// </summary>
public sealed class BudhaAnimation
{
	public string Name;
	public float FrameRate = 30f;
	public int FrameCount;
	public int MaxBoneIndex = -1;

	sealed class Block
	{
		public int FirstFrame;
		public int LastFrame;
		public List<Dyn<Rotation>> Rot = new();
		public List<Dyn<Vector3>> Pos = new();
		public List<Dyn<Vector3>> Scale = new();
	}

	sealed class Dyn<T>
	{
		public int Bone;
		public int[] Times;
		public T[] Values;
	}

	readonly List<Block> _blocks = new();
	readonly Dictionary<int, Rotation> _staticRot = new();
	readonly Dictionary<int, Vector3> _staticPos = new();
	readonly Dictionary<int, Vector3> _staticScale = new();

	const float QuantK = 16384f;
	const float QuantS = 4.3159689e-05f;

	public static BudhaAnimation Parse( byte[] blob, string name )
	{
		if ( blob is null || blob.Length < 0x60 )
			return null;

		int chunk = FindDnap( blob );
		if ( chunk < 0 )
			return null;

		var a = new BudhaAnimation { Name = name };
		a.FrameRate = ReadF32( blob, chunk + 0x08 );
		if ( a.FrameRate <= 1f || a.FrameRate > 240f )
			a.FrameRate = 30f;

		a.FrameCount = ReadU16( blob, chunk + 0x10 );
		if ( a.FrameCount <= 0 || a.FrameCount > 20000 )
			return null;

		int poseCount = ReadU16( blob, chunk + 0x16 );
		if ( poseCount <= 0 || poseCount > 4096 )
			return null;

		// The channel lists below store transform-track indices. The outer AnimResource header
		// maps every track to a rig bone (u8[R] at +0x0E); this is not the identity map.
		int trackCount = ReadU16( blob, chunk + 0x12 );
		if ( trackCount <= 0 || 0x0E + trackCount > blob.Length )
			return null;

		var boneMap = new int[trackCount];
		for ( int t = 0; t < trackCount; t++ )
			boneMap[t] = blob[0x0E + t];

		int poseTable = chunk + 0x48 + (int)ReadU32( blob, chunk + 0x44 );
		int frameTable = chunk + 0x48 + ReadU16( blob, chunk + 0x48 );

		// Counts: rot static/dyn, scale static/dyn, pos static/dyn, float static/dyn.
		var c = new int[8];
		for ( int i = 0; i < 8; i++ )
			c[i] = ReadU16( blob, chunk + 0x34 + i * 2 );

		int p0 = chunk + 0x60;
		int p1 = p0 + ((c[0] + 7) & ~7) * 2;
		int p2 = p1 + ((c[2] + 3) & ~3) * 2;
		int p3 = p2 + ((c[4] + 3) & ~3) * 2;
		int p4 = p3 + ((c[6] + 3) & ~3) * 2;
		int p5 = p4 + ((c[1] + 7) & ~7) * 2;
		int p6 = p5 + ((c[3] + 3) & ~3) * 2;

		int arrRot = chunk + 0x4C + (int)ReadU32( blob, chunk + 0x4C );
		int arrScl = chunk + 0x50 + (int)ReadU32( blob, chunk + 0x50 );
		int arrPos = chunk + 0x54 + (int)ReadU32( blob, chunk + 0x54 );

		// Static channels (global to the animation).
		for ( int i = 0; i < c[0]; i++ )
		{
			int track = ReadU16( blob, p0 + i * 2 );
			if ( track >= trackCount ) continue;
			int bone = boneMap[track];
			if ( bone < 0 ) continue;
			a._staticRot[bone] = ReadQuat( blob, arrRot + i * 6 );
			if ( bone > a.MaxBoneIndex ) a.MaxBoneIndex = bone;
		}

		for ( int i = 0; i < c[2]; i++ )
		{
			int track = ReadU16( blob, p1 + i * 2 );
			if ( track >= trackCount ) continue;
			int bone = boneMap[track];
			if ( bone < 0 ) continue;
			a._staticScale[bone] = ReadHalf3( blob, arrScl + i * 6, Vector3.One );
			if ( bone > a.MaxBoneIndex ) a.MaxBoneIndex = bone;
		}

		for ( int i = 0; i < c[4]; i++ )
		{
			int track = ReadU16( blob, p2 + i * 2 );
			if ( track >= trackCount ) continue;
			int bone = boneMap[track];
			if ( bone < 0 ) continue;
			a._staticPos[bone] = ReadHalf3( blob, arrPos + i * 6, Vector3.Zero );
			if ( bone > a.MaxBoneIndex ) a.MaxBoneIndex = bone;
		}

		// Blocks.
		for ( int bi = 0; bi < poseCount; bi++ )
		{
			int rawBlk = (int)ReadU32( blob, poseTable + bi * 8 );
			int blk = chunk + rawBlk;
			if ( blk < 0 || blk + 0x24 > blob.Length )
				continue;

			int first = ReadU16( blob, frameTable + bi * 4 );
			int last = ReadU16( blob, frameTable + bi * 4 + 2 );
			if ( first >= a.FrameCount ) continue;

			int dataStart = blk + ReadU16( blob, blk + 0x22 );
			int s0Rot = dataStart;
			int s0Scl = s0Rot + ReadU16( blob, blk + 0x00 );
			int s0Pos = s0Scl + ReadU16( blob, blk + 0x02 );
			int s0Flt = s0Pos + ReadU16( blob, blk + 0x04 );

			int mskRot = s0Flt + blob[blk + 0x08] + blob[blk + 0x09] + ReadU16( blob, blk + 0x06 );
			int mskScl = mskRot + ReadU16( blob, blk + 0x0A );
			int mskPos = mskScl + ReadU16( blob, blk + 0x0C );
			int mskFlt = mskPos + ReadU16( blob, blk + 0x0E );

			int ctrlRot = mskFlt + blob[blk + 0x12] + blob[blk + 0x13] + ReadU16( blob, blk + 0x10 );
			int ctrlScl = ctrlRot + ReadU16( blob, blk + 0x14 );
			int ctrlPos = ctrlScl + ReadU16( blob, blk + 0x16 );

			int next = blk + ReadU16( blob, blk + 0x20 );
			bool hasNext = next + 0x24 <= blob.Length && next > blk;
			int nRot = s0Rot, nScl = s0Scl, nPos = s0Pos;
			if ( hasNext )
			{
				int nextData = next + ReadU16( blob, next + 0x22 );
				nRot = nextData;
				nScl = nRot + ReadU16( blob, next + 0x00 );
				nPos = nScl + ReadU16( blob, next + 0x02 );
			}

			var block = new Block { FirstFrame = first, LastFrame = first + last };
			block.Rot = ParseDyn( blob, c[1], p4, mskRot, s0Rot, ctrlRot, nRot, last, trackCount, boneMap, ReadQuatDefault );
			block.Scale = ParseDyn( blob, c[3], p5, mskScl, s0Scl, ctrlScl, nScl, last, trackCount, boneMap, ReadHalf3One );
			block.Pos = ParseDyn( blob, c[5], p6, mskPos, s0Pos, ctrlPos, nPos, last, trackCount, boneMap, ReadHalf3Zero );

			foreach ( var t in block.Rot ) if ( t.Bone > a.MaxBoneIndex ) a.MaxBoneIndex = t.Bone;
			foreach ( var t in block.Scale ) if ( t.Bone > a.MaxBoneIndex ) a.MaxBoneIndex = t.Bone;
			foreach ( var t in block.Pos ) if ( t.Bone > a.MaxBoneIndex ) a.MaxBoneIndex = t.Bone;

			a._blocks.Add( block );
		}

		return a._blocks.Count > 0 ? a : null;
	}

	static List<Dyn<T>> ParseDyn<T>( byte[] blob, int count, int list, int msk, int s0, int ctrl, int nextS0, int L, int trackCount, int[] boneMap, Func<byte[], int, T> read )
	{
		var result = new List<Dyn<T>>( Math.Max( 0, count ) );
		int ctrlOffset = ctrl;

		for ( int k = 0; k < count; k++ )
		{
			int track = ReadU16( blob, list + k * 2 );

			ulong mask = ReadBits( blob, msk * 8 + k * L, L );
			var times = new List<int> { 0 };
			for ( int j = 0; j < L; j++ )
				if ( (mask & (1UL << (L - 1 - j))) != 0 )
					times.Add( j + 1 );
			times.Add( L + 1 );

			int controlCount = times.Count - 2;
			int bone = (track < trackCount) ? boneMap[track] : -1;
			if ( bone < 0 )
			{
				// The control array is laid out by track order; advance past this track's
				// controls even when its index is unusable, or every later track misaligns.
				ctrlOffset += controlCount * 6;
				continue;
			}

			var values = new T[times.Count];
			values[0] = read( blob, s0 + k * 6 );
			for ( int i = 0; i < controlCount; i++ )
				values[1 + i] = read( blob, ctrlOffset + i * 6 );
			values[^1] = read( blob, nextS0 + k * 6 );
			ctrlOffset += controlCount * 6;

			result.Add( new Dyn<T> { Bone = bone, Times = times.ToArray(), Values = values } );
		}

		return result;
	}

	/// <summary>
	/// Appends one frame per animation frame to a new sequence on <paramref name="builder"/>.
	/// Transforms are composed in Buddha space and converted to s&amp;box model space, matching
	/// the model's model-space bind transforms.
	/// </summary>
	public bool AppendTo( ModelBuilder builder, BlRig rig, Func<Vector3, Vector3> convertPos, Func<Rotation, Rotation> convertRot )
	{
		int boneCount = rig.Bones.Length;
		if ( boneCount == 0 || MaxBoneIndex < 0 || MaxBoneIndex >= boneCount )
			return false;

		var parent = new int[boneCount];
		var bindModel = new Transform[boneCount];
		var bindLocal = new Transform[boneCount];

		for ( int i = 0; i < boneCount; i++ )
		{
			parent[i] = rig.Bones[i].ParentIndex;
			bindModel[i] = new Transform( rig.Bones[i].Position, rig.Bones[i].Rotation, Vector3.One );
		}

		for ( int i = 0; i < boneCount; i++ )
		{
			int p = parent[i];
			bindLocal[i] = (p >= 0 && p < boneCount) ? bindModel[p].ToLocal( bindModel[i] ) : bindModel[i];
		}

		// Map every animation frame to its block. Blocks partition [first, first + L].
		var blockForFrame = new int[FrameCount];
		for ( int i = 0; i < blockForFrame.Length; i++ ) blockForFrame[i] = -1;

		for ( int bi = 0; bi < _blocks.Count; bi++ )
		{
			var b = _blocks[bi];
			int start = Math.Max( 0, b.FirstFrame );
			int end = Math.Min( FrameCount - 1, b.LastFrame );
			for ( int f = start; f <= end; f++ )
				blockForFrame[f] = bi;
		}

		int lastBlock = 0;
		for ( int f = 0; f < FrameCount; f++ )
		{
			if ( blockForFrame[f] >= 0 ) lastBlock = blockForFrame[f];
			else blockForFrame[f] = lastBlock;
		}

		var animation = builder.AddAnimation( Name, FrameRate );
		animation.Looping = false;

		var local = new Transform[boneCount];
		var model = new Transform[boneCount];
		var done = new bool[boneCount];
		var frame = new List<Transform>( boneCount );
		var localFrame = new List<Transform>( boneCount );

		for ( int f = 0; f < FrameCount; f++ )
		{
			var block = _blocks[blockForFrame[f]];
			int blockFrame = f - block.FirstFrame;

			Array.Copy( bindLocal, local, boneCount );

			foreach ( var kv in _staticPos )
			{
				var t = local[kv.Key];
				t.Position = kv.Value;
				local[kv.Key] = t;
			}

			foreach ( var kv in _staticScale )
			{
				var t = local[kv.Key];
				t.Scale = kv.Value;
				local[kv.Key] = t;
			}

			foreach ( var kv in _staticRot )
			{
				var t = local[kv.Key];
				t.Rotation = kv.Value;
				local[kv.Key] = t;
			}

			foreach ( var tr in block.Pos )
			{
				var t = local[tr.Bone];
				t.Position = Sample( tr, blockFrame, ( a, b, f ) => a + (b - a) * f );
				local[tr.Bone] = t;
			}

			foreach ( var tr in block.Scale )
			{
				var t = local[tr.Bone];
				t.Scale = Sample( tr, blockFrame, ( a, b, f ) => a + (b - a) * f );
				local[tr.Bone] = t;
			}

			foreach ( var tr in block.Rot )
			{
				var t = local[tr.Bone];
				t.Rotation = Sample( tr, blockFrame, Slerp );
				local[tr.Bone] = t;
			}

			Array.Clear( done, 0, boneCount );
			frame.Clear();
			localFrame.Clear();

			for ( int i = 0; i < boneCount; i++ )
			{
				var m = Resolve( i, local, model, done, parent );
				frame.Add( new Transform( convertPos( m.Position ), convertRot( m.Rotation ), m.Scale ) );
			}

			for ( int i = 0; i < boneCount; i++ )
			{
				int p = parent[i];
				localFrame.Add( (p >= 0 && p < boneCount) ? frame[p].ToLocal( frame[i] ) : frame[i] );
			}

			animation.AddFrame( localFrame );
		}

		return true;
	}

	static Transform Resolve( int i, Transform[] local, Transform[] model, bool[] done, int[] parent )
	{
		if ( done[i] ) return model[i];

		int p = parent[i];
		var m = (p >= 0 && p < local.Length) ? Resolve( p, local, model, done, parent ).ToWorld( local[i] ) : local[i];
		model[i] = m;
		done[i] = true;
		return m;
	}

	static T Sample<T>( Dyn<T> track, int frame, Func<T, T, float, T> lerp )
	{
		var times = track.Times;
		var values = track.Values;
		if ( times is null || values is null || times.Length == 0 )
			return default;

		int k = 0;
		while ( k + 1 < times.Length && times[k + 1] <= frame )
			k++;

		if ( k >= values.Length - 1 )
			return values[^1];

		int t0 = times[k], t1 = times[k + 1];
		float alpha = t1 == t0 ? 1f : (frame - t0) / (float)(t1 - t0);
		return lerp( values[k], values[k + 1], Math.Clamp( alpha, 0f, 1f ) );
	}

	static Rotation Slerp( Rotation a, Rotation b, float t )
	{
		float ax = a.x, ay = a.y, az = a.z, aw = a.w;
		float bx = b.x, by = b.y, bz = b.z, bw = b.w;

		float dot = ax * bx + ay * by + az * bz + aw * bw;
		if ( dot < 0f )
		{
			bx = -bx; by = -by; bz = -bz; bw = -bw;
			dot = -dot;
		}

		if ( dot > 0.9995f )
			return new Rotation( ax + (bx - ax) * t, ay + (by - ay) * t, az + (bz - az) * t, aw + (bw - aw) * t ).Normal;

		float theta = MathF.Acos( Math.Clamp( dot, -1f, 1f ) );
		float sin = MathF.Sin( theta );
		float s0 = MathF.Sin( (1f - t) * theta ) / sin;
		float s1 = MathF.Sin( t * theta ) / sin;
		return new Rotation( ax * s0 + bx * s1, ay * s0 + by * s1, az * s0 + bz * s1, aw * s0 + bw * s1 );
	}

	static int FindDnap( byte[] d )
	{
		for ( int i = 0; i + 0x60 <= d.Length; i++ )
		{
			if ( d[i] != 'd' || d[i + 1] != 'n' || d[i + 2] != 'a' || d[i + 3] != 'p' )
				continue;

			float fps = ReadF32( d, i + 0x08 );
			int frames = ReadU16( d, i + 0x10 );
			if ( fps > 1f && fps <= 240f && frames > 0 && frames <= 20000 )
				return i;
		}

		return -1;
	}

	static ulong ReadBits( byte[] d, int bitOffset, int count )
	{
		if ( count <= 0 )
			return 0;
		if ( count > 64 ) count = 64;

		ulong v = 0;
		for ( int i = 0; i < count; i++ )
		{
			int bit = bitOffset + i;
			int index = bit >> 3;
			if ( index < 0 || index >= d.Length )
			{
				v <<= count - i;
				break;
			}

			v = (v << 1) | (uint)((d[index] >> (7 - (bit & 7))) & 1);
		}

		return v;
	}

	static Rotation ReadQuatDefault( byte[] d, int o ) => ReadQuat( d, o );

	static Vector3 ReadHalf3One( byte[] d, int o ) => ReadHalf3( d, o, Vector3.One );
	static Vector3 ReadHalf3Zero( byte[] d, int o ) => ReadHalf3( d, o, Vector3.Zero );

	static Rotation ReadQuat( byte[] d, int o )
	{
		if ( o < 0 || o + 6 > d.Length )
			return Rotation.Identity;

		ulong u = ((ulong)d[o] << 40) | ((ulong)d[o + 1] << 32) | ((ulong)d[o + 2] << 24)
			| ((ulong)d[o + 3] << 16) | ((ulong)d[o + 4] << 8) | d[o + 5];

		int shift = (int)(u & 3);
		int c0 = (int)((u >> 32) & 0x7fff);
		int c1 = (int)((u >> 17) & 0x7fff);
		int c2 = (int)((u >> 2) & 0x7fff);

		float v0 = (c0 - QuantK) * QuantS;
		float v1 = (c1 - QuantK) * QuantS;
		float v2 = (c2 - QuantK) * QuantS;
		float w2 = 1f - (v0 * v0 + v1 * v1 + v2 * v2);
		float w = w2 > 0f ? MathF.Sqrt( w2 ) : 0f;

		// The component table places the computed component on the omitted axis:
		// shift 0 -> x, 1 -> y, 2 -> z, 3 -> w. The output is always canonical (x,y,z,w).
		return shift switch
		{
			0 => new Rotation( w, v0, v1, v2 ),
			1 => new Rotation( v0, w, v1, v2 ),
			2 => new Rotation( v0, v1, w, v2 ),
			_ => new Rotation( v0, v1, v2, w )
		};
	}

	static Vector3 ReadHalf3( byte[] d, int o, Vector3 fallback )
	{
		if ( o < 0 || o + 6 > d.Length )
			return fallback;

		return new Vector3(
			(float)BitConverter.UInt16BitsToHalf( BitConverter.ToUInt16( d, o ) ),
			(float)BitConverter.UInt16BitsToHalf( BitConverter.ToUInt16( d, o + 2 ) ),
			(float)BitConverter.UInt16BitsToHalf( BitConverter.ToUInt16( d, o + 4 ) ) );
	}

	static int ReadU16( byte[] d, int o )
	{
		if ( o < 0 || o + 2 > d.Length ) return 0;
		return d[o] | (d[o + 1] << 8);
	}

	static uint ReadU32( byte[] d, int o )
	{
		if ( o < 0 || o + 4 > d.Length ) return 0;
		return (uint)(d[o] | (d[o + 1] << 8) | (d[o + 2] << 16) | (d[o + 3] << 24));
	}

	static float ReadF32( byte[] d, int o )
	{
		if ( o < 0 || o + 4 > d.Length ) return 0f;
		return BitConverter.ToSingle( d, o );
	}
}

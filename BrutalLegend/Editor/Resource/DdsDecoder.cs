using System;

namespace Sandbox.Mounting.BrutalLegend;

/// <summary>
/// Decodes a Brütal Legend DDS image's top mip level to RGBA8. Handles BC1 (DXT1), BC2 (DXT3),
/// BC3 (DXT5) and uncompressed A8R8G8B8. Used by the model loader to build textures without
/// triggering compressed-texture GPU issues (mirrors the Skyrim mount's approach).
/// </summary>
public static class DdsDecoder
{
	public static int ReadWidth( byte[] dds ) => (int)ReadU32( dds, 16 );
	public static int ReadHeight( byte[] dds ) => (int)ReadU32( dds, 12 );

	/// <summary>Decodes the top-level image to RGBA8 (width*height*4 bytes).</summary>
	public static byte[] DecodeToRgba( byte[] dds )
	{
		if ( dds.Length < 128 || dds[0] != 'D' || dds[1] != 'D' || dds[2] != 'S' || dds[3] != ' ' )
			throw new Exception( "Not a DDS file" );

		int height = (int)ReadU32( dds, 12 );
		int width = (int)ReadU32( dds, 16 );
		uint fourCC = ReadU32( dds, 84 );
		uint rgbBitCount = ReadU32( dds, 88 );
		int dataOffset = 128;

		switch ( fourCC )
		{
			case 0x31545844: return DecodeBC( dds, dataOffset, width, height, 1 ); // "DXT1"
			case 0x32545844: return DecodeBC( dds, dataOffset, width, height, 2 ); // "DXT2"
			case 0x33545844: return DecodeBC( dds, dataOffset, width, height, 2 ); // "DXT3"
			case 0x34545844: return DecodeBC( dds, dataOffset, width, height, 3 ); // "DXT4"
			case 0x35545844: return DecodeBC( dds, dataOffset, width, height, 3 ); // "DXT5"
			default:
				if ( rgbBitCount == 32 || fourCC == 0 )
					return SwizzleARGB( dds, dataOffset, width, height );
				throw new Exception( $"Unsupported DDS fourCC 0x{fourCC:X8}" );
		}
	}

	static byte[] DecodeBC( byte[] src, int off, int w, int h, int variant )
	{
		var dst = new byte[w * h * 4];
		int blockBytes = variant == 1 ? 8 : 16;
		int blocksX = (w + 3) / 4, blocksY = (h + 3) / 4;
		var block = new byte[16];

		for ( int by = 0; by < blocksY; by++ )
		for ( int bx = 0; bx < blocksX; bx++ )
		{
			int srcBlock = off + (by * blocksX + bx) * blockBytes;
			Array.Copy( src, srcBlock, block, 0, Math.Min( blockBytes, src.Length - srcBlock ) );

			var px = new byte[16 * 4];
			if ( variant == 1 ) DecodeColorBlock( block, 0, px, true );
			else
			{
				if ( variant == 2 ) DecodeExplicitAlpha( block, 0, px );
				else DecodeInterpAlpha( block, 0, px );
				DecodeColorBlock( block, 8, px, false );
			}

			for ( int py = 0; py < 4; py++ )
			{
				int y = by * 4 + py;
				if ( y >= h ) continue;
				for ( int pxi = 0; pxi < 4; pxi++ )
				{
					int x = bx * 4 + pxi;
					if ( x >= w ) continue;
					int di = (y * w + x) * 4, si = (py * 4 + pxi) * 4;
					dst[di] = px[si]; dst[di + 1] = px[si + 1]; dst[di + 2] = px[si + 2]; dst[di + 3] = px[si + 3];
				}
			}
		}
		return dst;
	}

	static void DecodeColorBlock( byte[] b, int o, byte[] px, bool bc1 )
	{
		ushort c0 = (ushort)(b[o] | (b[o + 1] << 8));
		ushort c1 = (ushort)(b[o + 2] | (b[o + 3] << 8));
		uint bits = (uint)(b[o + 4] | (b[o + 5] << 8) | (b[o + 6] << 16) | (b[o + 7] << 24));

		Span<byte> c = stackalloc byte[16];
		Expand565( c0, c[..4] );
		Expand565( c1, c[4..8] );
		if ( !bc1 || c0 > c1 )
		{
			for ( int i = 0; i < 3; i++ )
			{
				c[8 + i] = (byte)((2 * c[i] + c[4 + i]) / 3);
				c[12 + i] = (byte)((c[i] + 2 * c[4 + i]) / 3);
			}
			c[11] = c[15] = 255;
		}
		else
		{
			for ( int i = 0; i < 3; i++ )
			{
				byte mid = (byte)((c[i] + c[4 + i]) / 2);
				c[8 + i] = mid;
				c[12 + i] = mid;
			}
			c[11] = 255; c[15] = 0;
		}

		for ( int i = 0; i < 16; i++ )
		{
			int sel = (int)((bits >> (2 * i)) & 3);
			int dst = i * 4, s = sel * 4;
			px[dst] = c[s]; px[dst + 1] = c[s + 1]; px[dst + 2] = c[s + 2];
			if ( bc1 ) px[dst + 3] = c[s + 3];
		}
	}

	static void DecodeExplicitAlpha( byte[] b, int o, byte[] px )
	{
		for ( int i = 0; i < 16; i++ )
		{
			int nibble = (b[o + i / 2] >> ((i % 2) * 4)) & 0xF;
			px[i * 4 + 3] = (byte)(nibble * 17);
		}
	}

	static void DecodeInterpAlpha( byte[] b, int o, byte[] px )
	{
		byte a0 = b[o], a1 = b[o + 1];
		Span<byte> a = stackalloc byte[8];
		a[0] = a0; a[1] = a1;
		if ( a0 > a1 )
		{
			for ( int i = 1; i <= 6; i++ ) a[1 + i] = (byte)(((7 - i) * a0 + i * a1) / 7);
		}
		else
		{
			for ( int i = 1; i <= 4; i++ ) a[1 + i] = (byte)(((5 - i) * a0 + i * a1) / 5);
			a[6] = 0; a[7] = 255;
		}

		ulong bits = 0;
		for ( int i = 0; i < 6; i++ ) bits |= (ulong)b[o + 2 + i] << (8 * i);
		for ( int i = 0; i < 16; i++ )
			px[i * 4 + 3] = a[(int)((bits >> (3 * i)) & 7)];
	}

	static void Expand565( ushort c, Span<byte> out4 )
	{
		int r = (c >> 11) & 0x1F, g = (c >> 5) & 0x3F, bl = c & 0x1F;
		out4[0] = (byte)((r << 3) | (r >> 2));
		out4[1] = (byte)((g << 2) | (g >> 4));
		out4[2] = (byte)((bl << 3) | (bl >> 2));
		out4[3] = 255;
	}

	static byte[] SwizzleARGB( byte[] src, int off, int w, int h )
	{
		var dst = new byte[w * h * 4];
		for ( int i = 0; i < w * h; i++ )
		{
			int s = off + i * 4, d = i * 4;
			dst[d] = src[s + 2]; dst[d + 1] = src[s + 1]; dst[d + 2] = src[s]; dst[d + 3] = src[s + 3];
		}
		return dst;
	}

	static uint ReadU32( byte[] b, int o ) => (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24));
}

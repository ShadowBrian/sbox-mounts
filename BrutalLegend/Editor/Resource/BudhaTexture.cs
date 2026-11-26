using System;

namespace Sandbox.Mounting.BrutalLegend;

/// <summary>
/// Builds s&box textures from Brütal Legend <c>Texture</c> entries. A Buddha texture payload is a
/// small engine header followed by a DDS image; we locate the DDS, decode it to RGBA and upload
/// (mirrors the Skyrim mount's approach, which avoids compressed-texture GPU issues).
/// </summary>
public static class BudhaTexture
{
	/// <summary>Slices the DDS image out of a texture payload (strips the Buddha header).</summary>
	public static byte[] ExtractDds( byte[] blob )
	{
		int off = IndexOf( blob, "DDS " );
		return off < 0 ? null : blob[off..];
	}

	public static Texture BuildTexture( BudhaEntry entry )
	{
		var blob = entry.GetData();
		var dds = ExtractDds( blob );
		if ( dds is null || dds.Length < 128 )
			return null;

		int width = DdsDecoder.ReadWidth( dds );
		int height = DdsDecoder.ReadHeight( dds );
		var rgba = DdsDecoder.DecodeToRgba( dds );

		return Texture.Create( width, height )
			.WithData( rgba )
			.WithMips()
			.WithName( entry.FullPath )
			.Finish();
	}

		static int IndexOf( byte[] hay, string needle )
	{
		var n = System.Text.Encoding.ASCII.GetBytes( needle );
		for ( int i = 0; i + n.Length <= hay.Length; i++ )
		{
			int k = 0;
			while ( k < n.Length && hay[i + k] == n[k] ) k++;
			if ( k == n.Length ) return i;
		}
		return -1;
	}
}

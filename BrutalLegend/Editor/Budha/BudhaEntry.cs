namespace Sandbox.Mounting.BrutalLegend;

/// <summary>A single file entry inside a Brütal Legend <c>.~h</c>/<c>.~p</c> pack.</summary>
public class BudhaEntry
{
	internal BudhaArchive Archive { get; set; }

	/// <summary>Logical path from the archive name pool, e.g. <c>Textures/Eddie/Eddie_Body</c>.</summary>
	public string FullPath { get; set; }

	/// <summary>File-type name: "Texture", "MeshSet", "Material", ...</summary>
	public string TypeName { get; set; }

	/// <summary>True if the uncompressed payload begins with a 4-byte length prefix.</summary>
	public bool HasSizeHeader { get; set; }

	internal long Offset { get; set; }
	internal int Size { get; set; }
	internal int ContentSize { get; set; }
	internal int CompressFlag { get; set; }

	public bool Compressed { get; set; }

	/// <summary>Extract this file's decoded content bytes (decompressed, size-header stripped).</summary>
	public byte[] GetData() => Archive.ExtractFileData( this );

	public System.IO.MemoryStream GetDataStream() => new( GetData() );

	/// <summary>
	/// If <paramref name="path"/> names an LOD variant (<c>..._lod1</c>..<c>..._lod7</c>), returns its base path.
	/// </summary>
	public static bool TryGetLodBaseName( string path, out string baseName )
	{
		baseName = null;
		if ( string.IsNullOrEmpty( path ) ) return false;

		int i = path.LastIndexOf( "_lod", System.StringComparison.OrdinalIgnoreCase );
		if ( i <= 0 || path.Length - i != 5 ) return false;

		char digit = path[^1];
		if ( digit < '1' || digit > '7' ) return false;

		baseName = path[..i];
		return true;
	}

	public override string ToString() => FullPath;
}

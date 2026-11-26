using System;
using System.IO;
using System.IO.Compression;
using System.Text;

namespace Sandbox.Mounting.BrutalLegend;

/// <summary>
/// Reads a Brütal Legend (Buddha engine) archive pair: <c>NAME.~h</c> (file allocation table) +
/// <c>NAME.~p</c> (concatenated payload). Big-endian container, bit-packed 16-byte file index,
/// zlib-compressed entries. Format per the decompiled Double Fine Tool (doublefinetool).
/// </summary>
public sealed class BudhaArchive : IDisposable
{
	// Types whose uncompressed payload begins with a 4-byte length prefix (DoubleFineTool.exe.config).
	static readonly HashSet<string> SizeHeaderTypes = new( StringComparer.OrdinalIgnoreCase )
	{
		"Texture","MeshSet","Material","Cutscene","StringTable","GibData","Outfit",
		"AudioProgrammerReport","AudioEnvironment","AudioWavbankMarkers","AudioWavebankData",
		"SimulationData","Stance","ComboPose","ComboAnim","AmbMeshDefinition","Climate","Weather",
		"OceanData","TerrainMaterial","LootLookup","LobbyRulesAttributes","DLCLobbyAttributes","Buff",
		"MechItem","SpawnManagerAttributes","ShiftStory","SpawnEvent","VidSubtitles","Effect",
		"RigidBodyEventData","EffectTable","Story","LevelData","DUIMovie","ResourceBuildStamp",
		"Chatter","MusicSet","WarChest","Experience","GameSettings","LoadingBackgroundSets",
		"RegimentChallengeTab","AchievementRequirementData","CameraSettings","DamageResponseTable",
		"ControllerConfig","LevelList","LootTableInfo","AmbientVignettes","Loadout","DriverOutfit",
		"PilotList","RichPresenceInfo","MissionData","BuffEffectTable","BuffEffectsData",
		"VisualTypeDefinitions","CommonLineCodes","FlashConfig","InputAliases","AttachmentPointTable",
	};

	public List<BudhaEntry> Files { get; } = new();

	readonly FileStream _payload;

	BudhaArchive( string hPath )
	{
		_payload = File.OpenRead( System.IO.Path.ChangeExtension( hPath, ".~p" ) );
		using var br = new BinaryReader( File.OpenRead( hPath ), Encoding.ASCII, leaveOpen: false );
		Parse( br );
	}

	public static BudhaArchive Open( string hPath ) => new( hPath );

	public void Dispose() => _payload.Dispose();

	/// <summary>
	/// Reads an entry's full decompressed payload. For Mesh/Rig the payload is
	/// [metadata][data] and <c>ContentSize</c> covers only the trailing data, so we must return
	/// the whole blob and let each parser locate its sections (BlMesh reads meta@0 + geo@tail,
	/// BudhaTexture finds "DDS ", text resources are scanned for fields).
	/// </summary>
	public byte[] ExtractFileData( BudhaEntry e )
	{
		var packed = new byte[e.Size];
		_payload.Seek( e.Offset, SeekOrigin.Begin );
		_payload.ReadExactly( packed, 0, e.Size );
		return e.Compressed ? Inflate( packed ) : packed;
	}

	void Parse( BinaryReader br )
	{
		ReadBytes( br, 4 );                                  // Magic "dfpf"
		var ver = ReadBytes( br, 4 );                        // BL: 02 00 00 00
		long fileTypeOffset = (long)ReadU64BE( br );
		long fileNameOffset = (long)ReadU64BE( br );
		int fileTypeCount = ReadI32BE( br );
		ReadI32BE( br );                                     // FileNameLength
		int fileCount = ReadI32BE( br );
		ReadBytes( br, 4 );                                  // Delim1
		ReadU64BE( br );                                     // UnknownOffset
		ReadU64BE( br );                                     // DataFooterOffset
		long fileIndexOffset = (long)ReadU64BE( br );
		long footerOffset1 = (long)ReadU64BE( br );
		ReadU64BE( br );                                     // FooterOffset2
		ReadI32BE( br );                                     // UnknownCount
		ReadBytes( br, 4 );                                  // Delim2

		bool v5 = ver[0] == 5;                               // Stacking layout; BL uses v2 (Costume Quest)
		bool compressedMask8 = v5;                           // v5 compressed bit = 0x8, v2 = 0x4

		// File-type table.
		br.BaseStream.Seek( fileTypeOffset, SeekOrigin.Begin );
		var types = new string[fileTypeCount];
		for ( int i = 0; i < fileTypeCount; i++ )
		{
			int len = ReadI32BE( br );
			types[i] = Encoding.ASCII.GetString( ReadBytes( br, len ) ).TrimEnd( '\0' );
			ReadBytes( br, 12 );                             // 3 unknown int32
		}

		// File index: FileCount x 16-byte bit-packed records.
		br.BaseStream.Seek( fileIndexOffset, SeekOrigin.Begin );
		var records = new (int contentSize, int size, long offset, int nameOff, int typeIdx, int compFlag)[fileCount];
		for ( int i = 0; i < fileCount; i++ )
		{
			var d = ReadBytes( br, 16 );
			if ( v5 )
			{
				records[i] = ( (int)BitExtract( d, 0, 24 ), (int)BitExtract( d, 93, 23 ),
					(long)BitExtract( d, 64, 29 ), (int)BitExtract( d, 24, 21 ), (int)BitExtract( d, 116, 8 ),
					(int)BitExtract( d, 124, 4 ) );
			}
			else
			{
				records[i] = ( (int)BitExtract( d, 0, 23 ), (int)BitExtract( d, 41, 22 ),
					(long)BitExtract( d, 63, 30 ), (int)BitExtract( d, 96, 21 ), (int)BitExtract( d, 117, 7 ),
					(int)BitExtract( d, 124, 4 ) );
			}
		}

		// Resolve names from the string pool.
		foreach ( var r in records )
		{
			br.BaseStream.Seek( fileNameOffset + r.nameOff, SeekOrigin.Begin );
			var name = ReadCString( br, footerOffset1 );
			if ( name is null ) continue;

			int typeIdx = v5 ? (r.typeIdx >> 1) : r.typeIdx;
			var typeName = (typeIdx >= 0 && typeIdx < types.Length) ? types[typeIdx] : "";

			Files.Add( new BudhaEntry
			{
				Archive = this,
				FullPath = name,
				TypeName = typeName,
				HasSizeHeader = SizeHeaderTypes.Contains( typeName ),
				Offset = r.offset,
				Size = r.size,
				ContentSize = r.contentSize,
				CompressFlag = r.compFlag,
				Compressed = (r.compFlag & (compressedMask8 ? 0x8 : 0x4)) != 0,
			} );
		}
	}

	// ---- bit / endian helpers (container is big-endian, MSB-first bit packing) ----

	static long BitExtract( byte[] d, int bitOffset, int bitLen )
	{
		long v = 0;
		for ( int i = 0; i < bitLen; i++ )
		{
			int bit = bitOffset + i;
			int m = 7 - (bit % 8);
			v = (v << 1) | (uint)((d[bit / 8] >> m) & 1);
		}
		return v;
	}

	static byte[] ReadBytes( BinaryReader r, int n )
	{
		var b = r.ReadBytes( n );
		if ( b.Length != n ) throw new EndOfStreamException( "Unexpected end of ~h" );
		return b;
	}

	static int ReadI32BE( BinaryReader r ) => (int)ReadU32BE( r );

	static uint ReadU32BE( BinaryReader r )
	{
		var b = ReadBytes( r, 4 );
		return (uint)((b[0] << 24) | (b[1] << 16) | (b[2] << 8) | b[3]);
	}

	static ulong ReadU64BE( BinaryReader r )
	{
		var b = ReadBytes( r, 8 );
		ulong v = 0;
		for ( int i = 0; i < 8; i++ ) v = (v << 8) | b[i];
		return v;
	}

	static string ReadCString( BinaryReader r, long limit )
	{
		if ( r.BaseStream.Position >= limit ) return null;
		var sb = new StringBuilder();
		while ( r.BaseStream.Position < limit )
		{
			int c = r.BaseStream.ReadByte();
			if ( c < 0 ) break;
			if ( c == 0xCC ) return sb.Length == 0 ? null : sb.ToString();   // end-of-pool marker
			if ( c == 0x00 ) return sb.ToString();
			sb.Append( (char)c );
		}
		return sb.Length == 0 ? null : sb.ToString();
	}

	static byte[] Inflate( byte[] data )
	{
		if ( data.Length == 0 ) return Array.Empty<byte>();
		using var input = new MemoryStream( data );
		Stream zs = IsZlib( data )
			? new ZLibStream( input, CompressionMode.Decompress )
			: new DeflateStream( input, CompressionMode.Decompress );
		using var output = new MemoryStream();
		zs.CopyTo( output );
		return output.ToArray();
	}

	static bool IsZlib( byte[] d ) =>
		d.Length >= 2 && d[0] == 0x78 && (d[1] == 0x01 || d[1] == 0x5E || d[1] == 0x9C || d[1] == 0xDA);
}

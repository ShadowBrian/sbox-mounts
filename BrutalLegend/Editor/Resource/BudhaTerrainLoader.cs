using System;
using System.Globalization;
using System.Text;
using Sandbox.ModelEditor;

namespace Sandbox.Mounting.BrutalLegend;

/// <summary>
/// Builds one merged terrain model for a Brütal Legend <c>LevelData</c> map.
///
/// The world is a 512x512 tile grid. Each tile references a 512x512 uint16 heightfield,
/// a center and a scale; this loader decodes every referenced heightfield, merges all tiles
/// into a single mesh (decimated adaptively), recenters the map on the origin, and adds a
/// mesh collision representation so the character controller can walk on it.
///
/// Material blending is intentionally simple for now: a neutral lit terrain material. The
/// per-tile "blend"/"occlusion" textures and TerrainMaterial layers can be layered on later.
/// </summary>
public sealed class BudhaTerrainLoader : ResourceLoader<BrutalLegendMount>
{
	readonly BudhaEntry _entry;

	const int HeightfieldSize = 512;
	const int HeightfieldPixels = HeightfieldSize * HeightfieldSize;

	// Havok heightfields are R16_UNORM; LevelData Scale.Y is the tile's vertical extent.
	// (A 13-bit interpretation made a 64-tile map 6,000+ ft tall; 16-bit gives ~760 ft.)
	const float HeightQuantization = 65535f;

	// Cap the merged vertex count; the render mesh and collision mesh share it.
	const long MaxMergedVertices = 700_000;

	sealed class Tile
	{
		public Vector3 Center;
		public Vector3 Scale;
		public ushort[] Heights;
		public string VisualPath;
	}

	[GameData( "brutal_world_objects" )]
	public sealed class BrutalWorldObjectsData
	{
		public List<BrutalWorldObjectData> Objects { get; set; } = new();
	}

	public sealed class BrutalWorldObjectData
	{
		public string Mesh { get; set; }
		public Vector3 Position { get; set; }
		public Rotation Rotation { get; set; }
		public float Scale { get; set; }
	}

	public BudhaTerrainLoader( BudhaEntry entry )
	{
		_entry = entry;
	}

	protected override object Load()
	{
		if ( !TryParseTiles( out var tiles ) )
			return null;

		if ( tiles.Count == 0 )
			return null;

		// Choose a decimation step that keeps a full-map mesh and its collision affordable.
		int step = 8;
		int side = HeightfieldSize / step + 1;

		while ( step < 64 )
		{
			side = HeightfieldSize / step + 1;
			long estimate = (long)tiles.Count * side * side;
			if ( estimate <= MaxMergedVertices )
				break;
			step *= 2;
		}

		side = HeightfieldSize / step + 1;

		// Recenter the map on the origin.
		float minX = tiles.Min( t => t.Center.x - t.Scale.x * 0.5f );
		float maxX = tiles.Max( t => t.Center.x + t.Scale.x * 0.5f );
		float minZ = tiles.Min( t => t.Center.z - t.Scale.z * 0.5f );
		float maxZ = tiles.Max( t => t.Center.z + t.Scale.z * 0.5f );
		var mapCenter = new Vector3( (minX + maxX) * 0.5f, 0f, (minZ + maxZ) * 0.5f );

		// Tiles are independently sampled, so their shared edge heights differ by a few raw units.
		// Average those edges so the merged mesh has no visible cracks.
		var edgeHeights = new Dictionary<(int x, int z), (float sum, int count)>();
		foreach ( var tile in tiles )
		{
			for ( int z = 0; z < side; z++ )
			{
				float fz = z / (float)(side - 1);
				int hz = (int)MathF.Round( fz * (HeightfieldSize - 1) );

				for ( int x = 0; x < side; x++ )
				{
					if ( x != 0 && x != side - 1 && z != 0 && z != side - 1 )
						continue;

					float fx = x / (float)(side - 1);
					int hx = (int)MathF.Round( fx * (HeightfieldSize - 1) );
					float bx = tile.Center.x + (fx - 0.5f) * tile.Scale.x - mapCenter.x;
					float bz = tile.Center.z + (fz - 0.5f) * tile.Scale.z - mapCenter.z;
					float by = tile.Center.y + SampleHeight( tile, hx, hz );

					var key = ((int)MathF.Round( bx * 100f ), (int)MathF.Round( bz * 100f ));
					if ( edgeHeights.TryGetValue( key, out var entry ) )
						edgeHeights[key] = (entry.sum + by, entry.count + 1);
					else
						edgeHeights[key] = (by, 1);
				}
			}
		}

		var vertices = new List<SimpleVertex>( tiles.Count * side * side );
		var positions = new List<Vector3>( tiles.Count * side * side );
		var indices = new List<int>( tiles.Count * (side - 1) * (side - 1) * 6 );
		var placements = BuildPlacements( tiles, mapCenter );

		foreach ( var tile in tiles )
		{
			int baseIndex = vertices.Count;

			for ( int z = 0; z < side; z++ )
			{
				float fz = z / (float)(side - 1);
				int hz = (int)MathF.Round( fz * (HeightfieldSize - 1) );

				for ( int x = 0; x < side; x++ )
				{
					float fx = x / (float)(side - 1);
					int hx = (int)MathF.Round( fx * (HeightfieldSize - 1) );

					float h = SampleHeight( tile, hx, hz );

					float bx = tile.Center.x + (fx - 0.5f) * tile.Scale.x - mapCenter.x;
					float bz = tile.Center.z + (fz - 0.5f) * tile.Scale.z - mapCenter.z;
					float by = tile.Center.y + h;

					if ( x == 0 || x == side - 1 || z == 0 || z == side - 1 )
					{
						var key = ((int)MathF.Round( bx * 100f ), (int)MathF.Round( bz * 100f ));
						if ( edgeHeights.TryGetValue( key, out var edge ) && edge.count > 0 )
							by = edge.sum / edge.count;
					}

					// Central-difference normal in tile space.
					float worldPerPixelX = tile.Scale.x / (HeightfieldSize - 1);
					float worldPerPixelZ = tile.Scale.z / (HeightfieldSize - 1);
					float dhdx = (SampleHeight( tile, hx + step, hz ) - SampleHeight( tile, hx - step, hz ))
						/ (2f * step * worldPerPixelX);
					float dhdz = (SampleHeight( tile, hx, hz + step ) - SampleHeight( tile, hx, hz - step ))
						/ (2f * step * worldPerPixelZ);
					var normal = new Vector3( -dhdx, 1f, -dhdz ).Normal;

					var position = ConvertPos( new Vector3( bx, by, bz ) );
					var uv = new Vector2( bx, bz ) / 64f;

					vertices.Add( new SimpleVertex(
						position,
						ConvertDir( normal ),
						ConvertDir( Vector3.Right ),
						uv ) );
					positions.Add( position );
				}
			}

			for ( int z = 0; z < side - 1; z++ )
			{
				for ( int x = 0; x < side - 1; x++ )
				{
					int a = baseIndex + z * side + x;
					int b = a + 1;
					int c = a + side;
					int d = c + 1;

					indices.Add( a ); indices.Add( c ); indices.Add( b );
					indices.Add( b ); indices.Add( c ); indices.Add( d );
				}
			}
		}

		var material = CreateTerrainMaterial();
		var mesh = new Mesh( material );
		var vertexArray = vertices.ToArray();
		var indexArray = indices.ToArray();

		mesh.CreateVertexBuffer( vertexArray.Length, vertexArray );
		mesh.CreateIndexBuffer( indexArray.Length, indexArray );
		var bounds = BBox.FromPoints( positions.ToArray() );
		mesh.Bounds = bounds;

		var builder = Model.Builder.WithName( _entry.FullPath + "_terrain" );
		builder.AddMesh( mesh );

		bool collisionBuilt = false;
		try
		{
			builder.AddCollisionMesh( positions, indices );
			collisionBuilt = true;
		}
		catch ( Exception e )
		{
			Log.Warning( $"Terrain collision build failed {_entry.FullPath}: {e.Message}" );
		}

		if ( placements.Count > 0 )
		{
			builder.WithData<BrutalWorldObjectsData>( new BrutalWorldObjectsData { Objects = placements } );
		}

		var model = builder.Create();
		Log.Info( $"Built terrain {_entry.FullPath}: tiles={tiles.Count} step={step} verts={vertexArray.Length} tris={indexArray.Length / 3} collision={collisionBuilt} objects={placements.Count} bounds={bounds}" );
		return model;
	}

	bool TryParseTiles( out List<Tile> tiles )
	{
		tiles = new List<Tile>();

		byte[] data;
		try
		{
			data = _entry.GetData();
		}
		catch ( Exception e )
		{
			Log.Warning( $"LevelData read failed {_entry.FullPath}: {e.Message}" );
			return false;
		}

		if ( data is null || data.Length < 16 )
			return false;

		var text = Encoding.ASCII.GetString( data );
		int bodyStart = text.IndexOf( "LevelData{", StringComparison.Ordinal );
		if ( bodyStart >= 0 )
			text = text[bodyStart..];

		int search = 0;
		while ( true )
		{
			search = text.IndexOf( "TileData{", search, StringComparison.Ordinal );
			if ( search < 0 )
				break;

			int end = text.IndexOf( "};", search, StringComparison.Ordinal );
			if ( end < 0 )
				break;

			var body = text[(search + "TileData{".Length)..end];
			search = end + 2;

			var heightRef = Field( body, "Heightfield" );
			var centerRef = Field( body, "Center" );
			var scaleRef = Field( body, "Scale" );
			var visualRef = Field( body, "VisualObjects" );

			if ( string.IsNullOrWhiteSpace( heightRef ) || heightRef == "@" )
				continue;
			if ( !TryParseVec3( centerRef, out var center ) || !TryParseVec3( scaleRef, out var scale ) )
				continue;

			var heightEntry = Host.GetFile( heightRef.TrimStart( '@' ), "Heightfield" );
			if ( heightEntry is null )
				continue;

			byte[] heightBytes;
			try
			{
				heightBytes = heightEntry.GetData();
			}
			catch ( Exception e )
			{
				Log.Warning( $"Heightfield read failed {heightEntry.FullPath}: {e.Message}" );
				continue;
			}

			if ( heightBytes is null || heightBytes.Length < HeightfieldPixels * 2 )
				continue;

			var heights = new ushort[HeightfieldPixels];
			Buffer.BlockCopy( heightBytes, 0, heights, 0, HeightfieldPixels * 2 );

			tiles.Add( new Tile
			{
				Center = center,
				Scale = scale,
				Heights = heights,
				VisualPath = visualRef?.TrimStart( '@' )
			} );
		}

		return true;
	}

	List<BrutalWorldObjectData> BuildPlacements( List<Tile> tiles, Vector3 mapCenter )
	{
		var placements = new List<BrutalWorldObjectData>();

		foreach ( var tile in tiles )
		{
			if ( string.IsNullOrWhiteSpace( tile.VisualPath ) )
				continue;

			var visualEntry = Host.GetFile( tile.VisualPath, "ObjectData" );
			if ( visualEntry is null )
				continue;

			byte[] data;
			try
			{
				data = visualEntry.GetData();
			}
			catch
			{
				continue;
			}

			if ( data is null )
				continue;

			int p = 0;
			while ( p + 4 <= data.Length )
			{
				int len = BitConverter.ToInt32( data, p );
				if ( len < 4 || len > 512 || p + 4 + len + 36 > data.Length )
				{
					p++;
					continue;
				}

				var meshPath = Encoding.ASCII.GetString( data, p + 4, len ).TrimEnd( '\0' );
				if ( meshPath.EndsWith( ".mesh", StringComparison.OrdinalIgnoreCase ) )
					meshPath = meshPath[..^5];

				if ( !IsMeshPath( meshPath ) || Host.GetFile( meshPath, "Mesh" ) is null )
				{
					p++;
					continue;
				}

				int q = p + 4 + len;
				var position = ReadVec3( data, q + 4 );
				var rotation = ReadQuat( data, q + 16 );
				float scale = BitConverter.ToSingle( data, q + 32 );

				float quatLength = rotation.x * rotation.x + rotation.y * rotation.y
					+ rotation.z * rotation.z + rotation.w * rotation.w;

				if ( !float.IsFinite( position.x ) || !float.IsFinite( position.y ) || !float.IsFinite( position.z )
				  || !float.IsFinite( quatLength ) || quatLength < 0.5f || quatLength > 2f
				  || !float.IsFinite( scale ) )
				{
					p++;
					continue;
				}

				if ( scale <= 0.0001f )
					scale = 1f;
				else
					scale = Math.Clamp( scale, 0.01f, 100f );

				var worldPos = ConvertPos( new Vector3( position.x - mapCenter.x, position.y, position.z - mapCenter.z ) );
				var worldRot = ConvertRotation( new Rotation( rotation.x, rotation.y, rotation.z, rotation.w ).Normal );

				placements.Add( new BrutalWorldObjectData
				{
					Mesh = meshPath,
					Position = worldPos,
					Rotation = worldRot,
					Scale = scale
				} );

				p = q + 36;
			}
		}

		return placements;
	}

	static bool IsMeshPath( string path )
	{
		if ( string.IsNullOrWhiteSpace( path ) || path.Length < 4 || path.Contains( '\0' ) )
			return false;

		var prefixes = new[]
		{
			"environments/", "buildings/", "characters/", "vehicles/", "props/",
			"dlc/", "dlc1/", "dlc2/", "effects/", "particles/", "rendering/"
		};

		foreach ( var prefix in prefixes )
			if ( path.StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) )
				return true;

		return false;
	}

	static Vector3 ReadVec3( byte[] data, int offset )
	{
		return new Vector3(
			BitConverter.ToSingle( data, offset ),
			BitConverter.ToSingle( data, offset + 4 ),
			BitConverter.ToSingle( data, offset + 8 ) );
	}

	static Rotation ReadQuat( byte[] data, int offset )
	{
		return new Rotation(
			BitConverter.ToSingle( data, offset ),
			BitConverter.ToSingle( data, offset + 4 ),
			BitConverter.ToSingle( data, offset + 8 ),
			BitConverter.ToSingle( data, offset + 12 ) );
	}

	static float SampleHeight( Tile tile, int x, int z )
	{
		x = Math.Clamp( x, 0, HeightfieldSize - 1 );
		z = Math.Clamp( z, 0, HeightfieldSize - 1 );
		return tile.Heights[z * HeightfieldSize + x] / HeightQuantization * tile.Scale.y;
	}

	static string Field( string body, string key )
	{
		foreach ( var part in body.Split( ';' ) )
		{
			int eq = part.IndexOf( '=' );
			if ( eq < 0 )
				continue;

			if ( part[..eq].Trim().Equals( key, StringComparison.OrdinalIgnoreCase ) )
				return part[(eq + 1)..].Trim();
		}

		return null;
	}

	static bool TryParseVec3( string value, out Vector3 result )
	{
		result = default;

		if ( string.IsNullOrWhiteSpace( value ) )
			return false;

		value = value.Trim().Trim( '<', '>' );
		var parts = value.Split( ',' );
		if ( parts.Length < 3 )
			return false;

		if ( !float.TryParse( parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var x )
		  || !float.TryParse( parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var y )
		  || !float.TryParse( parts[2].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var z ) )
			return false;

		result = new Vector3( x, y, z );
		return true;
	}

	// Same Buddha (meters) -> s&box (inches, +X fwd, +Y left, +Z up) conversion as the model loader.
	static readonly Rotation ModelRotation = new Angles( 0, 90, 90 ).ToRotation();
	static readonly Rotation YawFix = new Angles( 0, 0, 0 ).ToRotation();
	static Vector3 ConvertPos( Vector3 v ) => YawFix * (ModelRotation * (v * 39.37f));
	static Vector3 ConvertDir( Vector3 v ) => (YawFix * (ModelRotation * v)).Normal;
	static Rotation ConvertRotation( Rotation r ) => YawFix * (ModelRotation * r);

	Material CreateTerrainMaterial()
	{
		var texture = FindTerrainTexture();
		string matName = "brutal/terrain_" + _entry.FullPath.Replace( '/', '_' ).Replace( '\\', '_' ).ToLowerInvariant();

		try
		{
			var material = Material.Create( matName, "shaders/brutal.shader" );
			if ( material is not null )
			{
				material.Set( "g_tColor", texture ?? Texture.White );
				material.Set( "g_vColorTint", texture is null ? new Vector3( 0.42f, 0.40f, 0.34f ) : Vector3.One );
				material.Set( "g_flRoughness", 0.92f );
				material.Set( "g_flMetalness", 0.0f );
				material.Set( "g_flHasNormal", 0.0f );
				material.Set( "g_flAlphaTest", 0.0f );
				material.Set( "g_flOpacity", 1.0f );
				return material;
			}
		}
		catch ( Exception e )
		{
			Log.Warning( $"Terrain material create failed: {e.Message}" );
		}

		return Material.Load( "materials/dev/reflectivity_30.vmat" );
	}

	Texture FindTerrainTexture()
	{
		var matEntry = Host.GetFilesUnder( "dlc1/terrainmaterials", "TerrainMaterial" ).FirstOrDefault()
			?? Host.GetFilesUnder( "", "TerrainMaterial" ).FirstOrDefault();

		if ( matEntry is null )
			return null;

		try
		{
			var text = Encoding.ASCII.GetString( matEntry.GetData() );
			var textureRef = Field( text, "DiffuseTexture" );
			if ( string.IsNullOrWhiteSpace( textureRef ) || textureRef == "@" )
				return null;

			var textureEntry = Host.GetFile( textureRef.TrimStart( '@' ), "Texture" );
			if ( textureEntry is null )
				return null;

			return BudhaTexture.BuildTexture( textureEntry );
		}
		catch ( Exception e )
		{
			Log.Warning( $"Terrain texture load failed {matEntry.FullPath}: {e.Message}" );
			return null;
		}
	}
}

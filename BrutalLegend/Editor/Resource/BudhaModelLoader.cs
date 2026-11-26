using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace Sandbox.Mounting.BrutalLegend;

/// <summary>
/// Loads a Brütal Legend <c>Mesh</c> entry and converts it to an s&box Model via Model.Builder.
/// Each submesh becomes an s&box Mesh whose material is built from the Buddha Material entry:
/// DiffuseTexture→albedo, Normal1Texture→normal, GlossTexture→roughness, plus metalness/emission
/// scalars, bound to the "shaders/brutal.shader" PBR shader.
/// </summary>
[StructLayout( LayoutKind.Sequential )]
public struct SkinnedVertex
{
	[VertexLayout.Position]
	public Vector3 position;

	[VertexLayout.Normal]
	public Vector3 normal;

	[VertexLayout.Tangent]
	public Vector3 tangent;

	[VertexLayout.TexCoord]
	public Vector2 texcoord;

	[VertexLayout.BlendIndices]
	public Color32 blendIndices;

	[VertexLayout.BlendWeight]
	public Color32 blendWeights;

	public static readonly VertexAttribute[] Layout =
	[
		new( VertexAttributeType.Position, VertexAttributeFormat.Float32, 3 ),
		new( VertexAttributeType.Normal, VertexAttributeFormat.Float32, 3 ),
		new( VertexAttributeType.Tangent, VertexAttributeFormat.Float32, 3 ),
		new( VertexAttributeType.TexCoord, VertexAttributeFormat.Float32, 2 ),
		new( VertexAttributeType.BlendIndices, VertexAttributeFormat.UInt8, 4 ),
		new( VertexAttributeType.BlendWeights, VertexAttributeFormat.UInt8, 4 )
	];

	public SkinnedVertex( Vector3 pos, Vector3 norm, Vector3 tan, Vector2 uv, Color32 bi, Color32 bw )
	{
		position = pos;
		normal = norm;
		tangent = tan;
		texcoord = uv;
		blendIndices = bi;
		blendWeights = bw;
	}
}

public class BudhaModelLoader : ResourceLoader<BrutalLegendMount>
{
	readonly BudhaEntry _entry;

	public BudhaModelLoader( BudhaEntry entry )
	{
		_entry = entry;
	}

	protected override object Load()
	{
		BlMesh mesh;
		try
		{
			mesh = BlMesh.Parse( _entry.GetData() );
		}
		catch ( Exception e )
		{
			Log.Warning( $"Mesh parse failed {_entry.FullPath}: {e.Message}" );
			return null;
		}

		if ( mesh.Submeshes.Count == 0 )
			return null;

		// A _lodN mesh is a detail level of its base model and shares that model's rig.
		bool isLod = BudhaEntry.TryGetLodBaseName( _entry.FullPath, out string basePath );
		if ( !isLod )
			basePath = _entry.FullPath;

		BlRig rig = null;
		var rigEntry = Host.GetFile( basePath, "Rig" );
		if ( rigEntry != null )
		{
			try { rig = BlRig.Parse( rigEntry.GetData() ); }
			catch ( Exception ex ) { Log.Warning( $"Rig parse failed {_entry.FullPath}: {ex.Message}" ); }
		}

		var builder = Model.Builder.WithName( Path );

		if ( rig != null && rig.Bones.Length > 0 )
		{
			// Bone.LocalTransform in a cooked s&box model is the model-space (root-relative)
			// bind transform, not parent-relative - GetBoneTransform() returns the same value.
			// AddBone therefore takes model-space bind transforms.
			foreach ( var bone in rig.Bones )
			{
				string parentName = (bone.ParentIndex >= 0 && bone.ParentIndex < rig.Bones.Length)
					? rig.Bones[bone.ParentIndex].Name : null;
				builder.AddBone( bone.Name, ConvertPos( bone.Position ), ConvertRotation( bone.Rotation ), parentName );
			}
		}

		int AddSubmeshes( BlMesh source, int lod )
		{
			int added = 0;

			foreach ( var sub in source.Submeshes )
			{
				if ( sub.Positions is null || sub.Indices is null || sub.VertCount == 0 )
					continue;

				// `nomaterial` is a placeholder surface (its texture is the magenta/yellow debug
				// checker). Inside hair it is a missing hair card, so reuse the hair sheet material
				// instead of leaving a magenta streak or a hole.
				var resolvedMatName = ResolveMaterialName( source, sub );
				if ( resolvedMatName is null )
					continue;

				try
				{
					var material = CreateMaterial( source, sub, resolvedMatName ) ?? Material.Load( "materials/dev/reflectivity_30.vmat" );
					var sboxMesh = new Mesh( material );
					var indices = sub.FaceType == 3 ? ExpandStrip( sub.Indices ) : sub.Indices;

					if ( rig != null && rig.Bones.Length > 0 && sub.IsSkinned && sub.BlendIndices != null )
					{
						var vertices = new SkinnedVertex[sub.VertCount];
						for ( int i = 0; i < sub.VertCount; i++ )
						{
							byte[] bi = sub.BlendIndices[i];
							byte[] bw = sub.BlendWeights[i];
							int b0 = (bi != null && bi.Length > 0 && bi[0] < sub.BoneIDS.Length) ? sub.BoneIDS[bi[0]] : 0;
							int b1 = (bi != null && bi.Length > 1 && bi[1] < sub.BoneIDS.Length) ? sub.BoneIDS[bi[1]] : 0;
							int b2 = (bi != null && bi.Length > 2 && bi[2] < sub.BoneIDS.Length) ? sub.BoneIDS[bi[2]] : 0;
							int b3 = (bi != null && bi.Length > 3 && bi[3] < sub.BoneIDS.Length) ? sub.BoneIDS[bi[3]] : 0;

							vertices[i] = new SkinnedVertex(
								ConvertPos( sub.Positions[i] ),
								ConvertDir( sub.Normals[i] ),
								ConvertDir( sub.Tangents[i] ),
								sub.UVs[i],
								new Color32( (byte)b0, (byte)b1, (byte)b2, (byte)b3 ),
								NormalizeWeights( bw )
							);
						}

						sboxMesh.CreateVertexBuffer( sub.VertCount, vertices );
						sboxMesh.CreateIndexBuffer( indices.Length, indices );
						var pts = new Vector3[sub.VertCount];
						for ( int k = 0; k < sub.VertCount; k++ ) pts[k] = vertices[k].position;
						sboxMesh.Bounds = BBox.FromPoints( pts );
					}
					else
					{
						var vertices = new SimpleVertex[sub.VertCount];
						for ( int i = 0; i < sub.VertCount; i++ )
						{
							vertices[i] = new SimpleVertex(
								ConvertPos( sub.Positions[i] ),
								ConvertDir( sub.Normals[i] ),
								ConvertDir( sub.Tangents[i] ),
								sub.UVs[i] );
						}

						sboxMesh.CreateVertexBuffer( sub.VertCount, vertices );
						sboxMesh.CreateIndexBuffer( indices.Length, indices );
						var pts = new Vector3[sub.VertCount];
						for ( int k = 0; k < sub.VertCount; k++ ) pts[k] = vertices[k].position;
						sboxMesh.Bounds = BBox.FromPoints( pts );
					}

					builder.AddMesh( sboxMesh, lod );
					added++;
				}
				catch ( Exception e )
				{
					Log.Warning( $"Submesh build failed {_entry.FullPath}#{sub.MatID}: {e.Message}" );
				}
			}

			return added;
		}

		// LOD0 is the base mesh; fold _lod1.._lod7 into the same model, like a cooked vmdl's chain.
		int built = AddSubmeshes( mesh, 0 );

		if ( !isLod )
		{
			for ( int lod = 1; lod <= 7; lod++ )
			{
				var lodEntry = Host.GetFile( basePath + "_lod" + lod, "Mesh" );
				if ( lodEntry is null )
					continue;

				BlMesh lodMesh;
				try
				{
					lodMesh = BlMesh.Parse( lodEntry.GetData() );
				}
				catch ( Exception e )
				{
					Log.Warning( $"LOD{lod} parse failed {lodEntry.FullPath}: {e.Message}" );
					continue;
				}

				if ( lodMesh.Submeshes.Count > 0 )
					built += AddSubmeshes( lodMesh, lod );
			}
		}

		if ( built == 0 )
			return null;

		if ( !isLod && rig != null && rig.Bones.Length > 0 )
			AddAnimations( builder, rig );

		try
		{
			return builder.Create();
		}
		catch ( Exception e )
		{
			Log.Warning( $"Model.Create failed {_entry.FullPath}: {e.Message}" );
			return null;
		}
	}

	/// <summary>
	/// Finds the character's <c>AnimResource</c> entries and embeds them as sequences on this
	/// model. Animations live next to the model folder (e.g. <c>character/animations/*</c>) while
	/// the mesh may sit in <c>character/rig/*</c> or <c>character/model/*</c>.
	/// </summary>
	void AddAnimations( ModelBuilder builder, BlRig rig )
	{
		var path = _entry.FullPath.Replace( '\\', '/' );
		int slash = path.LastIndexOf( '/' );
		if ( slash < 0 ) return;

		var dir = path[..slash];
		var prefixes = new List<string> { dir + "/animations/" };

		int parentSlash = dir.LastIndexOf( '/' );
		if ( parentSlash >= 0 )
			prefixes.Add( dir[..parentSlash] + "/animations/" );

		var seen = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
		int added = 0;

		foreach ( var prefix in prefixes )
		{
			foreach ( var entry in Host.GetFilesUnder( prefix, "AnimResource" ) )
			{
				if ( !seen.Add( entry.FullPath ) )
					continue;

				BudhaAnimation data;
				try
				{
					data = BudhaAnimation.Parse( entry.GetData(), System.IO.Path.GetFileName( entry.FullPath ) );
				}
				catch ( Exception e )
				{
					Log.Warning( $"Anim parse failed {entry.FullPath}: {e.Message}" );
					continue;
				}

				if ( data is null || data.MaxBoneIndex < 0 || data.MaxBoneIndex >= rig.Bones.Length )
					continue;

				try
				{
					if ( data.AppendTo( builder, rig, ConvertPos, ConvertRotation ) )
						added++;
				}
				catch ( Exception e )
				{
					Log.Warning( $"Anim build failed {entry.FullPath}: {e.Message}" );
				}
			}
		}

		if ( added > 0 )
			Log.Info( $"Added {added} animations to {_entry.FullPath}" );
	}

	/// <summary>
	/// Resolves a submesh's material name. <c>nomaterial</c> is a placeholder in the exporter;
	/// inside a character mesh it is a missing hair card, so reuse the hair-sheet material
	/// instead of drawing the magenta/yellow debug texture. Returns null when it should be skipped.
	/// </summary>
	string ResolveMaterialName( BlMesh source, BlSubmesh sub )
	{
		var name = (sub.MatID >= 0 && sub.MatID < source.Materials.Length) ? source.Materials[sub.MatID] : null;
		if ( string.IsNullOrEmpty( name ) || !name.Contains( "nomaterial", StringComparison.OrdinalIgnoreCase ) )
			return name;

		string sheet = null, hair = null;
		foreach ( var m in source.Materials )
		{
			if ( sheet is null && m.Contains( "hairsheet", StringComparison.OrdinalIgnoreCase ) ) sheet = m;
			if ( hair is null && m.EndsWith( "/hair", StringComparison.OrdinalIgnoreCase ) ) hair = m;
		}

		return sheet ?? hair;
	}

	Material CreateMaterial( BlMesh mesh, BlSubmesh sub, string matNameOverride = null )
	{
		string matName = matNameOverride ?? ((sub.MatID >= 0 && sub.MatID < mesh.Materials.Length) ? mesh.Materials[sub.MatID] : null);
		string matLower = matName?.ToLowerInvariant() ?? "";
		Texture diffuse = null, normal = null, hairRamp = null;
		bool normalDxt5 = false;
		bool hasDiffuse = false;
		Vector3 diffuseColor = Vector3.One;
		float envIntensity = 0f;
		float roughness = 0.5f, metalness = 0f, emission = 0f, opacity = 1f, alphaTest = 0f;
		string shaderPath = "shaders/brutal.shader";
		Vector3 hairColor = new Vector3( 0.35f, 0.30f, 0.25f );

		if ( matName != null )
		{
			var matEntry = Host.GetFile( matName, "Material" );
			if ( matEntry != null )
			{
				var text = System.Text.Encoding.ASCII.GetString( matEntry.GetData() );
				string shaderName = ParseRef( text, "ShaderName" ) ?? "";
				string blendMode = ParseRef( text, "BlendMode" ) ?? "";

				diffuse = LoadTex( ParseRef( text, "DiffuseTexture" ) );
				hasDiffuse = diffuse != null;
				if ( diffuse == null )
					diffuse = LoadTex( ParseRef( text, "Layer1Texture" ) );
				diffuseColor = ParseVec3( text, "DiffuseColor", Vector3.One );

				var normalName = ParseRef( text, "Normal1Texture" );
				normal = LoadTex( normalName );
				normalDxt5 = IsDxt5( normalName );
				roughness = ParseFloat( text, "Roughness", 0.5f );
				emission = ParseFloat( text, "Incandescence", 0f );
				float transparency = ParseFloat( text, "Transparency", 1f );
				envIntensity = ParseFloat( text, "EnvironmentMapIntensity", 0f );

				// 1. Additive materials (eye cornea/reflection layers): blend, no depth write.
				if ( blendMode.Equals( "kBLEND_Additive", StringComparison.OrdinalIgnoreCase ) )
				{
					shaderPath = "shaders/brutal_additive.shader";
					diffuse = LoadTex( ParseRef( text, "EnvironmentMapTexture" ) )
					       ?? LoadTex( ParseRef( text, "IncandescenceTexture" ) )
					       ?? LoadTex( ParseRef( text, "DiffuseTexture" ) );
					normal = null;
					metalness = 0.0f;
					roughness = 0.0f;
					emission = 0.0f;
					float additiveOpacity = envIntensity > 0f ? envIntensity : 0.35f;
					opacity = diffuse != null ? Math.Clamp( additiveOpacity, 0.05f, 1.0f ) : 0.0f;
					alphaTest = 0.0f;
				}
				// 2. Hair materials (RenderHair in Buddha engine). Keep alpha cutout (the sheet
				//    geometry is authored as cards), but clip only the almost-empty texels so
				//    soft sideburn strands survive.
				else if ( shaderName.Equals( "RenderHair", StringComparison.OrdinalIgnoreCase ) ||
				          matLower.Contains( "hair" ) || matLower.Contains( "hairsheet" ) )
				{
					shaderPath = "shaders/brutal_hair.shader";
					metalness = 0.0f;
					roughness = 0.80f;
					// Extract exact per-character hair color and gradient ramp
					hairColor = ParseVec3( text, "DiffuseColor", new Vector3( 0.35f, 0.30f, 0.25f ) );
					hairRamp = LoadTex( ParseRef( text, "Ramp1Texture" ) );
					alphaTest = matLower.Contains( "hairsheet" ) ? 0.01f : 0.0f;
				}
				// 2. Translucent materials (kBLEND_Alpha: veils, wedding dress, glass, tears)
				else if ( blendMode.Equals( "kBLEND_Alpha", StringComparison.OrdinalIgnoreCase ) ||
				          transparency < 0.95f ||
				          matLower.Contains( "veil" ) ||
				          matLower.Contains( "glass" ) ||
				          matLower.Contains( "trans" ) )
				{
					shaderPath = "shaders/brutal_translucent.shader";
					opacity = transparency < 0.95f ? transparency : 0.8f;
					metalness = 0.0f;
				}
				// 3. Binary alpha cutout materials (kBLEND_BinaryAlpha: lace, aprons, chainlink, fringes)
				else if ( blendMode.Equals( "kBLEND_BinaryAlpha", StringComparison.OrdinalIgnoreCase ) ||
				          matLower.Contains( "cutout" ) ||
				          matLower.Contains( "apron" ) ||
				          matLower.Contains( "lace" ) )
				{
					shaderPath = "shaders/brutal.shader";
					alphaTest = 0.35f;
					metalness = 0.0f;
				}
				// 4. Standard opaque materials (metals vs dielectrics)
				else
				{
					bool isMetal = matLower.Contains( "chrome" ) ||
					               matLower.Contains( "metal" ) ||
					               matLower.Contains( "axemetal" ) ||
					               matLower.Contains( "buckle" ) ||
					               matLower.Contains( "zipper" ) ||
					               matLower.Contains( "button" ) ||
					               matLower.Contains( "strings" ) ||
					               matLower.Contains( "lighter" ) ||
					               envIntensity >= 1.0f;

					if ( isMetal )
					{
						metalness = 1.0f;
						roughness = Math.Clamp( roughness, 0.03f, 0.20f );
						// A metal's base colour is its reflectance. Chrome's DiffuseColor is
						// black because Buddha's metal shading pulls the colour from its
						// environment map; pure white here reads as flat white. Use a neutral
						// chrome reflectance so the sun/env specular has something to tint.
						if ( !hasDiffuse )
						{
							diffuse = null;
							if ( diffuseColor.LengthSquared < 0.001f )
								diffuseColor = new Vector3( 0.58f, 0.60f, 0.64f );
						}
					}
					else
					{
						metalness = 0.0f;
						roughness = Math.Clamp( roughness, 0.35f, 0.85f );
					}
				}
			}
		}

		Material material;
		try
		{
			material = Material.Create( $"brutal/{matName ?? "default"}", shaderPath );
		}
		catch
		{
			return Material.Load( "materials/dev/reflectivity_30.vmat" );
		}

		if ( material is null )
			return Material.Load( "materials/dev/reflectivity_30.vmat" );

		material.Set( "g_tColor", diffuse ?? Texture.White );
		if ( normal != null ) material.Set( "g_tNormal", normal );
		material.Set( "g_flRoughness", roughness );
		material.Set( "g_flMetalness", metalness );
		material.Set( "g_flEmission", emission );
		material.Set( "g_flOpacity", opacity );
		material.Set( "g_flAlphaTest", alphaTest );
		material.Set( "g_vColorTint", diffuseColor );
		material.Set( "g_flHasNormal", normal != null ? 1f : 0f );
		material.Set( "g_bDxt5nm", normalDxt5 ? 1f : 0f );
		if ( shaderPath == "shaders/brutal_hair.shader" )
		{
			material.Set( "g_vHairColor", hairColor );
			material.Set( "g_flAlphaTest", alphaTest );
			material.Set( "g_tRamp", hairRamp ?? Texture.White );
		}
		return material;
	}

	bool IsDxt5( string name )
	{
		if ( string.IsNullOrEmpty( name ) ) return false;
		var e = Host.GetFile( name, "Texture" );
		if ( e is null ) return false;
		try
		{
			var d = e.GetData();
			for ( int i = 0; i + 128 <= d.Length; i++ )
			{
				if ( d[i] == 0x44 && d[i + 1] == 0x44 && d[i + 2] == 0x53 && d[i + 3] == 0x20 )
					return d[i + 84] == (byte)'D' && d[i + 85] == (byte)'X' &&
					       d[i + 86] == (byte)'T' && d[i + 87] == (byte)'5';
			}
		}
		catch { }
		return false;
	}

	Texture LoadTex( string name )
	{
		if ( string.IsNullOrEmpty( name ) ) return null;
		var e = Host.GetFile( name, "Texture" );
		if ( e == null ) return null;
		try { return BudhaTexture.BuildTexture( e ); }
		catch ( Exception ex ) { Log.Warning( $"Texture {name}: {ex.Message}" ); return null; }
	}

	static Texture _flatNormal;
	static Texture FlatNormal => _flatNormal ??= Texture.Create( 1, 1 ).WithData( new byte[] { 128, 128, 255, 255 } ).Finish();

	static float ParseFloat( string text, string key, float def )
	{
		var s = ParseRef( text, key )?.Trim();
		if ( string.IsNullOrEmpty( s ) ) return def;
		return float.TryParse( s, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var f ) ? f : def;
	}

	static Vector3 ParseVec3( string text, string key, Vector3 def )
	{
		var s = ParseRef( text, key )?.Trim().Trim( '<', '>' );
		if ( string.IsNullOrEmpty( s ) ) return def;
		var p = s.Split( ',' );
		if ( p.Length < 3 ) return def;
		var ci = System.Globalization.CultureInfo.InvariantCulture;
		if ( float.TryParse( p[0].Trim(), System.Globalization.NumberStyles.Float, ci, out var x )
		  && float.TryParse( p[1].Trim(), System.Globalization.NumberStyles.Float, ci, out var y )
		  && float.TryParse( p[2].Trim(), System.Globalization.NumberStyles.Float, ci, out var z ) )
			return new Vector3( x, y, z );
		return def;
	}

	/// <summary>Extracts a <c>Key=@value</c> (or <c>Key=value</c>) field from a Buddha text resource.</summary>
	static string ParseRef( string text, string key )
	{
		int i = 0;
		while ( true )
		{
			i = text.IndexOf( key + "=", i, StringComparison.OrdinalIgnoreCase );
			if ( i < 0 ) return null;
			// require a boundary before the key so "Roughness" doesn't match "SpecularRoughness"
			if ( i == 0 || text[i - 1] == ';' || text[i - 1] == '{' || text[i - 1] == '[' )
				break;
			i += key.Length;
		}
		i += key.Length + 1;
		int end = text.IndexOfAny( AnySemiBrace, i );
		if ( end < 0 ) end = text.Length;
		var v = text[i..end].Trim();
		return v.StartsWith( "@" ) ? v[1..] : v;
	}
	static readonly char[] AnySemiBrace = { ';', '}', ']' };

	// Buddha (meters) → s&box (inches, +X fwd, +Y left, +Z up): scale ×39.37, rotate (0,90,90),
	// then correct the residual 90° left yaw with a −90° world yaw.
	static readonly Rotation ModelRotation = new Angles( 0, 90, 90 ).ToRotation();
	static readonly Rotation YawFix = new Angles( 0, 0, 0 ).ToRotation();
	static Vector3 ConvertPos( Vector3 v ) => YawFix * (ModelRotation * (v * 39.37f));
	static Vector3 ConvertDir( Vector3 v ) => YawFix * (ModelRotation * v );
	static Rotation ConvertRotation( Rotation r ) => YawFix * (ModelRotation * r);

	static Color32 NormalizeWeights( byte[] w )
	{
		if ( w == null || w.Length < 4 ) return new Color32( 255, 0, 0, 0 );
		int sum = w[0] + w[1] + w[2] + w[3];
		if ( sum == 0 ) return new Color32( 255, 0, 0, 0 );
		if ( sum == 255 ) return new Color32( w[0], w[1], w[2], w[3] );
		int w0 = (w[0] * 255 + (sum / 2)) / sum;
		int w1 = (w[1] * 255 + (sum / 2)) / sum;
		int w2 = (w[2] * 255 + (sum / 2)) / sum;
		int w3 = (w[3] * 255 + (sum / 2)) / sum;
		int diff = 255 - (w0 + w1 + w2 + w3);
		w0 += diff;
		return new Color32( (byte)Math.Clamp( w0, 0, 255 ), (byte)Math.Clamp( w1, 0, 255 ), (byte)Math.Clamp( w2, 0, 255 ), (byte)Math.Clamp( w3, 0, 255 ) );
	}

	/// <summary>Expands a triangle-strip index buffer into a triangle list with consistent winding.</summary>
	static int[] ExpandStrip( int[] s )
	{
		if ( s.Length < 3 ) return System.Array.Empty<int>();
		var list = new List<int>();
		for ( int k = 0; k + 2 < s.Length; k++ )
		{
			int a = s[k], b = s[k + 1], c = s[k + 2];
			if ( a == b || b == c || a == c ) continue; // skip degenerate
			if ( (k & 1) == 0 ) { list.Add( a ); list.Add( b ); list.Add( c ); }
			else { list.Add( b ); list.Add( a ); list.Add( c ); }
		}
		return list.ToArray();
	}
}

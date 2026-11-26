HEADER
{
	Description = "Brütal Legend hair shader — double-sided strand rendering with per-character color ramps, strand normals, and smooth alpha cutout";
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
    Forward();
    Depth();
}

COMMON
{
	#include "common/shared.hlsl"
}

struct VertexInput
{
	#include "common/vertexinput.hlsl"
};

struct PixelInput
{
	#include "common/pixelinput.hlsl"
};

VS
{
	#include "common/vertex.hlsl"

	PixelInput MainVs( VertexInput v )
	{
		PixelInput i = ProcessVertex( v );
		return FinalizeVertex( i );
	}
}

PS
{
	#include "common/pixel.hlsl"

	RenderState( CullMode, NONE );

	SamplerState g_sSampler0 < Filter( Anisotropic ); AddressU( WRAP ); AddressV( WRAP ); >;

	CreateInputTexture2D( Color, Srgb, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tColor < Channel( RGBA, Box( Color ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;

	CreateInputTexture2D( Normal, Linear, 8, "NormalizeNormal", "_normal", ",0/,0/0", Default4( 0.5, 0.5, 1, 1 ) );
	Texture2D g_tNormal < Channel( RGBA, Box( Normal ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;

	CreateInputTexture2D( Ramp, Srgb, 8, "None", "_ramp", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tRamp < Channel( RGBA, Box( Ramp ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;

	float3 g_vHairColor     < Attribute( "g_vHairColor" ); Default3( 0.5, 0.5, 0.5 ); >;
	float g_flRoughness     < Attribute( "g_flRoughness" ); Default( 0.70 ); >;
	float g_flAlphaTest     < Attribute( "g_flAlphaTest" ); Default( 0.05 ); >;
	float g_flHasNormal     < Attribute( "g_flHasNormal" ); Default( 1.0 ); >;
	float g_bDxt5nm         < Attribute( "g_bDxt5nm" ); Default( 1.0 ); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float2 uv = i.vTextureCoords.xy;
		float4 util = Tex2DS( g_tColor, g_sSampler0, uv );

		// Gentle alpha cutout — only discard the transparent background, keeping soft strands intact
		if ( g_flAlphaTest > 0.0f )
		{
			clip( util.a - g_flAlphaTest );
		}

		Material m = Material::Init();

		float3 worldNormal = i.vNormalWs;
		if ( g_flHasNormal > 0.5f )
		{
			// Buddha hair normal maps are BC3/DXT5nm: X in A, Y in G. Do not guess per-texel —
			// mixing RGB and DXT5nm decoding within one sheet produces shiny streaks.
			float4 normSample = Tex2DS( g_tNormal, g_sSampler0, uv );
			float3 nTS;
			if ( g_bDxt5nm > 0.5f )
			{
				nTS.x = normSample.a * 2.0 - 1.0;
				nTS.y = normSample.g * 2.0 - 1.0;
				nTS.z = sqrt( saturate( 1.0 - dot( nTS.xy, nTS.xy ) ) );
			}
			else
			{
				nTS = normSample.xyz * 2.0 - 1.0;
			}

			worldNormal = TransformNormal( nTS, i.vNormalWs, i.vTangentUWs, i.vTangentVWs );
		}

		m.Normal = worldNormal;
		m.WorldTangentU = i.vTangentUWs;
		m.WorldTangentV = i.vTangentVWs;
		m.TextureCoords = uv;

		// Sample the hair color ramp (defaults to white if no ramp), tinted by the per-character DiffuseColor
		float3 rampColor = Tex2DS( g_tRamp, g_sSampler0, float2( util.r, 0.5 ) ).rgb;
		float strandDepth = lerp( 0.85, 1.15, util.g );
		m.Albedo = rampColor * g_vHairColor * strandDepth;

		m.Roughness = saturate( g_flRoughness );
		m.Metalness = 0.0f;
		m.Emission = 0.0f;
		m.AmbientOcclusion = 1.0f;
		m.TintMask = 1.0f;
		m.Opacity = 1.0f;
		m.Transmission = 0.0f;

		return ShadingModelStandard::Shade( i, m );
	}
}

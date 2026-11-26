HEADER
{
	Description = "Brütal Legend material shader with Source 2 standard shading, roughness, metalness, and emission";
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

	float g_flRoughness     < Attribute( "g_flRoughness" ); Default( 0.5 ); >;
	float g_flMetalness     < Attribute( "g_flMetalness" ); Default( 0.0 ); >;
	float g_flEmission      < Attribute( "g_flEmission" ); Default( 0.0 ); >;
	float g_flAlphaTest     < Attribute( "g_flAlphaTest" ); Default( 0.0 ); >;
	float3 g_vColorTint     < Attribute( "g_vColorTint" ); Default3( 1.0, 1.0, 1.0 ); >;
	float g_flHasNormal     < Attribute( "g_flHasNormal" ); Default( 0.0 ); >;
	float g_bDxt5nm         < Attribute( "g_bDxt5nm" ); Default( 0.0 ); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		Material m = Material::Init( i );

		float2 uv = i.vTextureCoords.xy;
		float4 texColor = Tex2DS( g_tColor, g_sSampler0, uv );

		// Alpha cutout for binary alpha materials (lace, fences, torn edges, hair base)
		if ( g_flAlphaTest > 0.0f )
		{
			clip( texColor.a - g_flAlphaTest );
		}

		float3 worldNormal = i.vNormalWs;
		if ( g_flHasNormal > 0.5f )
		{
			float4 nSample = Tex2DS( g_tNormal, g_sSampler0, uv );
			float3 nTS;
			if ( g_bDxt5nm > 0.5f )
			{
				nTS.x = nSample.a * 2.0 - 1.0;
				nTS.y = nSample.g * 2.0 - 1.0;
				nTS.z = sqrt( saturate( 1.0 - dot( nTS.xy, nTS.xy ) ) );
			}
			else
			{
				nTS = nSample.xyz * 2.0 - 1.0;
			}
			worldNormal = TransformNormal( nTS, i.vNormalWs, i.vTangentUWs, i.vTangentVWs );
		}

		m.Albedo = texColor.rgb * g_vColorTint;
		m.Normal = worldNormal;
		m.WorldTangentU = i.vTangentUWs;
		m.WorldTangentV = i.vTangentVWs;
		m.TextureCoords = uv;

		m.Roughness = saturate( g_flRoughness );
		m.Metalness = saturate( g_flMetalness );
		m.Emission = texColor.rgb * g_flEmission;
		m.AmbientOcclusion = 1.0f;
		m.TintMask = 1.0f;
		m.Opacity = 1.0f;
		m.Transmission = 0.0f;

		return ShadingModelStandard::Shade( i, m );
	}
}

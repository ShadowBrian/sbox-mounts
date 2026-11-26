HEADER
{
	Description = "Brütal Legend translucent shader — alpha blended rendering for veils, dresses, glass, and tears";
}

FEATURES
{
	#include "common/features.hlsl"
}

MODES
{
    Forward();
}

COMMON
{
	#define BLEND_MODE_ALREADY_SET 1
	#define S_TRANSLUCENT 1
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
	#define DEPTH_STATE_ALREADY_SET 1
	#define BLEND_MODE_ALREADY_SET 1
	#define S_TRANSLUCENT 1

	#include "common/pixel.hlsl"

	RenderState( BlendEnable, true );
	RenderState( SrcBlend, SRC_ALPHA );
	RenderState( DstBlend, INV_SRC_ALPHA );
	RenderState( DepthWriteEnable, false );
	RenderState( CullMode, NONE );

	SamplerState g_sSampler0 < Filter( Anisotropic ); AddressU( WRAP ); AddressV( WRAP ); >;

	CreateInputTexture2D( Color, Srgb, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tColor < Channel( RGBA, Box( Color ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;

	CreateInputTexture2D( Normal, Linear, 8, "NormalizeNormal", "_normal", ",0/,0/0", Default4( 0.5, 0.5, 1, 1 ) );
	Texture2D g_tNormal < Channel( RGBA, Box( Normal ), Linear ); OutputFormat( DXT5 ); SrgbRead( false ); >;

	float g_flRoughness     < Attribute( "g_flRoughness" ); Default( 0.2 ); >;
	float g_flMetalness     < Attribute( "g_flMetalness" ); Default( 0.0 ); >;
	float g_flOpacity       < Attribute( "g_flOpacity" ); Default( 1.0 ); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		Material m = Material::Init();

		float2 uv = i.vTextureCoords.xy;
		float4 texColor = Tex2DS( g_tColor, g_sSampler0, uv );

		m.Albedo = texColor.rgb;
		m.Normal = i.vNormalWs;
		m.TextureCoords = uv;

		m.Roughness = saturate( g_flRoughness );
		m.Metalness = saturate( g_flMetalness );
		m.Emission = 0.0f;
		m.AmbientOcclusion = 1.0f;
		m.TintMask = 1.0f;
		m.Opacity = saturate( texColor.a * g_flOpacity );
		m.Transmission = 0.0f;

		return ShadingModelStandard::Shade( i, m );
	}
}

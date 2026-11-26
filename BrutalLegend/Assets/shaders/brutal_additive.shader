HEADER
{
	Description = "Brütal Legend additive shader — unlit additive layer for eye corneas/reflections";
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
	RenderState( DstBlend, ONE );
	RenderState( DepthWriteEnable, false );
	RenderState( CullMode, NONE );

	SamplerState g_sSampler0 < Filter( Anisotropic ); AddressU( WRAP ); AddressV( WRAP ); >;

	CreateInputTexture2D( Color, Srgb, 8, "None", "_color", ",0/,0/0", Default4( 1.00, 1.00, 1.00, 1.00 ) );
	Texture2D g_tColor < Channel( RGBA, Box( Color ), Srgb ); OutputFormat( DXT5 ); SrgbRead( true ); >;

	float g_flOpacity < Attribute( "g_flOpacity" ); Default( 0.35 ); >;

	float4 MainPs( PixelInput i ) : SV_Target0
	{
		float4 c = Tex2DS( g_tColor, g_sSampler0, i.vTextureCoords.xy );
		return float4( c.rgb * g_flOpacity, c.a * g_flOpacity );
	}
}

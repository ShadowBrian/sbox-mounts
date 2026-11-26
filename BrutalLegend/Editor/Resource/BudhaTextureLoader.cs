using System;

namespace Sandbox.Mounting.BrutalLegend;

/// <summary>Loads a Brütal Legend <c>Texture</c> entry into an s&box texture.</summary>
public class BudhaTextureLoader : ResourceLoader<BrutalLegendMount>
{
	readonly BudhaEntry _entry;

	public BudhaTextureLoader( BudhaEntry entry )
	{
		_entry = entry;
	}

	protected override object Load()
	{
		try
		{
			return BudhaTexture.BuildTexture( _entry );
		}
		catch ( Exception e )
		{
			Log.Warning( $"Failed to load texture {_entry.FullPath}: {e.Message}" );
			return null;
		}
	}
}

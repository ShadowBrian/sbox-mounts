using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using SysDir = System.IO.Directory;

namespace Sandbox.Mounting.BrutalLegend;

/// <summary>
/// Mounts Brütal Legend (Buddha engine) assets from the paired <c>.~h</c>/<c>.~p</c> packs in
/// <c>Win/Packs</c> into s&box. Resources are keyed by (name, type); a model is a <c>Mesh</c>
/// entry whose materials resolve to <c>Material</c> → <c>Texture</c> entries.
/// </summary>
public class BrutalLegendMount : BaseGameMount
{
	public override string Ident => "brutal_legend";
	public override string Title => "Brütal Legend";

	const long AppId = 225260;
	public override long? SteamAppId => AppId;

	public string PacksDirectory { get; private set; }

	readonly List<BudhaArchive> _archives = new();
	readonly List<BudhaEntry> _all = new();
	readonly Dictionary<string, BudhaEntry> _index = new( StringComparer.OrdinalIgnoreCase );

	protected override void Initialize( InitializeContext context )
	{
		if ( !context.IsAppInstalled( AppId ) )
			return;

		var appDir = context.GetAppDirectory( AppId );
		if ( string.IsNullOrEmpty( appDir ) )
			return;

		PacksDirectory = Path.Combine( appDir, "Win", "Packs" );
		if ( !SysDir.Exists( PacksDirectory ) )
			return;

		IsInstalled = true;
		Log.Info( $"Found Brütal Legend at: {appDir}" );
	}

	protected override Task Mount( MountContext context )
	{
		if ( !IsInstalled || string.IsNullOrEmpty( PacksDirectory ) )
			return Task.CompletedTask;

		OpenArchives();
		RegisterAssets( context );

		IsMounted = true;
		Log.Info( $"Registered {_all.Count} files from {_archives.Count} packs" );
		return Task.CompletedTask;
	}

	void OpenArchives()
	{
		var files = SysDir.GetFiles( PacksDirectory, "*.~h", SearchOption.TopDirectoryOnly );
		Array.Sort( files, StringComparer.OrdinalIgnoreCase );

		foreach ( var h in files )
		{
			try
			{
				var archive = BudhaArchive.Open( h );
				_archives.Add( archive );

				foreach ( var entry in archive.Files )
				{
					_all.Add( entry );
					_index[IndexKey( entry.FullPath, entry.TypeName )] = entry;
				}

				Log.Info( $"Opened pack: {Path.GetFileName( h )} ({archive.Files.Count} files)" );
			}
			catch ( Exception e )
			{
				Log.Warning( $"Failed to open pack {Path.GetFileName( h )}: {e.Message}" );
			}
		}
	}

	void RegisterAssets( MountContext context )
	{
		foreach ( var entry in _all )
		{
			switch ( entry.TypeName.ToLowerInvariant() )
			{
				case "mesh":
					// _lod1.._lod7 are folded into their base model's LOD chain.
					if ( BudhaEntry.TryGetLodBaseName( entry.FullPath, out var lodBase )
						&& _index.ContainsKey( IndexKey( lodBase, "mesh" ) ) )
						break;

					context.Add( ResourceType.Model, entry.FullPath, new BudhaModelLoader( entry ) );
					break;
				case "texture":
					context.Add( ResourceType.Texture, entry.FullPath, new BudhaTextureLoader( entry ) );
					break;
				case "leveldata":
					// Each LevelData manifest becomes one merged terrain model for the whole map.
					// Keep the model path distinct from the LevelData path so it appears as a
					// draggable Model asset (worlds/<map>/<map>_terrain.vmdl). Search the map
					// name in the asset browser to find it.
					context.Add( ResourceType.Model, entry.FullPath + "_terrain", new BudhaTerrainLoader( entry ) );
					break;
			}
		}
	}

	/// <summary>Look up a file by logical name and file-type name (both case-insensitive).</summary>
	public BudhaEntry GetFile( string name, string typeName )
	{
		return _index.TryGetValue( IndexKey( name, typeName ), out var entry ) ? entry : null;
	}

	/// <summary>Enumerate entries whose path starts with <paramref name="prefix"/> and whose type matches. An empty prefix matches all.</summary>
	public IEnumerable<BudhaEntry> GetFilesUnder( string prefix, string typeName )
	{
		prefix = (prefix ?? "").Replace( '\\', '/' );
		foreach ( var entry in _all )
		{
			if ( !string.Equals( entry.TypeName, typeName, StringComparison.OrdinalIgnoreCase ) )
				continue;
			if ( prefix.Length == 0 || entry.FullPath.Replace( '\\', '/' ).StartsWith( prefix, StringComparison.OrdinalIgnoreCase ) )
				yield return entry;
		}
	}

	static string IndexKey( string name, string typeName ) =>
		(name ?? "").Replace( '\\', '/' ).ToLowerInvariant() + "|" + (typeName ?? "").ToLowerInvariant();

	protected override void Shutdown()
	{
		foreach ( var a in _archives ) a.Dispose();
		_archives.Clear();
		_all.Clear();
		_index.Clear();
	}
}

using System;

namespace Sandbox.World;

/// <summary>
/// Loads a merged Brütal Legend terrain model (one per map) and sets up rendering + collision.
/// The map models are exposed by the mount as draggable Model assets, e.g.
/// <c>mount://brutal_legend/worlds/dlc1_1/dlc1_1_terrain.vmdl</c>. Assign one to <see cref="MapModel"/>.
/// </summary>
[Title( "Brütal World Map" )]
[Category( "World" )]
[Icon( "terrain" )]
public sealed class BrutalWorldMap : Component
{
	/// <summary>Merged terrain model for the map, from the mount's asset browser.</summary>
	[Property] public Model MapModel { get; set; }

	[Property] public bool AddCollision { get; set; } = true;

	/// <summary>Drop the scene's <c>a01_avatar</c> above the map so it lands on the terrain.</summary>
	[Property] public bool SpawnPlayerOnTop { get; set; } = true;
	[Property, Range( 32f, 2000f )] public float SpawnHeight { get; set; } = 300f;

	/// <summary>Maximum visual objects to spawn from the map data. 0 = unlimited. Large maps can contain 100k+.</summary>
	[Property, Range( 0, 20000 )] public int MaxObjects { get; set; } = 1500;

	protected override void OnStart()
	{
		var model = MapModel;
		if ( model is null || !model.IsValid )
		{
			Log.Warning( "BrutalWorldMap: assign a map terrain Model (e.g. worlds/dlc1_1/dlc1_1_terrain.vmdl)" );
			return;
		}

		var renderer = Components.Get<ModelRenderer>() ?? GameObject.AddComponent<ModelRenderer>();
		renderer.Model = model;

		if ( AddCollision )
		{
			var collider = Components.Get<ModelCollider>() ?? GameObject.AddComponent<ModelCollider>();
			collider.Model = model;
			collider.Static = true;
		}

		SpawnMapObjects( model );

		if ( SpawnPlayerOnTop )
		{
			var player = Scene.GetAllObjects( true ).FirstOrDefault( o => o.Name == "a01_avatar" );
			if ( player is not null )
			{
				player.WorldPosition = new Vector3( 0f, 0f, model.Bounds.Maxs.z + SpawnHeight );
				var cc = player.GetComponent<CharacterController>();
				if ( cc is not null ) cc.Velocity = Vector3.Zero;
			}
		}

		Log.Info( $"BrutalWorldMap: {model.Name} bounds={model.Bounds}" );
	}

	void SpawnMapObjects( Model model )
	{
		var data = model.GetData<BrutalWorldObjectsData>();
		if ( data?.Objects is null || data.Objects.Count == 0 )
			return;

		int spawned = 0;
		int skipped = 0;
		foreach ( var item in data.Objects )
		{
			if ( MaxObjects > 0 && spawned >= MaxObjects )
			{
				skipped = data.Objects.Count - spawned;
				break;
			}

			if ( string.IsNullOrWhiteSpace( item.Mesh ) )
				continue;

			var objectModel = Model.Load( "mount://brutal_legend/" + item.Mesh + ".vmdl" );
			if ( objectModel is null || !objectModel.IsValid )
				continue;

			var go = Scene.CreateObject();
			go.Name = System.IO.Path.GetFileNameWithoutExtension( item.Mesh );
			go.WorldPosition = item.Position;
			go.WorldRotation = item.Rotation;
			go.WorldScale = Vector3.One * Math.Clamp( item.Scale, 0.01f, 100f );

			var mr = go.AddComponent<ModelRenderer>();
			mr.Model = objectModel;
			spawned++;
		}

		Log.Info( $"BrutalWorldMap: spawned {spawned} map objects" + (skipped > 0 ? $" (skipped {skipped}, MaxObjects={MaxObjects})" : "") );
	}
}

using Sandbox.ModelEditor;

namespace Sandbox.World;

/// <summary>
/// Model data written by the terrain loader. The editor-side type uses the same
/// <c>[GameData("brutal_world_objects")]</c> key, so the JSON layout matches without a
/// cross-assembly reference.
/// </summary>
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

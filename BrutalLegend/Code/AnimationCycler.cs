using System;

/// <summary>
/// Cycles through every animation on a <see cref="SkinnedModelRenderer"/> by playing the model's
/// sequences directly, with the animation graph disabled. Add it to the same GameObject as the
/// renderer, or to a parent — it will find the renderer automatically.
/// </summary>
[Title( "Animation Cycler" )]
[Category( "Animation" )]
[Icon( "movie" )]
public sealed class AnimationCycler : Component
{
	/// <summary>The renderer to drive. Found automatically when left unset.</summary>
	[Property] public SkinnedModelRenderer Renderer { get; set; }

	/// <summary>How long each animation plays before advancing.</summary>
	[Property, Range( 0.25f, 30f )] public float SecondsPerAnimation { get; set; } = 2.5f;

	/// <summary>Whether each sequence loops while it is shown.</summary>
	[Property] public bool Loop { get; set; } = true;

	/// <summary>Start cycling as soon as the component is enabled.</summary>
	[Property] public bool StartAutomatically { get; set; } = true;

	/// <summary>Log every available animation name on start.</summary>
	[Property] public bool LogAnimationList { get; set; }

	/// <summary>The animation currently being played.</summary>
	[Property, ReadOnly] public string CurrentAnimation { get; set; } = "";

	readonly List<string> _names = new();
	int _index = -1;
	float _nextAt;

	protected override void OnEnabled()
	{
		if ( !Renderer.IsValid() )
			Renderer = Components.Get<SkinnedModelRenderer>( FindMode.EverythingInSelfAndDescendants );

		Refresh();

		if ( StartAutomatically && _names.Count > 0 )
			PlayNext();
	}

	protected override void OnUpdate()
	{
		if ( !Renderer.IsValid() )
			return;

		// A model is often assigned after OnEnabled (prefabs, editor setup), so retry until we
		// have a list. Once we have one, keep it — animations don't change at runtime.
		if ( _names.Count == 0 )
			Refresh();

		if ( !StartAutomatically || _names.Count <= 1 )
			return;

		if ( Time.Now < _nextAt )
			return;

		PlayNext();
	}

	void Refresh()
	{
		_names.Clear();

		if ( !Renderer.IsValid() || Renderer.Model is null )
			return;

		// Play sequences directly rather than through an animation graph.
		Renderer.UseAnimGraph = false;
		Renderer.PlayAnimationsInEditorScene = true;

		foreach ( var name in Renderer.Model.AnimationNames )
		{
			if ( !string.IsNullOrWhiteSpace( name ) )
				_names.Add( name );
		}

		// Models without baked animations may still expose animgraph sequences.
		if ( _names.Count == 0 && Renderer.Sequence is not null )
		{
			foreach ( var name in Renderer.Sequence.SequenceNames )
			{
				if ( !string.IsNullOrWhiteSpace( name ) )
					_names.Add( name );
			}
		}

		_index = -1;

		Log.Info( $"[AnimationCycler] {_names.Count} animation(s) on '{Renderer.Model.ResourcePath}'" );
		if ( LogAnimationList )
		{
			foreach ( var name in _names )
				Log.Info( "  " + name );
		}
	}

	void PlayNext()
	{
		if ( _names.Count == 0 )
			return;

		_index = ( _index + 1 ) % _names.Count;
		var name = _names[_index];

		Renderer.Sequence.Name = name;
		Renderer.Sequence.Looping = Loop;

		CurrentAnimation = name;
		_nextAt = Time.Now + SecondsPerAnimation;

		Log.Info( $"[AnimationCycler] {_index + 1}/{_names.Count}: {name}" );
	}
}

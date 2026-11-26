using System;

namespace Sandbox.Player;

/// <summary>
/// Basic third-person player built on <see cref="CharacterController"/>. Moves with WASD relative
/// to the camera, runs with Shift, jumps with Space, and drives the model's baked sequences directly
/// (walk/run/strafe/backpedal/idle/fall/land). The supplied model has no citizen animgraph.
/// </summary>
[Title( "Brütal Player Controller" )]
[Category( "Player" )]
[Icon( "sports_esports" )]
public sealed class BrutalPlayerController : Component
{
	[Property, Range( 0f, 500f )] public float WalkSpeed { get; set; } = 120f;
	[Property, Range( 0f, 800f )] public float RunSpeed { get; set; } = 260f;
	[Property, Range( 0f, 800f )] public float JumpSpeed { get; set; } = 320f;
	[Property, Range( 0f, 2000f )] public float Gravity { get; set; } = 800f;
	[Property, Range( 1f, 60f )] public float GroundAcceleration { get; set; } = 18f;
	[Property, Range( 0.1f, 20f )] public float AirAcceleration { get; set; } = 3f;

	[Property, Range( 32f, 96f )] public float EyeHeight { get; set; } = 62f;
	[Property, Range( 64f, 400f )] public float CameraDistance { get; set; } = 190f;
	[Property, Range( 0.1f, 4f )] public float MouseSensitivity { get; set; } = 1f;

	/// <summary>Renderer to drive. Found on this GameObject when unset.</summary>
	[Property] public SkinnedModelRenderer Renderer { get; set; }

	/// <summary>Camera to drive. Uses the scene's main camera when unset.</summary>
	[Property] public CameraComponent Camera { get; set; }

	/// <summary>
	/// Non-zero drives movement from code/AI instead of <see cref="Input.AnalogMove"/>. X is forward,
	/// Y is left, values are clamped to unit length. Leave zero for normal player input.
	/// </summary>
	[Property] public Vector3 InputOverride { get; set; }

	/// <summary>Force the run gait. Useful for AI and testing.</summary>
	[Property] public bool RunOverride { get; set; }

	CharacterController _cc;
	SkinnedModelRenderer _renderer;
	CameraComponent _camera;

	Angles _eyeAngles;
	HashSet<string> _animations;
	readonly HashSet<string> _warnedMissing = new();
	float _landTimer;
	bool _wasOnGround;
	float _fallSpeed;

	protected override void OnStart()
	{
		_cc = Components.Get<CharacterController>();
		_renderer = Renderer ?? Components.Get<SkinnedModelRenderer>();
		_camera = Camera ?? Scene.GetAllComponents<CameraComponent>().FirstOrDefault( x => x.IsMainCamera );

		if ( _cc is null || _renderer is null )
		{
			Log.Error( $"{nameof( BrutalPlayerController )} on {GameObject.Name} needs a CharacterController and SkinnedModelRenderer" );
			Enabled = false;
			return;
		}

		_cc.Height = 72f;
		_cc.Radius = 16f;
		_cc.StepHeight = 18f;
		_cc.GroundAngle = 45f;
		_cc.Acceleration = 20f;

		_renderer.UseAnimGraph = false;
		_renderer.CreateBoneObjects = false;

		_wasOnGround = _cc.IsOnGround;
		_eyeAngles = new Angles( 0f, 0f, 0f );

		EnsureAnimations();
		Play( "relaxed_breathe", 1f, true );
	}

	protected override void OnUpdate()
	{
		if ( _cc is null || _renderer is null )
			return;

		UpdateLook();
		UpdateMovement();
		UpdateAnimation();
		UpdateCameraTransform();
	}

	void UpdateLook()
	{
		var look = Input.AnalogLook;

		_eyeAngles.pitch = Math.Clamp( _eyeAngles.pitch + look.pitch * MouseSensitivity, -80f, 80f );
		_eyeAngles.yaw += look.yaw * MouseSensitivity;
		_eyeAngles.roll = 0f;
	}

	void UpdateMovement()
	{
		var move = InputOverride != Vector3.Zero ? InputOverride : Input.AnalogMove;
		move = move.WithZ( 0f );

		if ( move.Length > 1f )
			move = move.Normal;

		var yaw = Rotation.FromYaw( _eyeAngles.yaw );
		var wishDirection = move * yaw;
		var running = RunOverride || Input.Down( "Run" );
		var targetSpeed = running ? RunSpeed : WalkSpeed;
		var wishVelocity = wishDirection * targetSpeed;

		var velocity = _cc.Velocity;
		var horizontal = velocity.WithZ( 0f );
		var acceleration = _cc.IsOnGround ? GroundAcceleration : AirAcceleration;
		var blend = 1f - MathF.Exp( -acceleration * Time.Delta );
		horizontal = Vector3.Lerp( horizontal, wishVelocity, blend );

		var vertical = velocity.z - Gravity * Time.Delta;
		if ( _cc.IsOnGround && vertical < 0f )
			vertical = -2f;
		_cc.Velocity = horizontal.WithZ( vertical );

		if ( Input.Pressed( "Jump" ) && _cc.IsOnGround )
			_cc.Punch( Vector3.Up * JumpSpeed );

		_cc.Move();

		// Yaw stays independent of the camera while idle. Once there is movement input, align to
		// the camera yaw so the strafe/backpedal clips read correctly.
		if ( move.Length > 0.1f )
		{
			var targetRotation = Rotation.FromYaw( _eyeAngles.yaw );
			WorldRotation = Rotation.Slerp( WorldRotation, targetRotation, 1f - MathF.Exp( -14f * Time.Delta ) );
		}
	}

	void UpdateAnimation()
	{
		EnsureAnimations();
		if ( _animations is null || _animations.Count == 0 )
			return;

		var grounded = _cc.IsOnGround;
		if ( !grounded )
			_fallSpeed = _cc.Velocity.z;

		if ( !_wasOnGround && grounded && _fallSpeed < -120f )
			_landTimer = 0.35f;
		_wasOnGround = grounded;

		if ( _landTimer > 0f )
			_landTimer -= Time.Delta;

		var move = InputOverride != Vector3.Zero ? InputOverride : Input.AnalogMove;
		move = move.WithZ( 0f );
		var running = RunOverride || Input.Down( "Run" );
		var speed = _cc.Velocity.WithZ( 0f ).Length;

		if ( !grounded )
		{
			Play( "fall_breathe", 1f, true );
			return;
		}

		if ( _landTimer > 0f )
		{
			Play( "fall_land", 1f, false );
			return;
		}

		if ( move.Length < 0.1f && speed < 8f )
		{
			Play( "relaxed_breathe", 1f, true );
			return;
		}

		var localX = -move.y;
		var localY = move.x;
		var strafing = MathF.Abs( localX ) > MathF.Abs( localY ) * 0.9f;

		string clip;
		if ( strafing )
		{
			clip = localX > 0f
				? (running ? "action_straferun_right" : "action_strafewalk_right")
				: (running ? "action_straferun_left" : "action_strafewalk_left");
		}
		else if ( localY < -0.1f )
		{
			clip = running ? "action_run_backward" : "action_walk_backward";
		}
		else
		{
			clip = running ? "action_run_forward" : "action_walk_forward";
		}

		var nominal = running ? RunSpeed : WalkSpeed;
		var rate = nominal <= 0f ? 1f : Math.Clamp( speed / nominal, 0.6f, 1.5f );
		Play( clip, rate, true );
	}

	void UpdateCameraTransform()
	{
		if ( _camera is null )
			return;

		var rotation = _eyeAngles.ToRotation();
		var eye = WorldPosition + Vector3.Up * EyeHeight;
		var desired = eye + rotation.Backward * CameraDistance;

		var trace = Scene.Trace.Ray( eye, desired ).IgnoreGameObjectHierarchy( GameObject ).Run();
		if ( trace.Hit )
			desired = trace.EndPosition + trace.Normal * 6f;

		_camera.WorldPosition = desired;
		_camera.WorldRotation = rotation;
	}

	void EnsureAnimations()
	{
		if ( _animations is not null )
			return;

		var model = _renderer?.Model;
		if ( model is null || !model.IsValid || model.AnimationCount == 0 )
			return;

		_animations = new HashSet<string>( model.AnimationNames, StringComparer.OrdinalIgnoreCase );
	}

	void Play( string clip, float rate, bool loop )
	{
		if ( _renderer.Model is null || _renderer.Model.AnimationCount == 0 )
			return;

		if ( _animations is not null && !_animations.Contains( clip ) )
		{
			if ( _warnedMissing.Add( clip ) )
				Log.Warning( $"Missing animation '{clip}' on {_renderer.Model?.Name}" );

			clip = _animations.Contains( "relaxed_breathe" )
				? "relaxed_breathe"
				: _renderer.Model.AnimationNames[0];
		}

		if ( !string.Equals( _renderer.Sequence.Name, clip, StringComparison.OrdinalIgnoreCase ) )
		{
			_renderer.Sequence.Blending = true;
			_renderer.Sequence.Name = clip;
			_renderer.Sequence.Time = 0f;
		}

		_renderer.Sequence.Looping = loop;
		_renderer.PlaybackRate = rate;
	}
}

using System;
using System.Collections.Generic;
using Ironfront.Net.Protocol;
using Ironfront.Net.Unity;
using UnityEngine;
public class CarHorn : MountedWeapon
{
	/// <summary>Metres from a heard honk to the horn it came from: a body sits on its vehicle.</summary>
	public const float HornSearchRadius = 6f;

	private static readonly List<CarHorn> live = new List<CarHorn>();

	/// <summary>
	/// A horn sounded where the game is decided (offline, or on the server): bots answer a call to
	/// come aboard (<see cref="BotHorn"/>). Never raised on a networked client.
	/// </summary>
	public static event Action<CarHorn> Honked;

	private Vehicle vehicle;

	/// <summary>The vehicle this horn is fitted to.</summary>
	public Vehicle Vehicle => vehicle;

	/// <summary>The actor at the wheel, or null.</summary>
	public Actor Driver => user;

	protected override void Awake()
	{
		base.Awake();
		// The id the wire knows a horn by, so every client can tell a honk from a shot and play
		// this vehicle's own horn (owner report 2026-10-04: the horn icon showed, nothing sounded,
		// and nobody else could have heard it anyway).
		NetworkId = WeaponIds.CAR_HORN;
		vehicle = GetComponentInParent<Vehicle>();
	}

	private void OnEnable()
	{
		live.Add(this);
	}

	private void OnDisable()
	{
		live.Remove(this);
	}

	/// <summary>
	/// Sounds the horn: reveals the occupant to AI, and makes a noise. V6 task 5.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Each line belongs to a different machine.</b> <c>user.Highlight()</c> is GAMEPLAY — it
	/// is what makes AI notice the vehicle (<c>Actor.cs</c>'s highlight action) — so it happens
	/// once, on the authority, and so does <see cref="Honked"/>. <c>audio.Play()</c> is cosmetic
	/// and is skipped on a dedicated server, where <c>Weapon.Awake</c>'s
	/// <c>GetComponent&lt;AudioSource&gt;()</c> returns null on a stripped prefab.
	/// </para>
	/// <para>
	/// <b>Heard by everyone near.</b> A player's honk reaches the other clients as the server's
	/// <c>S_WEAPON_FIRE</c> for the mounted weapon; a bot's has no input frame, so the server
	/// announces it here. Either way the message carries <see cref="WeaponIds.CAR_HORN"/>, and a
	/// client plays the horn of the vehicle the shooter sits in (<see cref="PlayNearest"/>).
	/// </para>
	/// <para>
	/// <b><c>lastFired</c> is never replicated.</b> <c>Time.time</c> is seconds since THIS PROCESS
	/// started, so the field is meaningless off-machine. The authoritative cooldown lives in the
	/// server's <c>WeaponRuntimeState.LastFiredTime</c>.
	/// </para>
	/// <para>
	/// <b>It spends no ammo and spawns no projectile</b> — this override skips <c>ammo--</c> and
	/// <c>AmmoChanged()</c> entirely, which is why <see cref="SpendsAmmoPerShot"/> says so and the
	/// server's clip of 1 stays a permanent 1.
	/// </para>
	/// </remarks>
	protected override void Shoot(Vector3 direction, bool useMuzzleDirection)
	{
		if (configuration.loud && NetWeaponAuthority.GameplayHalfRunsHere)
		{
			user.Highlight();
		}
		if (audio != null && NetWeaponAuthority.CosmeticHalfRunsHere)
		{
			audio.Play();
		}
		if (NetWeaponAuthority.GameplayHalfRunsHere)
		{
			if (user != null && user.aiControlled)
			{
				NetShotAnnouncements.AnnounceHorn(user.gameObject);
			}
			Honked?.Invoke(this);
		}
		lastFired = Time.time;
	}

	/// <summary>
	/// A bot's honk: the horn sounds now unless it sounded within its cooldown. Bots do not hold a
	/// trigger, so this is their way in (<see cref="BotHorn"/>).
	/// </summary>
	public bool Honk()
	{
		if (user == null || Time.time - lastFired < configuration.cooldown)
		{
			return false;
		}
		Shoot(Vector3.zero, true);
		return true;
	}

	/// <summary>
	/// Plays the horn nearest <paramref name="position"/>, within <see cref="HornSearchRadius"/>:
	/// a client hearing another player's or a bot's honk.
	/// </summary>
	public static void PlayNearest(Vector3 position)
	{
		CarHorn nearest = null;
		float best = HornSearchRadius * HornSearchRadius;
		for (int i = 0; i < live.Count; i++)
		{
			CarHorn horn = live[i];
			if (horn == null || horn.audio == null)
			{
				continue;
			}
			float d = (horn.transform.position - position).sqrMagnitude;
			if (d <= best)
			{
				best = d;
				nearest = horn;
			}
		}
		if (nearest != null)
		{
			nearest.audio.Play();
		}
	}

	/// <inheritdoc />
	protected override bool SpendsAmmoPerShot()
	{
		return false;
	}

	// Domain reload is off in the Editor: a subscriber from the last Play must not hear this one.
	[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
	private static void ResetOnLoad()
	{
		Honked = null;
		live.Clear();
	}
}

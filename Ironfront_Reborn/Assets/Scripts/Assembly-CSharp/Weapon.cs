using System;
using System.Collections.Generic;
using Ironfront.Net.Unity;
using UnityEngine;

public class Weapon : MonoBehaviour
{
	public enum Effectiveness
	{
		No = 0,
		Yes = 1,
		Preferred = 2
	}

	[Serializable]
	public class Configuration
	{
		public bool auto;

		public int ammo = 10;

		public int spareAmmo = 50;

		public int resupplyNumber = 10;

		public float reloadTime = 2f;

		public float cooldown = 0.2f;

		public float unholsterTime = 1.2f;

		/// <summary>
		/// Seconds between a throw being ordered and the projectile leaving the hand. V7-D7.
		/// </summary>
		/// <remarks>
		/// <para>
		/// <b>Matches the throw clip's animation-event time, and it is the one number in this
		/// phase that nothing in CI can discover.</b> <c>ThrowableWeapon.Fire</c> does not shoot
		/// -- it sets an Animator trigger, and an animation event calls
		/// <c>ThrowableWeapon.SpawnThrowable</c>. A headless server has no active Animator
		/// (<c>Weapon.HasActiveAnimator</c> already returns false there, and on a stripped prefab
		/// <c>GetComponent&lt;Animator&gt;()</c> returns null outright), so <b>today the server
		/// throws instantly and the client about 0.6 s later</b>. That divergence is not
		/// introduced by the network; the network is what makes it visible.
		/// </para>
		/// <para>
		/// <b>Why not run an Animator on the server.</b> A headless build strips the renderers
		/// the clip drives, the clip is authored for visuals rather than simulation, and it would
		/// make the release time an Editor-only fact no test can grade. <b>Why not trust the
		/// client's animation event.</b> It would make a client the author of the authoritative
		/// release tick, and a modified client throws instantly with nothing to check it against.
		/// A single authored constant is checkable by both sides.
		/// </para>
		/// <para>
		/// <b>Cost, stated plainly:</b> if this drifts from the clip's event time, the grenade
		/// leaves the hand at a visibly wrong point in the animation. That is a cosmetic error
		/// with a loud symptom, which is the right failure mode to trade a silent authority hole
		/// for.
		/// </para>
		/// </remarks>
		public float releaseDelay = 0.6f;

		public float aimFov = 50f;

		public bool forceAutoReload;

		public bool loud = true;

		public bool forceWorldAudioOutput;

		public Transform muzzle;

		public ParticleSystem muzzleFlash;

		public ParticleSystem casing;

		public int projectilesPerShot = 1;

		public GameObject projectilePrefab;

		public float kickback = 2f;

		public float randomKick = 0.2f;

		public float spread;

		public float snapMagnitude = 0.3f;

		public float snapDuration = 0.4f;

		public float snapFrequency = 4f;

		public bool aiIgnoreFriendlies;

		public float aiAllowedAimSpread = 1f;

		public Effectiveness effInfantry = Effectiveness.Yes;

		public Effectiveness effInfantryGroup;

		public Effectiveness effUnarmored = Effectiveness.Yes;

		public Effectiveness effArmored;

		public Effectiveness effAir;

		public float effectiveRange = 100f;
	}

	[NonSerialized]
	public Actor user;

	[NonSerialized]
	protected bool userIsPlayer;

	public Transform thirdPersonTransform;

	public Vector3 thirdPersonOffset = Vector3.zero;

	public float thirdPersonScale = 1f;

	public Configuration configuration;

	public AudioSource reverbAudio;

	public Sprite uiSprite;

	[NonSerialized]
	public int ammo;

	[NonSerialized]
	public byte NetworkId;

	[NonSerialized]
	public bool reloading;

	protected float lastFired;

	protected bool holdingFire;

	[NonSerialized]
	public bool unholstered;

	protected AudioSource audio;

	protected float weaponVolume = 1f;

	protected Action stopFireLoop = new Action(0.12f);

	[NonSerialized]
	public float projectileSpeed;

	[NonSerialized]
	public Animator animator;

	[NonSerialized]
	public int slot = -1;

	[NonSerialized]
	public bool aiming;

	private bool fireLoopPlaying;

	protected List<Renderer> renderers;

	protected virtual void Awake()
	{
		if (configuration.projectilePrefab != null)
		{
			projectileSpeed = configuration.projectilePrefab.GetComponent<Projectile>().configuration.speed;
		}
		else
		{
			projectileSpeed = 100f;
		}
		animator = GetComponent<Animator>();
		audio = GetComponent<AudioSource>();
	}

	protected virtual void Start()
	{
		// V6 task 3: one of the section 3.6 headless NREs. Weapon.Awake assigns `audio` from
		// GetComponent<AudioSource>(), which is null on a prefab whose audio was stripped for a
		// dedicated server -- and every branch below it then dies on the first weapon spawned.
		// Guarded rather than early-returned, because `ammo` below is GAMEPLAY and the server
		// needs it.
		if (audio != null)
		{
			weaponVolume = audio.volume;
			audio.loop = configuration.auto;
		}
		ammo = configuration.ammo;
		if (user != null)
		{
			if (user.aiControlled)
			{
				// The pitch draw is COSMETIC and is deliberately not taken on a server. It shares
				// UnityEngine.Random with TankTurret's recoil impulse, which is a server draw per
				// D4 -- taking a cosmetic draw here would advance that stream on one side only.
				if (audio != null)
				{
					audio.pitch *= UnityEngine.Random.Range(0.97f, 1.02f);
				}
				reverbAudio = null;
			}
			else if (reverbAudio != null)
			{
				reverbAudio.transform.parent = null;
			}
		}
	}

	public virtual void FindRenderers(bool thirdperson)
	{
		if (thirdperson)
		{
			renderers = new List<Renderer>(thirdPersonTransform.GetComponentsInChildren<Renderer>());
		}
		else
		{
			renderers = new List<Renderer>(GetComponentsInChildren<Renderer>());
		}
	}

	protected virtual void Update()
	{
		if (!stopFireLoop.Done() && audio != null)
		{
			float num = 1f - stopFireLoop.Ratio();
			audio.volume = num * weaponVolume;
			if (stopFireLoop.TrueDone())
			{
				audio.Stop();
			}
		}
		if (HasActiveAnimator() && user != null)
		{
			animator.SetBool("tuck", user.controller.IsSprinting());
		}
	}

	public virtual void Fire(Vector3 direction, bool useMuzzleDirection)
	{
		if (CanFire())
		{
			if (configuration.auto && audio != null && (!audio.isPlaying || !stopFireLoop.Done()))
			{
				StartFireLoop();
			}
			Shoot(direction, useMuzzleDirection);
		}
		holdingFire = true;
	}

	private void StartFireLoop()
	{
		if (audio == null)
		{
			return;
		}
		audio.volume = weaponVolume;
		audio.Play();
		stopFireLoop.Stop();
		fireLoopPlaying = true;
	}

	private void StopFireLoop()
	{
		if (fireLoopPlaying)
		{
			stopFireLoop.Start();
			fireLoopPlaying = false;
		}
	}

	public void StopFire()
	{
		if (configuration.auto)
		{
			StopFireLoop();
		}
		holdingFire = false;
	}

	public virtual void SetAiming(bool aiming)
	{
		this.aiming = aiming;
		if (HasActiveAnimator())
		{
			animator.SetBool("aim", aiming);
		}
	}

	public virtual void Reload(bool overrideHolstered = false)
	{
		if ((unholstered || overrideHolstered) && !reloading)
		{
			if (fireLoopPlaying)
			{
				StopFireLoop();
			}
			if (HasActiveAnimator())
			{
				animator.SetTrigger("reload");
			}
			DisableOverrideLayer();
			reloading = true;
			Invoke("ReloadDone", configuration.reloadTime);
		}
	}

	protected void ReloadDone()
	{
		EnableOverrideLayer();
		reloading = false;
		int count = configuration.ammo - ammo;
		int num = RemoveSpareAmmo(count);
		ammo += num;
		AmmoChanged();
	}

	protected virtual int RemoveSpareAmmo(int count)
	{
		return user.RemoveSpareAmmo(count, slot);
	}

	private void AmmoChanged()
	{
		user.AmmoChanged();
		if (HasActiveAnimator())
		{
			animator.SetBool("no ammo", !HasAnyAmmo());
		}
		// OptionsUi.GetOptions() is a client-only singleton and the third of the section 3.6
		// headless NREs. forceAutoReload is a prefab fact and stays authoritative everywhere; the
		// player's auto-reload PREFERENCE is only a question a client can answer, so a server
		// asking it is asking the wrong machine.
		bool autoReload = configuration.forceAutoReload
			|| (NetWeaponAuthority.CosmeticHalfRunsHere && OptionsUi.GetOptions().autoReload);
		if (!HasLoadedAmmo() && HasSpareAmmo() && !reloading && autoReload)
		{
			Reload();
		}
	}

	private void DisableOverrideLayer()
	{
		if (HasActiveAnimator() && animator.layerCount > 1)
		{
			animator.SetLayerWeight(1, 0f);
		}
	}

	private void EnableOverrideLayer()
	{
		if (HasActiveAnimator() && animator.layerCount > 1)
		{
			animator.SetLayerWeight(1, 1f);
		}
	}

	public virtual bool CanFire()
	{
		return unholstered && !reloading && HasLoadedAmmo() && (configuration.auto || !holdingFire) && !CoolingDown();
	}

	public bool CoolingDown()
	{
		return Time.time - lastFired < configuration.cooldown;
	}

	public bool AmmoFull()
	{
		return ammo >= configuration.ammo;
	}

	protected virtual void Shoot(Vector3 direction, bool useMuzzleDirection)
	{
		if (configuration.loud)
		{
			user.Highlight();
		}
		if (useMuzzleDirection)
		{
			direction = configuration.muzzle.forward;
		}
		lastFired = Time.time;
		if (HasActiveAnimator())
		{
			animator.SetTrigger("fire");
		}
		for (int i = 0; i < configuration.projectilesPerShot; i++)
		{
			SpawnProjectile(direction);
		}
		if (ammo != -1)
		{
			ammo--;
		}
		// V6-D4-local. Recoil is client-local for a human: the kick's consequence is already
		// inside the NEXT C_INPUT frame's yaw and pitch, which the server accepts as the aim, so
		// applying it server-side too would apply it twice. An AI actor has no input frame, so
		// its recoil is a server effect and its Random draw is a server draw (D4). The call also
		// chains through FpsActorController's fpParent -- the LOCAL camera rig -- which does not
		// exist on a headless build at all.
		if (user.aiControlled ? NetWeaponAuthority.GameplayHalfRunsHere : NetWeaponAuthority.CosmeticHalfRunsHere)
		{
			user.ApplyRecoil(configuration.kickback * Vector3.back + UnityEngine.Random.insideUnitSphere * configuration.randomKick);
		}
		AmmoChanged();
		if (!user.aiControlled && configuration.casing != null && NetWeaponAuthority.CosmeticHalfRunsHere)
		{
			configuration.casing.Play(false);
		}
		if (configuration.auto && ammo == 0)
		{
			StopFireLoop();
		}
		// An automatic weapon's report is a LOOP started from Fire(), not a per-shot clip, so
		// the local path must not also fire one here. See PlayFireCosmetics for why the
		// networked path passes true instead.
		if (NetWeaponAuthority.CosmeticHalfRunsHere)
		{
			PlayFireCosmetics(!configuration.auto);
		}
	}

	/// <summary>
	/// The visible and audible half of one shot: the muzzle flash and the report. Nothing else.
	/// </summary>
	/// <remarks>
	/// <para>
	/// Extracted so the networked cosmetic path and <see cref="Shoot"/> run the SAME code
	/// (phase-V10 D7). There is one copy, so a weapon that flashes offline flashes over the
	/// network, and offline single-player is unchanged.
	/// </para>
	/// <para>
	/// <b>Two things are outside this method by construction, and must stay outside it.</b>
	/// <see cref="SpawnProjectile"/> sets <c>component.source = user</c> and would do REAL
	/// DAMAGE from a client that is only meant to be drawing a flash. <c>user.ApplyRecoil</c>
	/// chains through to <c>FpsActorController</c>'s <c>fpParent</c> — the LOCAL camera rig — so
	/// running it for a remote shooter kicks your own view. A CI gate asserts that no file under
	/// <c>Net/Client/</c> references either name.
	/// </para>
	/// <para>
	/// <paramref name="playReport"/> exists because the full-auto report is a loop owned by
	/// <c>Fire()</c>, which the networked path never enters: each <c>S_WEAPON_FIRE</c> is one
	/// shot, so it plays one report per message and the loop stays a local-player optimisation
	/// (V10 D8). Calling <see cref="Shoot"/> alone on an automatic weapon would be SILENT, which
	/// reads as "network audio is flaky" rather than "wrong entry point".
	/// </para>
	/// </remarks>
	public void PlayFireCosmetics()
	{
		PlayFireCosmetics(true);
	}

	/// <inheritdoc cref="PlayFireCosmetics()"/>
	public void PlayFireCosmetics(bool playReport)
	{
		if (configuration.muzzleFlash != null)
		{
			configuration.muzzleFlash.Play(true);
		}
		if (playReport && audio != null)
		{
			audio.Play();
		}
		if (user != null && !user.aiControlled && reverbAudio != null)
		{
			PlayReverbAudio();
		}
	}

	private void PlayReverbAudio()
	{
		reverbAudio.Stop();
		reverbAudio.transform.position = configuration.muzzle.transform.position + configuration.muzzle.transform.forward * 50f;
		reverbAudio.Play();
	}

	private void OnDestroy()
	{
		if (reverbAudio != null)
		{
			UnityEngine.Object.Destroy(reverbAudio.gameObject);
		}
	}

	protected bool HasActiveAnimator()
	{
		return animator != null && animator.isActiveAndEnabled;
	}

	protected virtual Projectile SpawnProjectile(Vector3 direction)
	{
		Quaternion rotation = Quaternion.LookRotation(direction + UnityEngine.Random.insideUnitSphere * configuration.spread);
		Projectile component = ((GameObject)UnityEngine.Object.Instantiate(configuration.projectilePrefab, configuration.muzzle.position, rotation)).GetComponent<Projectile>();
		component.source = user;
		// V7 tasks 2 and 3. The single point every weapon's projectile passes through, and the
		// point AFTER the spread roll above -- which is V7-D4's server roll, resolved once, so
		// the direction announced is the direction fired. A no-op off the server.
		ProjectileNetAnnouncer.AnnounceLaunch(
			component, configuration.muzzle.position, rotation * Vector3.forward, user);
		return component;
	}

	public virtual void Hide()
	{
		foreach (Renderer renderer in renderers)
		{
			renderer.enabled = false;
		}
	}

	public virtual void Show()
	{
		foreach (Renderer renderer in renderers)
		{
			renderer.enabled = true;
		}
	}

	public virtual void CullFpsObjects()
	{
		for (int i = 0; i < base.transform.childCount; i++)
		{
			Transform child = base.transform.GetChild(i);
			if (child != thirdPersonTransform)
			{
				if (child == configuration.muzzle)
				{
					child.transform.localPosition = thirdPersonTransform.localPosition;
					thirdPersonTransform.localRotation = Quaternion.identity;
				}
				else
				{
					UnityEngine.Object.Destroy(child.gameObject);
				}
			}
		}
	}

	public bool IsEmpty()
	{
		return ammo == 0;
	}

	public virtual void Equip(Actor user)
	{
		this.user = user;
		userIsPlayer = !this.user.aiControlled;
	}

	public void Drop()
	{
		user = null;
		holdingFire = false;
		reloading = false;
		CancelInvoke();
		CancelPendingActions();
		UnityEngine.Object.Destroy(base.gameObject);
	}

	/// <summary>
	/// Cancels anything this weapon has scheduled that <c>CancelInvoke()</c> cannot reach.
	/// V7-D7.
	/// </summary>
	/// <remarks>
	/// <c>CancelInvoke()</c> only clears <c>Invoke</c> timers. V7 replaced the throwable's
	/// animation-event release with a scheduled TICK held in a plain field, which no
	/// <c>CancelInvoke</c> can see — so a throw ordered and then holstered, dropped or
	/// interrupted by death inside the release delay would still fire <c>Shoot()</c> and
	/// <c>Reload()</c>, spending a grenade the player no longer has out.
	/// </remarks>
	protected virtual void CancelPendingActions()
	{
	}

	public virtual void Unholster()
	{
		unholstered = false;
		aiming = false;
		if (HasActiveAnimator())
		{
			animator.SetBool("no ammo", !HasAnyAmmo());
			animator.SetTrigger("unholster");
		}
		Show();
		DisableOverrideLayer();
		Invoke("UnholsterDone", configuration.unholsterTime);
	}

	public void UnholsterDone()
	{
		EnableOverrideLayer();
		unholstered = true;
	}

	public virtual void Holster()
	{
		unholstered = false;
		reloading = false;
		aiming = false;
		CancelInvoke();
		CancelPendingActions();
		base.gameObject.SetActive(false);
	}

	public Effectiveness EffectivenessAgainst(Actor.TargetType targetType)
	{
		switch (targetType)
		{
		case Actor.TargetType.Unarmored:
			return configuration.effUnarmored;
		case Actor.TargetType.Armored:
			return configuration.effArmored;
		case Actor.TargetType.Air:
			return configuration.effAir;
		case Actor.TargetType.InfantryGroup:
			return configuration.effInfantryGroup;
		default:
			return configuration.effInfantry;
		}
	}

	public virtual Vector3 MuzzlePosition()
	{
		return configuration.muzzle.position;
	}

	public bool EffectiveAtRange(float range)
	{
		return configuration.effectiveRange > range;
	}

	public bool AllowsResupply()
	{
		return configuration.spareAmmo != -1;
	}

	public bool HasSpareAmmo()
	{
		if (HasInfiniteSpareAmmo())
		{
			return true;
		}
		return GetSpareAmmo() > 0;
	}

	public bool HasLoadedAmmo()
	{
		return ammo > 0 || configuration.ammo == -1;
	}

	public bool HasAnyAmmo()
	{
		return HasLoadedAmmo() || HasSpareAmmo();
	}

	public bool HasInfiniteSpareAmmo()
	{
		return configuration.spareAmmo == -2;
	}

	public virtual int GetSpareAmmo()
	{
		if (user != null)
		{
			return user.RemainingSpareAmmoFor(this);
		}
		return 0;
	}

	public void AssignFpAudioMix()
	{
		if (audio == null)
		{
			return;
		}
		audio.spatialBlend = 0.4f;
		if (!configuration.forceWorldAudioOutput)
		{
			audio.outputAudioMixerGroup = GameManager.instance.fpMixerGroup;
		}
	}

	public virtual bool IsToggleable()
	{
		return false;
	}

	public virtual bool CanBeAimed()
	{
		return !reloading && unholstered;
	}
}

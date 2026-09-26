using System;
using System.Collections.Generic;
using Ironfront.Net.Unity;
using UnityEngine;

public partial class Weapon : MonoBehaviour, Ironfront.Net.Unity.IGameplayWeapon
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
		/// <b>Authored per weapon, from that weapon's own throw clip.</b>
		/// <c>ThrowableWeapon.Fire</c> does not shoot -- it sets an Animator trigger, and an
		/// animation event calls <c>ThrowableWeapon.SpawnThrowable</c>. A headless server has no
		/// active Animator (<c>Weapon.HasActiveAnimator</c> already returns false there, and on a
		/// stripped prefab <c>GetComponent&lt;Animator&gt;()</c> returns null outright), so it
		/// schedules the release from this number instead. The two agree only when this equals
		/// the wall-clock time from trigger to event, which is the event's clip time divided by
		/// the <c>Throw</c> state's speed multiplier.
		/// </para>
		/// <para>
		/// <b>One number could never have served both throwables, and for a while one did.</b>
		/// <c>frag_throw.anim</c> raises the event at 1.2381772 s and <c>Ammobox Throw.anim</c> at
		/// 0.4142947 s -- three times apart -- and both <c>Throw</c> states run at
		/// <c>m_Speed: 1.3</c>. So the authored values are 0.952444 s (frag, spearhead) and
		/// 0.3186882 s (ammobox, medipack), and the <c>0.6f</c> below is now only the default a
		/// NEW throwable starts from before anybody authors it. It is deliberately left wrong for
		/// every existing clip: a new prefab that ships unauthored fails the gate loudly rather
		/// than inheriting a plausible-looking number (ledger D-1).
		/// </para>
		/// <para>
		/// <b>Why not run an Animator on the server.</b> A headless build strips the renderers
		/// the clip drives, the clip is authored for visuals rather than simulation, and it would
		/// make the release time an Editor-only fact no test can grade. <b>Why not trust the
		/// client's animation event.</b> It would make a client the author of the authoritative
		/// release tick, and a modified client throws instantly with nothing to check it against.
		/// An authored constant is checkable by both sides.
		/// </para>
		/// <para>
		/// <b>Drift is graded, and no longer only by eye.</b> This used to be "the one number in
		/// this phase that nothing in CI can discover", and the cost was stated as a cosmetic
		/// error with a loud symptom. Both halves were wrong: the clips are force-text and were
		/// readable all along, and the symptom is loud only to somebody watching for it. Gate rule
		/// <b>A9</b> (<c>AssetWiringDetectors.ThrowReleaseDelayMatchesTheThrowClip</c>) now reads
		/// prefab, controller and clip and fails the build when this and the clip disagree by
		/// more than 0.1 ms.
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

	/// <summary>
	/// Recolours only the first-person arm mesh carried by this weapon prefab.
	/// </summary>
	public void SetFirstPersonTeamColor(Color color)
	{
		if (user == null || user.aiControlled)
		{
			return;
		}

		Renderer[] children = GetComponentsInChildren<Renderer>(true);
		foreach (Renderer child in children)
		{
			// Every shipped first-person weapon calls this mesh "Arms". Keep the exact
			// match so weapon finishes and objects such as "Explosion Arms" are untouched.
			if (child != null && child.gameObject.name == "Arms")
			{
				child.material.color = color;
			}
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

	protected void AmmoChanged()
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
		// Once per shot, not once per projectile: a shell-loaded weapon fires twenty pellets and
		// every one of them leaves the same hand. Cleared here rather than at the top of the next
		// shot so that a bot -- which never has one supplied -- cannot inherit the last human's.
		networkShotOrigin = null;
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
		Vector3 origin = ProjectileOrigin(direction);
		GameObject instance = null;

		try
		{
			instance = (GameObject)UnityEngine.Object.Instantiate(
				configuration.projectilePrefab, origin, rotation);
			Projectile component = instance.GetComponent<Projectile>();
			if (component == null)
				throw new System.InvalidOperationException(
					$"Projectile prefab '{configuration.projectilePrefab.name}' has no Projectile component.");

			component.source = user;
			// V7 tasks 2 and 3. The single point every weapon's projectile passes through, and the
			// point AFTER the spread roll above -- which is V7-D4's server roll, resolved once, so
			// the direction announced is the direction fired. A no-op off the server.
			ProjectileNetAnnouncer.AnnounceLaunch(
				component, origin, rotation * Vector3.forward, user);
			return component;
		}
		catch
		{
			// A partially-created projectile must not survive an announcement/configuration
			// failure: the caller can then roll the authoritative inventory transaction back
			// without leaving an invisible server-side explosive behind.
			if (instance != null) UnityEngine.Object.Destroy(instance);
			throw;
		}
	}

	/// <summary>
	/// Where this weapon's projectile is born, in world space, for a shot fired along
	/// <paramref name="direction"/>.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>The authoritative pose wins when the server supplied one.</b>
	/// <see cref="SetNetworkShotOrigin"/> hands this weapon the origin the server's own combat
	/// authority computed for the shot it is about to launch (<c>CombatTickResult.Origin</c>,
	/// built from the session's capsule centre and the same eye height the hitboxes use). That
	/// value is derived from the session's deterministic state, so it is the one spawn point
	/// that is the same on every body prefab; anything read off a rig is not.
	/// </para>
	/// <para>
	/// <b>Not <c>configuration.muzzle.position</c> by default, and that default is a trap for
	/// anything but a first-person weapon.</b> <c>muzzle</c> hangs off the weapon root, which
	/// <c>Actor</c> parents to <c>controller.WeaponParent()</c> -- and for a human that parent is
	/// displaced and pitched every frame by <c>PlayerFpParent</c>, the LOCAL view-model rig. On a
	/// headless server that rig is inert, so the muzzle describes a pose nobody is standing in
	/// (measured 2026-09-25: a player rocket left from a skeleton bone). A weapon whose spawn
	/// point matters on the wire overrides this; see <c>ThrowableWeapon.ProjectileOrigin</c>.
	/// </para>
	/// <para>
	/// Kept as one method rather than a parameter so that the instantiate above and the
	/// <c>AnnounceLaunch</c> below cannot drift apart -- two copies of an origin is exactly how
	/// a client ends up drawing a projectile somewhere the server did not put it.
	/// </para>
	/// </remarks>
	protected virtual Vector3 ProjectileOrigin(Vector3 direction)
		=> networkShotOrigin ?? configuration.muzzle.position;

	/// <summary>
	/// The spawn point the server's authority chose for the shot about to be fired, or null when
	/// this shot is not one the authority ordered.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>A nullable Vector3 rather than a flag beside it</b>, so "no origin was supplied" and
	/// "an origin was supplied and happens to be the zero vector" cannot be confused, and so
	/// there is no second field to keep in step with the first.
	/// </para>
	/// <para>
	/// <b>Consumed, not merely read: <see cref="Shoot"/> clears it once the round has left.</b>
	/// A bot fires through the same <c>Shoot</c> with no authority behind it, so a value that
	/// outlived its shot would place a bot's next grenade wherever the last human's hand was.
	/// </para>
	/// </remarks>
	private Vector3? networkShotOrigin;

	/// <summary>Reads the authority-supplied one-shot origin without consuming it.</summary>
	protected bool TryGetNetworkShotOrigin(out Vector3 origin)
	{
		if (networkShotOrigin.HasValue)
		{
			origin = networkShotOrigin.Value;
			return true;
		}

		origin = default;
		return false;
	}

	/// <summary>
	/// Supplies the authoritative spawn point for the shot this weapon is about to fire.
	/// Server only -- see <see cref="networkShotOrigin"/>.
	/// </summary>
        public void SetNetworkShotOrigin(Vector3 origin) => networkShotOrigin = origin;

        /// <summary>Clears a one-shot authority origin after a specialised launch path.</summary>
        protected void ClearNetworkShotOrigin() => networkShotOrigin = null;

	/// <summary>
	/// Copies the server's authoritative carried-weapon state onto this weapon's own counters.
	/// </summary>
	/// <remarks>
	/// <para>
	/// <b>Three fields, because those are exactly the three <see cref="CanFire"/> reads that a
	/// server can leave stale.</b> <c>ammo</c> is never refilled on a dedicated server -- nothing
	/// reaches <see cref="ReloadDone"/>, since <c>Actor.UpdateWeapon</c> does not run for a
	/// suspended body and the auto-reload branch is gated to the cosmetic half -- so a clip-of-one
	/// launcher fired once and was then refused by its own gun for the rest of the life.
	/// <c>unholstered</c> completes on the engine's own <c>unholsterTime</c> timer while the
	/// session marks the weapon up immediately. <c>lastFired</c> is stamped at the RELEASE for a
	/// throwable and at the trigger for the authority, which is <c>releaseDelay</c> apart.
	/// <c>reloading</c> is deliberately not mirrored: on a server nothing sets it except a
	/// throwable's own zero-length refill, and <c>holdingFire</c> is already the launch path's to
	/// clear.
	/// </para>
	/// <para>
	/// <b>A duration, not a timestamp.</b> The authority counts seconds derived from the tick the
	/// frame carried; this side counts <c>Time.time</c>. Copying the number across would put the
	/// cooldown's end wherever the two clocks happened to differ by, which is exactly the kind of
	/// divergence this method exists to remove.
	/// </para>
	/// <para>
	/// <b>Called once per accepted frame, so it cannot be missed on a transition.</b> There is no
	/// reload, holster or unholster notification to hook -- inventing one would be a second place
	/// for the two copies to part.
	/// </para>
	/// </remarks>
	public void MirrorAuthorityState(int ammoInClip, bool unholstered, float elapsedSinceLastShot)
	{
		// The car horn's -1 is "never spends", not a count, and it is not on this path at all:
		// a mounted weapon has its own authority. Guarded rather than assumed so a future
		// infinite-ammo carried weapon keeps its sentinel instead of acquiring a magazine.
		if (ammo != -1) ammo = ammoInClip;

		this.unholstered = unholstered;

		// Never fired is the authority's -infinity, which arrives here as +infinity elapsed and
		// makes CoolingDown false. Subtracting an infinity is well defined and lands on -infinity,
		// which is the same value WeaponRuntimeState.Loaded starts from.
		lastFired = Time.time - elapsedSinceLastShot;
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

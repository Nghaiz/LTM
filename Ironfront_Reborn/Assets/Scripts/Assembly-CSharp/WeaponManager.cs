using System;
using System.Collections.Generic;
using UnityEngine;

public class WeaponManager : MonoBehaviour
{
	public enum WeaponSlot
	{
		Primary = 0,
		Secondary = 1,
		Gear = 2,
		LargeGear = 3
	}

	[Serializable]
	public class WeaponEntry
	{
		// Defaults to 0, not 1. A new entry created in the Inspector inherits this value, and 1
		// is a real weapon's id — defaulting to it made every new weapon a silent duplicate of
		// RK-44 that the validator dropped from the lookup while still stamping spawned weapons
		// with 1. 0 is the reserved unassigned value, so a new entry announces itself as
		// unconfigured instead of impersonating something. protocol-spec.md section 4.8.
		[Range(0, 255)]
		public int NetworkId;

		public string name = "Weapon";

		public Sprite image;

		public GameObject prefab;

		public WeaponSlot slot;

		public bool hidden;
	}

	public class LoadoutSet
	{
		public WeaponEntry primary;

		public WeaponEntry secondary;

		public WeaponEntry gear1;

		public WeaponEntry gear2;

		public WeaponEntry gear3;

		public LoadoutSet()
		{
			primary = null;
			secondary = null;
			gear1 = null;
			gear2 = null;
			gear3 = null;
		}
	}

	private const int M22S7_HASH_INDEXER = 18;

	public static WeaponManager instance;

	public List<WeaponEntry> weapons;

	private readonly Dictionary<byte, WeaponEntry> _weaponsByNetworkId = new Dictionary<byte, WeaponEntry>();

	private int sequenceIndex;

	private KeyCode[] secretSequence = new KeyCode[22]
	{
		KeyCode.R,
		KeyCode.E,
		KeyCode.D,
		KeyCode.L,
		KeyCode.I,
		KeyCode.N,
		KeyCode.E,
		KeyCode.S,
		KeyCode.T,
		KeyCode.O,
		KeyCode.P,
		KeyCode.D,
		KeyCode.E,
		KeyCode.C,
		KeyCode.O,
		KeyCode.M,
		KeyCode.P,
		KeyCode.I,
		KeyCode.L,
		KeyCode.I,
		KeyCode.N,
		KeyCode.G
	};

	private KeyCode[] secretSequence2 = new KeyCode[8]
	{
		KeyCode.C,
		KeyCode.Z,
		KeyCode.M,
		KeyCode.E,
		KeyCode.J,
		KeyCode.M,
		KeyCode.S,
		KeyCode.RightBracket
	};

	private void Awake()
	{
		instance = this;
		BuildNetworkIdLookup();
	}

	private void BuildNetworkIdLookup()
	{
		_weaponsByNetworkId.Clear();
		if (weapons == null)
		{
			return;
		}
		foreach (WeaponEntry weapon in weapons)
		{
			if (weapon == null)
			{
				continue;
			}

			if (weapon.NetworkId <= 0 || weapon.NetworkId > byte.MaxValue)
			{
				Debug.LogError("Weapon '" + weapon.name + "' has no network id (" + weapon.NetworkId + "). Valid ids are 1..255; 0 is reserved for no/unknown weapon. Give it the next free id and add it to protocol-spec.md section 4.8 and WeaponIds.cs — an unassigned weapon is transmitted as 0 and remote clients will not draw it.");
				continue;
			}

			byte networkId = (byte)weapon.NetworkId;
			if (_weaponsByNetworkId.ContainsKey(networkId))
			{
				Debug.LogError("Duplicate weapon network id " + networkId + " on '" + weapon.name + "' and '" + _weaponsByNetworkId[networkId].name + "'. Both are transmitted as 0 until this is fixed — see protocol-spec.md section 4.8, ids are unique and permanent.");
				continue;
			}

			_weaponsByNetworkId.Add(networkId, weapon);
		}
	}

	public static bool TryGetEntry(byte networkId, out WeaponEntry entry)
	{
		entry = null;
		return instance != null && instance._weaponsByNetworkId.TryGetValue(networkId, out entry);
	}

	// Resolves through the validated lookup rather than reading the field directly, which is the
	// whole point: an entry can carry a duplicate id, and returning it would put a weapon on the
	// wire wearing another weapon's identity. Remote clients would then draw the wrong gun and
	// the server would apply the wrong ballistics, with nothing failing anywhere. Falling back to
	// 0 makes a misconfigured weapon invisible instead, which is wrong in a way somebody notices.
	public static byte NetworkIdOf(WeaponEntry entry)
	{
		if (entry == null || entry.NetworkId <= 0 || entry.NetworkId > byte.MaxValue)
		{
			return 0;
		}
		byte networkId = (byte)entry.NetworkId;
		if (instance == null)
		{
			return networkId;
		}
		if (!instance._weaponsByNetworkId.TryGetValue(networkId, out WeaponEntry owner) || owner != entry)
		{
			return 0;
		}
		return networkId;
	}

	public static List<WeaponEntry> GetWeaponEntriesOfSlot(WeaponSlot slot)
	{
		List<WeaponEntry> list = new List<WeaponEntry>();
		foreach (WeaponEntry weapon in instance.weapons)
		{
			if (weapon.slot == slot)
			{
				list.Add(weapon);
			}
		}
		return list;
	}

	public static WeaponEntry EntryNamed(string name)
	{
		return instance.weapons.Find((WeaponEntry obj) => obj.name == name);
	}

	private void Update()
	{
		// The original's secret: typed on the menu, never inside a map, and never into a text field
		// (a username that happens to spell it is not a player looking for the wrench).
		if (GameManager.instance.ingame || Ironfront.Net.Unity.LocalTextEntry.OwnsKeyboard || TypingIntoAField())
		{
			return;
		}

		if (Input.GetKeyDown(secretSequence[sequenceIndex] + (int)secretSequence2[sequenceIndex] - (int)secretSequence[M22S7_HASH_INDEXER]))
		{
			if (sequenceIndex < secretSequence2.Length - 1)
			{
				sequenceIndex++;
				return;
			}
			sequenceIndex = 0;
			RevealGoldenWrench();
			GetComponent<AudioSource>().Play();
		}
		else if (Input.anyKeyDown)
		{
			sequenceIndex = 0;
		}
	}

	/// <summary>
	/// The original set every hidden entry's flag to false for the rest of the run, which also
	/// offered the wrench online, where the server treats it as inert. The entries now stay as
	/// authored and <see cref="Ironfront.Net.Unity.GoldenWrench"/> decides what the loadout screen
	/// offers: the golden wrench, in practice, from now on.
	/// </summary>
	private void RevealGoldenWrench()
	{
		WeaponEntry secret = weapons.Find((WeaponEntry entry) => entry.hidden);
		Ironfront.Net.Unity.GoldenWrench.Unlock(secret != null ? secret.image : null);
	}

	private static bool TypingIntoAField()
	{
		UnityEngine.EventSystems.EventSystem events = UnityEngine.EventSystems.EventSystem.current;
		GameObject selected = events != null ? events.currentSelectedGameObject : null;
		UnityEngine.UI.InputField field = selected != null ? selected.GetComponent<UnityEngine.UI.InputField>() : null;
		return field != null && field.isFocused;
	}
}

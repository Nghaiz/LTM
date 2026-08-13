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
		[Range(1, 255)]
		public int NetworkId = 1;

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
		foreach (WeaponEntry weapon in weapons)
		{
			if (weapon.NetworkId <= 0 || weapon.NetworkId > byte.MaxValue)
			{
				Debug.LogError("Weapon '" + weapon.name + "' has invalid network id " + weapon.NetworkId + ". Valid ids are 1..255; 0 is reserved for no weapon.");
				continue;
			}

			byte networkId = (byte)weapon.NetworkId;
			if (_weaponsByNetworkId.ContainsKey(networkId))
			{
				Debug.LogError("Duplicate weapon network id " + networkId + " on '" + weapon.name + "' and '" + _weaponsByNetworkId[networkId].name + "'.");
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

	public static byte NetworkIdOf(WeaponEntry entry)
	{
		if (entry == null || entry.NetworkId <= 0 || entry.NetworkId > byte.MaxValue)
		{
			return 0;
		}
		return (byte)entry.NetworkId;
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
		if (GameManager.instance.ingame)
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
			ShowAllWeapons();
			GetComponent<AudioSource>().Play();
		}
		else if (Input.anyKeyDown)
		{
			sequenceIndex = 0;
		}
	}


	private void ShowAllWeapons()
	{
		foreach (WeaponEntry weapon in weapons)
		{
			weapon.hidden = false;
		}
	}
}

namespace Ironfront.Net.Unity
{
    /// <summary>
    /// One name plate over a person's head, resolved for drawing. Playtest 2026-09-28, feature
    /// 1; people only, and the holo-frame design with its icons, since 2026-09-29.
    /// </summary>
    /// <remarks>
    /// Plain values, as <see cref="KillfeedLine"/> is and for its reason. The screen position is
    /// in pixels from the bottom-left, as <c>Camera.WorldToScreenPoint</c> gives it; the HUD maps
    /// it onto its own canvas.
    /// </remarks>
    public readonly struct Nameplate
    {
        public Nameplate(
            ushort actorId, float screenX, float screenY, float scale, float opacity,
            string name, int team, float health01, bool isTeammate,
            float distance, byte weaponId, bool isSeated, bool isInWater, bool isLeader)
        {
            ActorId = actorId;
            ScreenX = screenX;
            ScreenY = screenY;
            Scale = scale;
            Opacity = opacity;
            Name = name ?? string.Empty;
            Team = team;
            Health01 = health01;
            IsTeammate = isTeammate;
            Distance = distance;
            WeaponId = weaponId;
            IsSeated = isSeated;
            IsInWater = isInWater;
            IsLeader = isLeader;
        }

        /// <summary>Whose plate this is, so a plate follows its actor from frame to frame.</summary>
        public ushort ActorId { get; }

        public float ScreenX { get; }

        public float ScreenY { get; }

        /// <summary>Drawn size, 0.6 far away to 1 close up.</summary>
        public float Scale { get; }

        /// <summary>0 to 1: faded with distance, dimmed for a teammate behind cover.</summary>
        public float Opacity { get; }

        /// <summary>The player's name, as they wrote it.</summary>
        public string Name { get; }

        public int Team { get; }

        /// <summary>Health as a share of full, 0 to 1.</summary>
        public float Health01 { get; }

        /// <summary>On the viewer's side: a shield on the plate, where an enemy's carries the hostile diamond.</summary>
        public bool IsTeammate { get; }

        /// <summary>Metres from the viewer's eye to the plate.</summary>
        public float Distance { get; }

        /// <summary>The weapon the player holds, as the snapshot says; 0 for none.</summary>
        public byte WeaponId { get; }

        /// <summary>In a vehicle seat: the plate shows a wheel instead of a weapon.</summary>
        public bool IsSeated { get; }

        public bool IsInWater { get; }

        /// <summary>Top of their side's board, the player the Tab board stars.</summary>
        public bool IsLeader { get; }
    }
}

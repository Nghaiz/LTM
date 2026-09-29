namespace Ironfront.Net.Unity
{
    /// <summary>
    /// One name and health bar over somebody's head, resolved for drawing. Playtest 2026-09-28,
    /// feature 1.
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
            string name, int team, float health01, bool isBot, bool isTeammate)
        {
            ActorId = actorId;
            ScreenX = screenX;
            ScreenY = screenY;
            Scale = scale;
            Opacity = opacity;
            Name = name ?? string.Empty;
            Team = team;
            Health01 = health01;
            IsBot = isBot;
            IsTeammate = isTeammate;
        }

        /// <summary>Whose plate this is, so a plate follows its actor from frame to frame.</summary>
        public ushort ActorId { get; }

        public float ScreenX { get; }

        public float ScreenY { get; }

        /// <summary>Drawn size, 0.6 far away to 1 close up.</summary>
        public float Scale { get; }

        /// <summary>0 to 1: faded with distance, dimmed for a teammate behind cover.</summary>
        public float Opacity { get; }

        public string Name { get; }

        public int Team { get; }

        /// <summary>Health as a share of full, 0 to 1.</summary>
        public float Health01 { get; }

        /// <summary>A bot, drawn so it cannot be taken for a person.</summary>
        public bool IsBot { get; }

        public bool IsTeammate { get; }
    }
}

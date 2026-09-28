using System;
using System.Globalization;
using System.Text;

namespace Ironfront.Net.Protocol
{
    /// <summary>
    /// Turns a player-supplied display name into something safe to put in a UI label.
    /// verdict-closure R2 task R2.2, ledger X-36.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>A display name is attacker-controlled text that arrives over a socket and lands in a
    /// label.</b> The server's copy comes out of the join ticket, which is HMAC-signed — but the
    /// signature proves the master issued it, not that its contents are harmless; the master
    /// takes the string from a registration form. The client's copy arrives in
    /// <c>S_PLAYER_LIST</c> from a game server the client cannot verify at all. So both ends
    /// sanitize, at their own ingress, with this one function — a single rule the two sides
    /// cannot drift apart on.
    /// </para>
    /// <para>
    /// <b>What is removed, and why each one.</b>
    /// </para>
    /// <list type="bullet">
    /// <item><description>
    /// <b>Angle brackets.</b> Unity's <c>Text</c> and <c>TMP_Text</c> parse rich-text markup by
    /// default. A name of <c>&lt;color=#00000000&gt;</c> renders an invisible killfeed line, and
    /// <c>&lt;size=400%&gt;</c> renders one that covers the screen. Dropping the two characters
    /// is cheaper and more certain than trusting every label in the build to have rich text off.
    /// </description></item>
    /// <item><description>
    /// <b>Control and format characters.</b> A newline breaks a single-line feed into two; a NUL
    /// truncates the name in any consumer that reaches C; and the bidi overrides
    /// (U+202A…U+202E, U+2066…U+2069) let a name re-order the text <em>around</em> it, so
    /// "A killed B" can be made to read as "B killed A". <see cref="UnicodeCategory.Format"/>
    /// covers the overrides and the zero-width joiners together.
    /// </description></item>
    /// <item><description>
    /// <b>Unpaired surrogates and unassigned code points.</b> Not an attack so much as a
    /// guaranteed replacement glyph, and a lone surrogate is not valid UTF-8 — it would fail
    /// re-encoding on the way back out to <c>S_PLAYER_LIST</c>.
    /// </description></item>
    /// <item><description>
    /// <b>Whitespace runs.</b> Folded to one space and trimmed, so a name of sixteen spaces is
    /// <see cref="string.Empty"/> rather than a blank feed line that reads as a rendering bug.
    /// </description></item>
    /// </list>
    /// <para>
    /// <b>Empty is a real answer, and callers must handle it.</b> A name that sanitizes to
    /// nothing is not substituted for here: this function has no idea what a good fallback would
    /// be, and manufacturing one would make a hostile name indistinguishable from an honest one.
    /// The caller knows the actor id and picks — see <c>ServerTickLoop.DisplayNameFor</c> and
    /// <c>PlayerNameTable.Apply</c>, which take opposite but deliberate branches.
    /// </para>
    /// <para>
    /// <b>Dropping, not replacing.</b> Substituting <c>?</c> per removed character would let a
    /// name made entirely of overrides survive as a row of question marks — visible junk that
    /// still occupies the feed. There is nothing worth keeping in a character we refused.
    /// </para>
    /// </remarks>
    public static class PlayerNameSanitizer
    {
        /// <summary>
        /// The longest name this returns, in characters.
        /// </summary>
        /// <remarks>
        /// <b>Characters, and the wire limit is bytes</b> —
        /// <see cref="PlayerListMessage.MaxNameBytes"/> and
        /// <see cref="JoinTicket.DisplayNameSize"/> are both 16 <em>bytes</em>. Sixteen
        /// characters of Vietnamese or Cyrillic is more than sixteen bytes, so this cap does
        /// NOT relieve a writer of its own byte check; it exists so that a name cannot be
        /// unbounded in a label before it ever reaches a writer. The two limits agree in the
        /// ASCII case and the byte limit is the binding one otherwise, which is the safe
        /// direction.
        /// </remarks>
        public const int MaxCharacters = 16;

        /// <summary>
        /// Returns <paramref name="raw"/> stripped of everything a label must not be asked to
        /// render, or <see cref="string.Empty"/> when nothing survives.
        /// </summary>
        public static string Sanitize(string? raw) => Sanitize(raw, MaxCharacters);

        /// <summary>
        /// The same rule, clipped at a caller-supplied character bound. Phase P6 task 3.3.
        /// </summary>
        /// <remarks>
        /// <para>
        /// <b>Chat text is the second caller, and it is the same hazard at a different length.</b>
        /// A chat line arrives over a socket and lands in a label exactly as a display name does,
        /// so it needs the same rich-text, control-character and bidi-override treatment — only
        /// the cap differs (<see cref="Ironfront.Net.Protocol.ChatTextMessage.MaxTextCharacters"/>).
        /// A second sanitizer for chat would be a second place to keep a security rule in step,
        /// and the two would drift on exactly the character class nobody re-checked.
        /// </para>
        /// <para>
        /// The class keeps its name. It is the display-name rule that generalised, not a new
        /// rule that happens to resemble it, and renaming it would touch every call site to
        /// record that fact in one identifier.
        /// </para>
        /// </remarks>
        public static string Sanitize(string? raw, int maxCharacters)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            if (maxCharacters <= 0) return string.Empty;

            var builder = new StringBuilder(Math.Min(raw!.Length, maxCharacters));
            bool pendingSpace = false;

            for (int i = 0; i < raw.Length; i++)
            {
                char c = raw[i];

                // Whitespace of every kind — tab, NBSP, ideographic space — folds to one plain
                // space, and only once something else has been kept. That drops a leading run
                // and collapses an interior one in the same branch, so no Trim pass is needed.
                if (char.IsWhiteSpace(c))
                {
                    if (builder.Length > 0) pendingSpace = true;
                    continue;
                }

                if (c == '<' || c == '>') continue;

                // A surrogate pair is kept whole or dropped whole. Taking the high half alone
                // produces a string that cannot be UTF-8 encoded, which fails at the writer
                // rather than here — one boundary further from the input that caused it.
                if (char.IsHighSurrogate(c))
                {
                    if (i + 1 >= raw.Length || !char.IsLowSurrogate(raw[i + 1])) continue;

                    if (!IsRenderable(CharUnicodeInfo.GetUnicodeCategory(raw, i)))
                    {
                        i++;
                        continue;
                    }

                    if (builder.Length + 2 > maxCharacters) break;

                    if (pendingSpace) { builder.Append(' '); pendingSpace = false; }
                    builder.Append(c).Append(raw[i + 1]);
                    i++;
                    continue;
                }

                // A low surrogate reached on its own is the unpaired half of a pair whose high
                // half we already refused, or of one that never had a high half at all.
                if (char.IsLowSurrogate(c)) continue;

                if (!IsRenderable(CharUnicodeInfo.GetUnicodeCategory(c))) continue;

                // The pending space is charged against the cap before the character it precedes,
                // so a name is never truncated to end in a space.
                int cost = pendingSpace ? 2 : 1;
                if (builder.Length + cost > maxCharacters) break;

                if (pendingSpace) { builder.Append(' '); pendingSpace = false; }
                builder.Append(c);
            }

            return builder.ToString();
        }

        /// <summary>
        /// Whether a code point in <paramref name="category"/> is something a label can be asked
        /// to draw.
        /// </summary>
        /// <remarks>
        /// An allow-by-exclusion list rather than an allow-list of scripts, deliberately: this
        /// game ships in more languages than a script allow-list would survive, and refusing a
        /// legitimate name is a bug a player cannot work around. The five refused categories are
        /// the ones with no glyph to draw.
        /// </remarks>
        private static bool IsRenderable(UnicodeCategory category)
            => category != UnicodeCategory.Control
            && category != UnicodeCategory.Format
            && category != UnicodeCategory.Surrogate
            && category != UnicodeCategory.LineSeparator
            && category != UnicodeCategory.ParagraphSeparator
            && category != UnicodeCategory.OtherNotAssigned;
    }
}

/// <summary>
/// A random stream reserved for cosmetics — audio pitch, decal jitter, particle variation.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why this is not <c>UnityEngine.Random</c>.</b> That stream is global and shared with
/// gameplay, and every draw advances it for everyone. A dedicated server does not build particle
/// systems or audio sources, so the cosmetic draws that a client makes are draws the server never
/// makes — the two walk the same shared stream at different rates, and anything seeded for
/// reproducibility drifts apart for a reason that reads as unrelated. Guarding the cosmetic behind
/// a null check (which is correct on its own terms) makes that drift WORSE, not better, because it
/// is precisely what makes the two sides' draw counts differ.
/// </para>
/// <para>
/// Phase V1 flagged the grenade's pitch roll as exactly this defect and handed it to V0; V0 closed
/// without absorbing it. This is the seam that closes it: cosmetics draw here, gameplay draws from
/// <c>UnityEngine.Random</c>, and neither can move the other.
/// </para>
/// <para>
/// Deliberately not seeded and deliberately not synchronised. Nothing here is allowed to affect a
/// simulation outcome, so there is nothing to reproduce. If a caller ever needs a reproducible
/// value, that is a sign the value is not cosmetic and belongs on the other stream.
/// </para>
/// </remarks>
public static class CosmeticRandom
{
	private static readonly System.Random Stream = new System.Random();

	/// <summary>A cosmetic value in [<paramref name="min"/>, <paramref name="max"/>).</summary>
	public static float Range(float min, float max)
	{
		lock (Stream)
		{
			return min + (float)Stream.NextDouble() * (max - min);
		}
	}
}

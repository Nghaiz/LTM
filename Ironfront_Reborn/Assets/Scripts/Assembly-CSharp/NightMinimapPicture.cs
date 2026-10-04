using System.Collections.Generic;
using UnityEngine;
using UnityEngine.UI;

/// <summary>
/// The map's picture in Night Mode (phase P32): the baked night picture with a warm glow drawn
/// wherever one of this match's pumpkins or lamps shines, the way the original game's night map
/// showed its pumpkins.
/// </summary>
/// <remarks>
/// <para>
/// The pumpkins move every match (<see cref="NightModeDirector"/> seeds them by the room), so the
/// glows cannot be baked: they are painted once onto a render texture when the night's lights are
/// set out, and again only if their number changes. Both the M map and the corner radar show the
/// same texture.
/// </para>
/// <para>
/// Drawn with the UI's own default material, which every player build carries; a shader found by
/// name may have been stripped from the build.
/// </para>
/// </remarks>
public sealed class NightMinimapPicture
{
	private const int GlowTextureSize = 64;

	/// <summary>The glow's radius in metres: about where a candle's light gives out (its range is 8 m).</summary>
	public const float GlowRadiusMetres = 9f;

	/// <summary>The bright heart of a glow, in metres.</summary>
	public const float CoreRadiusMetres = 2.5f;

	/// <summary>A glow is never smaller than this many texels, or the whole map hides it.</summary>
	private const float MinGlowTexels = 6f;

	private const float MinCoreTexels = 2f;

	private static readonly Color Glow = new Color(1f, 0.62f, 0.2f, 0.55f);

	private static readonly Color Core = new Color(1f, 0.9f, 0.6f, 0.95f);

	private static Texture2D glowTexture;

	private readonly Texture2D baked;

	private RenderTexture picture;

	private Material material;

	private int paintedGlows = -1;

	public NightMinimapPicture(Texture2D baked)
	{
		this.baked = baked;
	}

	/// <summary>The night picture with <paramref name="glows"/> on it, framed by <paramref name="frame"/>.</summary>
	public Texture Picture(IReadOnlyList<Vector3> glows, Camera frame)
	{
		int count = glows != null ? glows.Count : 0;
		if (picture == null || count != paintedGlows)
		{
			Paint(glows, frame);
			paintedGlows = count;
		}
		return picture;
	}

	public void Release()
	{
		if (picture != null)
		{
			picture.Release();
			Object.Destroy(picture);
			picture = null;
		}
		if (material != null)
		{
			Object.Destroy(material);
			material = null;
		}
		paintedGlows = -1;
	}

	private void Paint(IReadOnlyList<Vector3> glows, Camera frame)
	{
		int size = baked.width;
		if (picture == null)
		{
			picture = new RenderTexture(size, size, 0, RenderTextureFormat.ARGB32);
			picture.name = "Night Minimap";
			picture.useMipMap = true;
			picture.autoGenerateMips = true;
			picture.filterMode = FilterMode.Trilinear;
			picture.wrapMode = TextureWrapMode.Clamp;
			picture.Create();
		}
		Graphics.Blit(baked, picture);
		if (glows == null || glows.Count == 0 || frame == null)
		{
			return;
		}
		if (material == null)
		{
			material = new Material(Canvas.GetDefaultCanvasMaterial());
			material.mainTexture = GlowTexture();
		}
		// Metres to texels: the camera is orthographic and frames the picture exactly.
		float texelsPerMetre = size / (frame.orthographicSize * 2f);
		float glowRadius = Mathf.Max(MinGlowTexels, GlowRadiusMetres * texelsPerMetre);
		float coreRadius = Mathf.Max(MinCoreTexels, CoreRadiusMetres * texelsPerMetre);

		RenderTexture previous = RenderTexture.active;
		RenderTexture.active = picture;
		GL.PushMatrix();
		GL.LoadPixelMatrix(0f, size, 0f, size);
		material.SetPass(0);
		GL.Begin(GL.QUADS);
		DrawAll(glows, frame, size, glowRadius, Glow);
		DrawAll(glows, frame, size, coreRadius, Core);
		GL.End();
		GL.PopMatrix();
		RenderTexture.active = previous;
	}

	private static void DrawAll(IReadOnlyList<Vector3> glows, Camera frame, int size, float radius, Color colour)
	{
		GL.Color(colour);
		for (int i = 0; i < glows.Count; i++)
		{
			Vector3 viewport = frame.WorldToViewportPoint(glows[i]);
			float x = viewport.x * size;
			float y = viewport.y * size;
			if (x < -radius || y < -radius || x > size + radius || y > size + radius)
			{
				continue;
			}
			GL.TexCoord2(0f, 0f);
			GL.Vertex3(x - radius, y - radius, 0f);
			GL.TexCoord2(0f, 1f);
			GL.Vertex3(x - radius, y + radius, 0f);
			GL.TexCoord2(1f, 1f);
			GL.Vertex3(x + radius, y + radius, 0f);
			GL.TexCoord2(1f, 0f);
			GL.Vertex3(x + radius, y - radius, 0f);
		}
	}

	/// <summary>A soft white disc, opaque at the centre and gone at the rim.</summary>
	private static Texture2D GlowTexture()
	{
		if (glowTexture != null)
		{
			return glowTexture;
		}
		glowTexture = new Texture2D(GlowTextureSize, GlowTextureSize, TextureFormat.RGBA32, false);
		glowTexture.name = "Night Minimap Glow";
		glowTexture.wrapMode = TextureWrapMode.Clamp;
		var pixels = new Color32[GlowTextureSize * GlowTextureSize];
		float half = (GlowTextureSize - 1) * 0.5f;
		for (int y = 0; y < GlowTextureSize; y++)
		{
			for (int x = 0; x < GlowTextureSize; x++)
			{
				float d = Mathf.Sqrt(((x - half) * (x - half)) + ((y - half) * (y - half))) / half;
				float a = Mathf.Clamp01(1f - d);
				pixels[(y * GlowTextureSize) + x] = new Color32(255, 255, 255, (byte)(a * a * 255f));
			}
		}
		glowTexture.SetPixels32(pixels);
		glowTexture.Apply();
		return glowTexture;
	}
}

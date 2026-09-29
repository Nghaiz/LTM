Shader "Masked/Mask" {
	// Depth only: writes the depth buffer and no colour, so whatever lies behind it is hidden
	// while it is there. The splash's title plane is the one user; it slides away as the
	// helicopter passes the camera, which is what wipes the title in.
	//
	// Rebuilt from the original's compiled ShaderLab (queue Geometry+10, ColorMask 0). The
	// decompiler left a stub here that drew an opaque textured surface instead: a grey block
	// over the title for the first nine seconds (2026-09-29).
	SubShader {
		Tags { "Queue" = "Geometry+10" }
		Pass {
			ColorMask 0
			ZWrite On
		}
	}
}

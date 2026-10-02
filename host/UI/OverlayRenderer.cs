using System;
using OWCraft.Link;
using UnityEngine;

namespace OWCraft.UI
{
	/// <summary>
	/// Minecraft's 2D layer (hand, hotbar, crosshair, inventory and other screens), drawn full screen
	/// over Outer Wilds while Minecraft mode is on.
	/// </summary>
	public sealed class OverlayRenderer : IDisposable
	{
		Texture2D _tex;
		bool _bottomUp = true;
		Texture2D _cursor;

		public bool Visible;
		public bool ShowCursor;
		public Vector2 Cursor; // overlay pixels, top-left origin
		public int FramesTaken; // diagnostics

		public void Update(SharedLink link)
		{
			if (!link.TryTakeOverlay(out IntPtr pixels, out int w, out int h, out bool bottomUp)) return;
			if (_tex == null || _tex.width != w || _tex.height != h)
			{
				if (_tex != null) UnityEngine.Object.Destroy(_tex);
				_tex = new Texture2D(w, h, TextureFormat.RGBA32, false, false)
				{
					name = "OWCraft overlay",
					filterMode = FilterMode.Point,
					wrapMode = TextureWrapMode.Clamp,
				};
			}
			_tex.LoadRawTextureData(pixels, w * h * 4);
			_tex.Apply(false);
			_bottomUp = bottomUp;
			FramesTaken++;
		}

		/// <summary>Call from OnGUI.</summary>
		public void Draw()
		{
			if (!Visible || _tex == null || Event.current.type != EventType.Repaint) return;
			var screen = new Rect(0, 0, Screen.width, Screen.height);
			// GUI draws texture row 0 at the bottom, which is how a bottom-up frame is stored.
			if (_bottomUp) GUI.DrawTexture(screen, _tex, ScaleMode.StretchToFill, true);
			else GUI.DrawTextureWithTexCoords(screen, _tex, new Rect(0, 1, 1, -1), true);

			if (ShowCursor)
			{
				if (_cursor == null) _cursor = MakeCursor();
				float sx = Screen.width / (float)_tex.width, sy = Screen.height / (float)_tex.height;
				GUI.DrawTexture(new Rect(Cursor.x * sx, Cursor.y * sy, _cursor.width, _cursor.height), _cursor);
			}
		}

		static Texture2D MakeCursor()
		{
			// A plain arrow, drawn here (the game hides the OS cursor).
			const int S = 16;
			var t = new Texture2D(S, S, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point };
			var px = new Color32[S * S];
			for (int y = 0; y < S; y++)
			{
				for (int x = 0; x < S; x++)
				{
					int row = S - 1 - y; // texture rows are bottom-up
					bool inside = x <= row && x + row / 2 < S && row < 14;
					bool edge = inside && (x == 0 || x == row || row == 13);
					px[y * S + x] = inside ? (edge ? new Color32(0, 0, 0, 255) : new Color32(255, 255, 255, 255)) : new Color32(0, 0, 0, 0);
				}
			}
			t.SetPixels32(px);
			t.Apply();
			return t;
		}

		public void Dispose()
		{
			if (_tex != null) UnityEngine.Object.Destroy(_tex);
			if (_cursor != null) UnityEngine.Object.Destroy(_cursor);
		}
	}
}

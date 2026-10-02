using System;
using System.Collections.Generic;
using OWCraft.Link;
using UnityEngine;
using UnityEngine.InputSystem;

namespace OWCraft.Player
{
	/// <summary>
	/// Sends the keyboard and mouse to Minecraft while Minecraft mode is on. Keys go as USB HID usage
	/// codes (what SkyCraft's guest expects); mouse look is not sent as input at all, it's integrated
	/// here and written into the host state as the authoritative yaw/pitch.
	/// </summary>
	public sealed class InputForwarder
	{
		static readonly Dictionary<Key, ushort> Hid = BuildMap();
		readonly HashSet<ushort> _down = new HashSet<ushort>();
		readonly bool[] _buttons = new bool[6];
		readonly Queue<char> _text = new Queue<char>();
		bool _textHooked;

		public Vector2 Cursor; // overlay pixels, top-left origin (what Minecraft's screens use)
		public const ushort HidF5 = 62;
		public Key ToggleKey = Key.F6;
		public Key OpenMenuKey = Key.O;
		public const ushort HidEscape = 41;

		static Dictionary<Key, ushort> BuildMap()
		{
			var m = new Dictionary<Key, ushort>();
			for (int i = 0; i < 26; i++) m[Key.A + i] = (ushort)(4 + i);
			for (int i = 0; i < 9; i++) m[Key.Digit1 + i] = (ushort)(30 + i);
			m[Key.Digit0] = 39;
			m[Key.Enter] = 40;
			m[Key.Escape] = 41;
			m[Key.Backspace] = 42;
			m[Key.Tab] = 43;
			m[Key.Space] = 44;
			m[Key.Minus] = 45;
			m[Key.Equals] = 46;
			m[Key.LeftBracket] = 47;
			m[Key.RightBracket] = 48;
			m[Key.Backslash] = 49;
			m[Key.Semicolon] = 51;
			m[Key.Quote] = 52;
			m[Key.Backquote] = 53;
			m[Key.Comma] = 54;
			m[Key.Period] = 55;
			m[Key.Slash] = 56;
			m[Key.CapsLock] = 57;
			for (int i = 0; i < 12; i++) m[Key.F1 + i] = (ushort)(58 + i);
			// Not F5: Outer Wilds' view is always first person; Minecraft's third person only hides the hand.
			m.Remove(Key.F5);
			m.Remove(Key.F7); // ours: clear the blocks around you
			m[Key.Insert] = 73;
			m[Key.Home] = 74;
			m[Key.PageUp] = 75;
			m[Key.Delete] = 76;
			m[Key.End] = 77;
			m[Key.PageDown] = 78;
			m[Key.RightArrow] = 79;
			m[Key.LeftArrow] = 80;
			m[Key.DownArrow] = 81;
			m[Key.UpArrow] = 82;
			m[Key.LeftCtrl] = 224;
			m[Key.LeftShift] = 225;
			m[Key.LeftAlt] = 226;
			m[Key.RightCtrl] = 228;
			m[Key.RightShift] = 229;
			m[Key.RightAlt] = 230;
			return m;
		}

		void HookText()
		{
			if (_textHooked || Keyboard.current == null) return;
			Keyboard.current.onTextInput += c => _text.Enqueue(c);
			_textHooked = true;
		}

		/// <summary>Once per frame while Minecraft has the controls. Returns the raw mouse delta (+Y down).</summary>
		public Vector2 Update(SharedLink link, bool screenOpen, int viewportW, int viewportH)
		{
			HookText();
			var kb = Keyboard.current;
			var mouse = Mouse.current;
			if (kb == null || mouse == null) return Vector2.zero;

			foreach (var pair in Hid)
			{
				if (pair.Key == ToggleKey) continue;
				// Escape opens Outer Wilds' pause menu unless a Minecraft screen is there to close.
				if (pair.Key == Key.Escape && !screenOpen && !_down.Contains(pair.Value)) continue;
				var control = kb[pair.Key];
				if (control == null) continue;
				bool pressed = control.isPressed;
				bool was = _down.Contains(pair.Value);
				if (pressed == was) continue;
				if (pressed && pair.Key == OpenMenuKey && !screenOpen)
				{
					// Minecraft's own pause menu (options, quit to title...): Escape belongs to Outer Wilds.
					ReleaseAll(link);
					link.PushInput(Proto.InOpenMenu, 0, 0);
					_down.Add(pair.Value);
					continue;
				}
				if (pressed) _down.Add(pair.Value);
				else _down.Remove(pair.Value);
				link.PushInput(Proto.InKey, pair.Value, pressed ? 1 : 0);
			}

			Button(link, 1, mouse.leftButton.isPressed);
			Button(link, 2, mouse.middleButton.isPressed);
			Button(link, 3, mouse.rightButton.isPressed);
			Button(link, 4, mouse.backButton.isPressed);
			Button(link, 5, mouse.forwardButton.isPressed);

			float scroll = mouse.scroll.ReadValue().y;
			if (Mathf.Abs(scroll) > 0.01f)
			{
				// Windows reports 120 per notch; keep whatever magnitude came, with at least one notch.
				int notches = Mathf.Max(1, Mathf.RoundToInt(Mathf.Abs(scroll) / 120f));
				for (int i = 0; i < notches; i++) link.PushInput(Proto.InScroll, 0, scroll > 0 ? 120 : -120);
			}

			Vector2 delta = mouse.delta.ReadValue();
			delta.y = -delta.y;
			if (screenOpen)
			{
				Cursor += delta;
				Cursor.x = Mathf.Clamp(Cursor.x, 0, viewportW - 1);
				Cursor.y = Mathf.Clamp(Cursor.y, 0, viewportH - 1);
				if (delta != Vector2.zero) link.PushInput(Proto.InCursor, 0, (int)Cursor.x, (int)Cursor.y);
				while (_text.Count > 0)
				{
					char c = _text.Dequeue();
					if (!char.IsControl(c)) link.PushInput(Proto.InText, 0, c);
				}
			}
			else
			{
				Cursor = new Vector2(viewportW * 0.5f, viewportH * 0.5f);
				_text.Clear();
			}
			return screenOpen ? Vector2.zero : delta;
		}

		void Button(SharedLink link, int index, bool pressed)
		{
			if (_buttons[index] == pressed) return;
			_buttons[index] = pressed;
			link.PushInput(Proto.InMouseButton, (ushort)index, pressed ? 1 : 0);
		}

		/// <summary>Lets go of everything (Outer Wilds took the controls back, or a menu opened).</summary>
		public void ReleaseAll(SharedLink link)
		{
			if (_down.Count == 0 && Array.IndexOf(_buttons, true) < 0) return;
			_down.Clear();
			Array.Clear(_buttons, 0, _buttons.Length);
			_text.Clear();
			link.PushInput(Proto.InReleaseAll, 0, 0);
		}
	}
}

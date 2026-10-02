using System.Collections.Generic;
using OWCraft.Link;
using UnityEngine;

namespace OWCraft.Player
{
	/// <summary>
	/// Runs Minecraft commands by typing them in its chat, one at a time: T opens the chat (same key on
	/// QWERTY and AZERTY), the text goes in as typed characters, Enter sends it. Needs cheats on in the
	/// Minecraft world.
	/// </summary>
	public sealed class CommandRunner
	{
		const ushort HidT = 23, HidEnter = 40;
		const float StepTimeout = 2f;

		enum Step { Idle, Opening, Sent }

		readonly Queue<string> _queue = new Queue<string>();
		Step _step;
		float _since;

		public bool Busy => _step != Step.Idle || _queue.Count > 0;

		public void Enqueue(string command) => _queue.Enqueue(command);

		public void Cancel()
		{
			_queue.Clear();
			_step = Step.Idle;
		}

		/// <summary>Once per frame while Minecraft mode is on.</summary>
		public void Update(SharedLink link, bool screenOpen)
		{
			float now = Time.unscaledTime;
			switch (_step)
			{
				case Step.Idle:
					if (_queue.Count == 0 || screenOpen) return;
					link.PushInput(Proto.InKey, HidT, 1);
					link.PushInput(Proto.InKey, HidT, 0);
					_step = Step.Opening;
					_since = now;
					return;
				case Step.Opening:
					if (screenOpen)
					{
						foreach (char c in _queue.Dequeue()) link.PushInput(Proto.InText, 0, c);
						link.PushInput(Proto.InKey, HidEnter, 1);
						link.PushInput(Proto.InKey, HidEnter, 0);
						_step = Step.Sent;
						_since = now;
					}
					else if (now - _since > StepTimeout)
					{
						OWCraft.Log("Minecraft's chat didn't open; commands cancelled");
						Cancel();
					}
					return;
				case Step.Sent:
					if (!screenOpen)
					{
						_step = Step.Idle;
					}
					else if (now - _since > StepTimeout)
					{
						link.PushInput(Proto.InKey, InputForwarder.HidEscape, 1);
						link.PushInput(Proto.InKey, InputForwarder.HidEscape, 0);
						_step = Step.Idle;
					}
					return;
			}
		}
	}
}

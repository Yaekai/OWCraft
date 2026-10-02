using UnityEngine;

namespace OWCraft.World
{
	/// <summary>
	/// Minecraft explosions (TNT, creepers) seen from Outer Wilds: Minecraft draws its own smoke and
	/// blast particles, we add the light the blast throws on the planet around it.
	/// </summary>
	public static class ExplosionEffects
	{
		public static void Spawn(double x, double y, double z, float radius)
		{
			var anchor = AnchorGrid.AnchorForMc((int)System.Math.Floor(x), (int)System.Math.Floor(z));
			if (anchor == null) return;
			var go = new GameObject("OWCraft explosion");
			go.transform.SetParent(anchor.EnsureRoot(), false);
			go.transform.localPosition = new Vector3((float)(x - anchor.McX0), (float)(y - AnchorGrid.Y0), (float)-(z - anchor.McZ0));
			var light = go.AddComponent<Light>();
			light.type = LightType.Point;
			light.color = new Color(1f, 0.75f, 0.4f);
			light.range = Mathf.Max(8f, radius * 6f);
			light.intensity = 6f;
			light.shadows = LightShadows.None;
			go.AddComponent<Fade>().Light = light;
		}

		sealed class Fade : MonoBehaviour
		{
			const float Duration = 0.6f;
			public Light Light;
			float _start, _intensity;

			void Start()
			{
				_start = Time.time;
				_intensity = Light.intensity;
			}

			void Update()
			{
				float t = (Time.time - _start) / Duration;
				if (t >= 1f)
				{
					Destroy(gameObject);
					return;
				}
				Light.intensity = _intensity * (1f - t) * (1f - t);
			}
		}
	}
}

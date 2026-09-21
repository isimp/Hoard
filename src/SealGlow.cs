using UnityEngine;

namespace Hoard
{
    /// <summary>
    /// An optional faint light on sealed chests, so they can be told apart without hovering.
    ///
    /// A light rather than a tint: tinting means writing the chest's material property block, which
    /// the game and other mods (AzuAutoStore's chest highlight, for one) also write, and whichever
    /// writes last wins. A child light touches nothing that belongs to the chest, and disappears
    /// with it when the chest is destroyed.
    ///
    /// The light carries the game's own LightLod, which switches it off at a distance and counts it
    /// against the light limit in the player's graphics settings, like any other light in the game.
    /// </summary>
    internal static class SealGlow
    {
        private const string ObjectName = "Hoard_SealGlow";
        private const float Range = 1.6f;
        private const float CullDistance = 30f;

        // Bounds wider than this belong to something bigger than a chest -- a ship or cart the
        // container sits on -- and would put the light above the whole vehicle.
        private const float MaxChestExtent = 1.5f;

        // A dedicated server renders nothing.
        private static readonly bool Headless = Application.isBatchMode;

        /// <summary>Adds, updates or removes the glow to match the chest's seal and the settings.</summary>
        internal static void Apply(Container container, bool isSealed)
        {
            if (Headless || container == null)
            {
                return;
            }

            Transform existing = container.transform.Find(ObjectName);
            bool wanted = isSealed && Plugin.Enabled && Plugin.ShowSealGlow;

            if (!wanted)
            {
                if (existing != null)
                {
                    Object.Destroy(existing.gameObject);
                }

                return;
            }

            if (existing != null)
            {
                Light current = existing.GetComponent<Light>();
                if (current != null)
                {
                    Style(current);
                }

                return;
            }

            GameObject glow = new GameObject(ObjectName);
            glow.transform.SetParent(container.transform, false);
            glow.transform.position = GlowPosition(container);

            // LightLod reads the light and its range in its own Awake, which AddComponent runs
            // immediately, so the light has to be complete first.
            Light light = glow.AddComponent<Light>();
            light.type = LightType.Point;
            light.range = Range;
            light.shadows = LightShadows.None;
            Style(light);

            LightLod lod = glow.AddComponent<LightLod>();
            lod.m_lightDistance = CullDistance;
            lod.m_shadowLod = false;
        }

        /// <summary>Re-applies the settings to every loaded sealed chest after one of them changes.</summary>
        internal static void ApplyAll()
        {
            foreach (Container c in SealSync.SealedContainers())
            {
                Apply(c, true);
            }
        }

        private static void Style(Light light)
        {
            light.color = Plugin.SealGlowColor;
            light.intensity = Plugin.SealGlowIntensity;
        }

        /// <summary>
        /// Just above the chest's visible body, so the light falls on the lid. Falls back to the
        /// container's own position for containers without a renderer of their own.
        /// </summary>
        private static Vector3 GlowPosition(Container container)
        {
            Renderer[] renderers = container.GetComponentsInChildren<Renderer>();
            bool found = false;
            Bounds bounds = default(Bounds);

            foreach (Renderer r in renderers)
            {
                if (r == null)
                {
                    continue;
                }

                if (!found)
                {
                    bounds = r.bounds;
                    found = true;
                }
                else
                {
                    bounds.Encapsulate(r.bounds);
                }
            }

            if (!found || bounds.extents.x > MaxChestExtent || bounds.extents.z > MaxChestExtent)
            {
                return container.transform.position + Vector3.up * 0.8f;
            }

            return bounds.center + Vector3.up * (bounds.extents.y + 0.2f);
        }
    }
}

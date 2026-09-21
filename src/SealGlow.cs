using System;
using UnityEngine;

namespace Hoard
{
    /// <summary>
    /// The visible mark of a sealed chest: a faint steady tint on the chest itself, and a small
    /// shimmer above it. Both are on by default and can be switched off separately.
    ///
    /// The tint goes through the game's own MaterialMan, the same channel the building highlight
    /// uses (WearNTear.Highlight, which AzuAutoStore also calls when it stores into a chest). It is
    /// registered on the same GameObject the highlight uses, so both write one property block
    /// instead of two blocks overwriting each other on the same renderers. A highlight resets the
    /// emission when it ends, so the tint is re-applied on SealSync's regular check: while a
    /// highlight runs it wins, and afterwards the seal tint returns within half a second.
    ///
    /// The shimmer is a copy of the game's vfx_TrollPheromones effect, stripped to what draws.
    /// </summary>
    internal static class SealGlow
    {
        private const string EffectName = "Hoard_SealEffect";
        private const string EffectPrefab = "vfx_TrollPheromones";
        private const float EffectScale = 0.5f;
        private const float EffectHeight = 0.9f;
        private const float EffectLightDistance = 30f;

        private static readonly int EmissionColor = Shader.PropertyToID("_EmissionColor");

        // A dedicated server renders nothing.
        private static readonly bool Headless = Application.isBatchMode;

        private static GameObject _effectSource;
        private static bool _effectMissing;

        /// <summary>Adds, updates or removes both parts to match the chest's seal and the settings.</summary>
        internal static void Apply(Container container, bool isSealed)
        {
            if (Headless || container == null)
            {
                return;
            }

            bool on = isSealed && Plugin.Enabled;
            ApplyTint(container, on && Plugin.ShowSealGlow);
            ApplyEffect(container, on && Plugin.ShowSealEffect);
        }

        /// <summary>
        /// Puts the tint back on a sealed chest. Called on every SealSync check, because a building
        /// or storage highlight removes it when it ends.
        /// </summary>
        internal static void Maintain(Container container)
        {
            if (Headless || container == null || !Plugin.Enabled || !Plugin.ShowSealGlow)
            {
                return;
            }

            // A running highlight re-applies itself every frame and schedules its own end with
            // Invoke("ResetHighlight"). Writing the tint meanwhile could win a frame and dip it.
            WearNTear piece = container.GetComponentInParent<WearNTear>();
            if (piece != null && piece.IsInvoking("ResetHighlight"))
            {
                return;
            }

            ApplyTint(container, true);
        }

        /// <summary>Re-applies the settings to every loaded sealed chest after one of them changes.</summary>
        internal static void ApplyAll()
        {
            foreach (Container c in SealSync.SealedContainers())
            {
                Apply(c, true);
            }
        }

        private static void ApplyTint(Container container, bool on)
        {
            MaterialMan man = MaterialMan.instance;
            GameObject target = TintTarget(container);
            if (man == null || target == null)
            {
                return;
            }

            if (on)
            {
                man.SetValue(target, EmissionColor, Plugin.SealGlowColor * Plugin.SealGlowStrength);
            }
            else
            {
                man.ResetValue(target, EmissionColor);
            }
        }

        /// <summary>
        /// The object the building highlight tints, so the two share one property block. When the
        /// container is only a part of a larger piece (a cart or ship), tinting that piece would
        /// tint the whole vehicle, so such containers get no tint.
        /// </summary>
        private static GameObject TintTarget(Container container)
        {
            WearNTear piece = container.GetComponentInParent<WearNTear>();
            GameObject target = piece != null ? piece.gameObject : container.gameObject;
            return target == container.gameObject ? target : null;
        }

        private static void ApplyEffect(Container container, bool on)
        {
            Transform existing = container.transform.Find(EffectName);

            if (!on)
            {
                if (existing != null)
                {
                    UnityEngine.Object.Destroy(existing.gameObject);
                }

                return;
            }

            if (existing != null)
            {
                return;
            }

            GameObject source = EffectSource();
            if (source == null)
            {
                return;
            }

            // Built inside an inactive holder, so nothing on the copy runs before it has been
            // stripped down to what draws.
            GameObject holder = new GameObject("Hoard_SealEffectHolder");
            holder.SetActive(false);
            GameObject copy = UnityEngine.Object.Instantiate(source, holder.transform, false);
            StripToVisuals(copy);

            // The effect brings its own flickering light. LightLod switches it off at a distance
            // and counts it against the light limit in the graphics settings, like the game's own.
            foreach (Light light in copy.GetComponentsInChildren<Light>(true))
            {
                if (light.GetComponent<LightLod>() == null)
                {
                    LightLod lod = light.gameObject.AddComponent<LightLod>();
                    lod.m_lightDistance = EffectLightDistance;
                    lod.m_shadowLod = false;
                }
            }

            copy.name = EffectName;
            copy.transform.SetParent(container.transform, false);
            copy.transform.localPosition = Vector3.up * EffectHeight;
            copy.transform.localRotation = Quaternion.identity;
            copy.transform.localScale = Vector3.one * EffectScale;
            UnityEngine.Object.Destroy(holder);
            copy.SetActive(true);
        }

        private static GameObject EffectSource()
        {
            if (_effectSource != null || _effectMissing)
            {
                return _effectSource;
            }

            _effectSource = ZNetScene.instance != null ? ZNetScene.instance.GetPrefab(EffectPrefab) : null;
            if (_effectSource == null && ZNetScene.instance != null)
            {
                _effectMissing = true;
                Plugin.Log.LogWarning(EffectPrefab + " was not found, sealed chests get the tint only.");
            }

            return _effectSource;
        }

        /// <summary>
        /// Leaves only what draws: renderers, particle systems, lights and the light helpers. Every
        /// other script goes, then joints and sound, then colliders, then rigidbodies, in the order
        /// Unity's component dependencies allow. Types from modules this project does not reference
        /// are matched by name.
        /// </summary>
        private static void StripToVisuals(GameObject copy)
        {
            string[][] passes =
            {
                new[] { "script", "Joint", "AudioSource" },
                new[] { "Collider" },
                new[] { "Rigidbody" },
            };

            foreach (string[] pass in passes)
            {
                foreach (Component c in copy.GetComponentsInChildren<Component>(true))
                {
                    if (c == null || c is Transform)
                    {
                        continue;
                    }

                    string type = c.GetType().Name;
                    bool isScript = c is MonoBehaviour && type != "LightFlicker" && type != "LightLod";

                    foreach (string rule in pass)
                    {
                        if (rule == "script" ? isScript : type.EndsWith(rule, StringComparison.Ordinal))
                        {
                            UnityEngine.Object.DestroyImmediate(c);
                            break;
                        }
                    }
                }
            }
        }
    }
}

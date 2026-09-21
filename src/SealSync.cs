using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace Hoard
{
    /// <summary>
    /// Notices seal changes on every machine, not just the one where the key was pressed.
    ///
    /// The seal is a value on the chest's ZDO, so it reaches every player on its own. The
    /// automation mods' chest lists do not: they are built on each machine as chests load, and only
    /// re-read when a chest reloads. Without this, sealing a chest another player already had
    /// loaded left it in their mods' lists, and unsealing one that was sealed when it loaded for
    /// them left it out until they walked away and came back.
    ///
    /// So each machine remembers the seal state of every chest it has loaded and compares it
    /// twice a second. A change, from whoever made it, is applied to this machine's mods through
    /// Interception.OnSealChanged. This needs no messages of its own: the ZDO sync that already
    /// carries the seal is the signal.
    ///
    /// The same check picks up changes that do not come from the key at all, such as a chest
    /// being bound or unbound by SeidrChest while AutoSealSeidrChest is on.
    /// </summary>
    internal static class SealSync
    {
        /// <summary>Seconds between checks. Reading a ZDO bool per chest is cheap at this rate.</summary>
        private const float Interval = 0.5f;

        private static readonly Dictionary<Container, bool> Known = new Dictionary<Container, bool>();
        private static readonly List<Container> Scratch = new List<Container>();
        private static float _nextCheck;

        [HarmonyPatch(typeof(Container), "Awake")]
        internal static class ContainerAwakePatch
        {
            // The state recorded here is the one the automation mods see when they register the
            // chest from their own Awake postfixes, so the two start out agreeing.
            private static void Postfix(Container __instance)
            {
                if (__instance != null && !Known.ContainsKey(__instance))
                {
                    bool isSealed = Exemption.IsSealed(__instance);
                    Known[__instance] = isSealed;

                    if (isSealed)
                    {
                        SealGlow.Apply(__instance, true);
                    }
                }
            }
        }

        /// <summary>Called every frame from the plugin's Update; does its work twice a second.</summary>
        internal static void Tick()
        {
            if (Known.Count == 0 || Time.unscaledTime < _nextCheck)
            {
                return;
            }

            _nextCheck = Time.unscaledTime + Interval;

            Scratch.Clear();
            Scratch.AddRange(Known.Keys);

            foreach (Container c in Scratch)
            {
                if (c == null)
                {
                    // Unloaded. A destroyed Unity object still hashes by instance id, so this
                    // removes the right entry.
                    Known.Remove(c);
                    Interception.Forget(c);
                    continue;
                }

                Refresh(c);
            }
        }

        /// <summary>
        /// Applies a seal change on this chest now if there is one. The marking key calls this
        /// straight after writing the seal, so the player who pressed it does not wait for the
        /// next check.
        /// </summary>
        internal static void Refresh(Container container)
        {
            if (container == null)
            {
                return;
            }

            bool now = Exemption.IsSealed(container);
            if (Known.TryGetValue(container, out bool before) && before == now)
            {
                return;
            }

            Known[container] = now;
            Plugin.Log.LogInfo($"Chest {(now ? "sealed" : "unsealed")}: {container.name} at {container.transform.position}");
            Interception.OnSealChanged(container, now);
            SealGlow.Apply(container, now);
        }

        /// <summary>The loaded chests that are sealed, for a target switched on mid-session.</summary>
        internal static List<Container> SealedContainers()
        {
            List<Container> result = new List<Container>();
            foreach (KeyValuePair<Container, bool> entry in Known)
            {
                if (entry.Value && entry.Key != null)
                {
                    result.Add(entry.Key);
                }
            }

            return result;
        }
    }
}

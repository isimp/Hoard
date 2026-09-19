using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace Hoard
{
    /// <summary>
    /// Installs one prefix on each discovered registration method, and moves a chest in and out of
    /// the other mods' lists when its seal changes.
    ///
    /// Intercepting registration rather than use is deliberate. Every feature these mods have --
    /// ground suction, the inventory dump hotkey, single-item store, the "where did I put it"
    /// search, crafting pulls, tame feeding -- reads from the same list, so keeping a chest out of
    /// the list disables all of them at once and keeps working when the mod grows a sixth feature.
    /// Patching the query side instead would mean chasing every call site, in a per-frame path, and
    /// one of these mods caches its query result for a quarter of a second anyway.
    ///
    /// Those lists live on each player's machine, so a seal change has to be applied on every
    /// machine, not just the one where the key was pressed. SealSync notices the change everywhere
    /// and calls OnSealChanged; this class only knows how to apply it.
    /// </summary>
    internal static class Interception
    {
        private static readonly MethodInfo PrefixMethod =
            AccessTools.Method(typeof(Interception), nameof(RegistrationPrefix));

        // The prefix is shared by every target, so it finds its own by the method it is running in.
        private static readonly Dictionary<MethodBase, Target> ByMethod = new Dictionary<MethodBase, Target>();

        /// <summary>
        /// Runs in place of another mod's "remember this container" method.
        ///
        /// <c>__0</c> rather than a named parameter because the argument is called something
        /// different in every mod, and the position is the one thing the discovery rule guarantees.
        /// A refused registration is remembered, so it can be replayed if the chest is unsealed.
        /// </summary>
        private static bool RegistrationPrefix(Container __0, MethodBase __originalMethod)
        {
            if (!Exemption.IsSealed(__0))
            {
                return true;
            }

            if (__0 != null && __originalMethod != null && ByMethod.TryGetValue(__originalMethod, out Target target))
            {
                target.Held.Add(__0);
            }

            return false;
        }

        /// <summary>Brings every target's patch state in line with config.</summary>
        internal static void ApplyAll()
        {
            foreach (Target t in Discovery.Targets)
            {
                ApplyPatchState(t);
            }
        }

        internal static void ApplyPatchState(Target target)
        {
            bool wanted = Plugin.Enabled && target.Enabled.Value && !target.Broken;

            if (wanted == target.Patched)
            {
                return;
            }

            try
            {
                if (wanted)
                {
                    ByMethod[target.Add] = target;
                    Plugin.Harmony.Patch(target.Add, prefix: new HarmonyMethod(PrefixMethod));
                    target.Patched = true;
                    Plugin.Log.LogInfo("Sealing honoured by " + target.Key);

                    // Switched on mid-session: chests that are already sealed come out now.
                    foreach (Container c in SealSync.SealedContainers())
                    {
                        Evict(target, c);
                    }
                }
                else
                {
                    Plugin.Harmony.Unpatch(target.Add, PrefixMethod);
                    target.Patched = false;
                    ByMethod.Remove(target.Add);
                    Plugin.Log.LogInfo("No longer intercepting " + target.Key);

                    // Switched off: hand back everything this target was holding, so turning a
                    // target or the whole mod off takes effect without a reload.
                    ReleaseAll(target);
                }
            }
            catch (Exception e)
            {
                // One target failing is one mod that keeps its old behaviour. It must not stop the
                // others being patched, and it must not throw out of a config callback.
                target.Broken = true;
                target.Patched = false;
                ByMethod.Remove(target.Add);
                Plugin.Log.LogWarning($"Could not patch {target.Key}, that mod will keep seeing sealed chests: {e.Message}");
            }
        }

        /// <summary>
        /// Applies a seal change to every intercepted mod on this machine: evicts a newly sealed
        /// chest, or hands an unsealed one back to the mods that were refused it.
        /// </summary>
        internal static void OnSealChanged(Container container, bool isSealed)
        {
            if (container == null)
            {
                return;
            }

            foreach (Target t in Discovery.Targets)
            {
                if (!t.Patched)
                {
                    continue;
                }

                if (isSealed)
                {
                    Evict(t, container);
                }
                else
                {
                    Restore(t, container);
                }
            }
        }

        /// <summary>Drops a chest that no longer exists from every target's held set.</summary>
        internal static void Forget(Container container)
        {
            foreach (Target t in Discovery.Targets)
            {
                t.Held.Remove(container);
            }
        }

        private static void Evict(Target t, Container container)
        {
            // Only a chest the mod is actually tracking is taken out and held. One its own checks
            // turned down stays unknown to it, and is not pushed on it later.
            if (!IsTracked(t, container))
            {
                return;
            }

            if (t.Remove == null)
            {
                Plugin.Log.LogInfo(
                    t.AssemblyName + " has no de-registration method; the seal takes effect there " +
                    "after the chest next unloads and reloads.");
                return;
            }

            try
            {
                t.Remove.Invoke(null, new object[] { container });
                t.Held.Add(container);

                // A mod's remove can leave the chest behind. AzuCraftyBoxes queues additions in
                // ContainersToAdd until its next crafting query, and its RemoveContainer only
                // acts on chests already in Containers -- so a chest sealed while still queued
                // would be flushed into the list afterwards. Take it out of whatever still holds it.
                if (IsTracked(t, container))
                {
                    Scrub(t, container);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{t.Key} refused a live update: {e.Message}");
            }
        }

        private static void Restore(Target t, Container container)
        {
            // Only registrations the mod itself asked for are replayed, through its own add method,
            // which the prefix now lets through.
            if (!t.Held.Remove(container) || container == null)
            {
                return;
            }

            try
            {
                t.Add.Invoke(null, new object[] { container });
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"{t.Key} refused a live update: {e.Message}");
            }
        }

        private static void ReleaseAll(Target t)
        {
            List<Container> held = new List<Container>(t.Held);
            t.Held.Clear();

            foreach (Container c in held)
            {
                if (c == null)
                {
                    continue;
                }

                try
                {
                    t.Add.Invoke(null, new object[] { c });
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"{t.Key} refused a live update: {e.Message}");
                    break;
                }
            }
        }

        /// <summary>Removes the chest from the mod's tracked collections directly.</summary>
        private static void Scrub(Target t, Container container)
        {
            foreach (FieldInfo f in t.Registries)
            {
                try
                {
                    object value = f.GetValue(null);

                    if (value is ICollection<Container> collection && !collection.IsReadOnly)
                    {
                        collection.Remove(container);
                    }
                    else if (value is IDictionary dictionary && !dictionary.IsReadOnly)
                    {
                        dictionary.Remove(container);
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"Could not clear a sealed chest from {t.Key}.{f.Name}: {e.Message}");
                }
            }
        }

        /// <summary>
        /// Whether the mod currently tracks the chest, read from its own static collections. When
        /// they cannot be read, the answer is yes: a harmless extra eviction beats leaving a sealed
        /// chest in a mod's hands.
        /// </summary>
        private static bool IsTracked(Target t, Container container)
        {
            if (t.Registries.Count == 0)
            {
                return true;
            }

            try
            {
                foreach (FieldInfo f in t.Registries)
                {
                    object value = f.GetValue(null);

                    if (value is ICollection<Container> collection && collection.Contains(container))
                    {
                        return true;
                    }

                    if (value is IDictionary dictionary && dictionary.Contains(container))
                    {
                        return true;
                    }
                }

                return false;
            }
            catch
            {
                return true;
            }
        }
    }
}

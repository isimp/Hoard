using System;
using System.Reflection;
using HarmonyLib;

namespace Hoard
{
    /// <summary>
    /// Installs one prefix on each discovered registration method, and moves a chest in and out of
    /// the other mods' lists when its seal is toggled.
    ///
    /// Intercepting registration rather than use is deliberate. Every feature these mods have --
    /// ground suction, the inventory dump hotkey, single-item store, the "where did I put it"
    /// search, crafting pulls, tame feeding -- reads from the same list, so keeping a chest out of
    /// the list disables all of them at once and keeps working when the mod grows a sixth feature.
    /// Patching the query side instead would mean chasing every call site, in a per-frame path, and
    /// one of these mods caches its query result for a quarter of a second anyway.
    /// </summary>
    internal static class Interception
    {
        private static readonly MethodInfo PrefixMethod =
            AccessTools.Method(typeof(Interception), nameof(RegistrationPrefix));

        /// <summary>
        /// Runs in place of another mod's "remember this container" method.
        ///
        /// <c>__0</c> rather than a named parameter because the argument is called something
        /// different in every mod, and the position is the one thing the discovery rule guarantees.
        /// </summary>
        private static bool RegistrationPrefix(Container __0)
        {
            return !Exemption.IsSealed(__0);
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
                    Plugin.Harmony.Patch(target.Add, prefix: new HarmonyMethod(PrefixMethod));
                    target.Patched = true;
                    Plugin.Log.LogInfo("Sealing honoured by " + target.Key);
                }
                else
                {
                    Plugin.Harmony.Unpatch(target.Add, PrefixMethod);
                    target.Patched = false;
                    Plugin.Log.LogInfo("No longer intercepting " + target.Key);
                }
            }
            catch (Exception e)
            {
                // One target failing is one mod that keeps its old behaviour. It must not stop the
                // others being patched, and it must not throw out of a config callback.
                target.Broken = true;
                target.Patched = false;
                Plugin.Log.LogWarning($"Could not patch {target.Key}, that mod will keep seeing sealed chests: {e.Message}");
            }
        }

        /// <summary>
        /// Called when a chest is sealed or unsealed, so the change takes effect immediately
        /// instead of at the next time that chest happens to be re-registered.
        /// </summary>
        internal static void OnSealChanged(Container container, bool isSealed)
        {
            if (container == null)
            {
                return;
            }

            object[] args = { container };

            foreach (Target t in Discovery.Targets)
            {
                if (!t.Patched)
                {
                    continue;
                }

                try
                {
                    if (isSealed)
                    {
                        // No remove method means the chest stays in that mod's list until it is
                        // unloaded. Say so rather than pretending it worked.
                        if (t.Remove == null)
                        {
                            Plugin.Log.LogInfo(
                                t.AssemblyName + " has no de-registration method; the seal takes effect there " +
                                "after the chest next unloads and reloads.");
                            continue;
                        }

                        t.Remove.Invoke(null, args);
                    }
                    else
                    {
                        // Unsealing calls the mod's own add method, which skips whatever gate it
                        // normally applies first (creator checks, ward checks). That gate was
                        // satisfied when you built or opened the chest, and you are stood next to
                        // it pressing a key -- and the worst case is a chest you own being tracked
                        // by a mod that would have tracked it anyway.
                        t.Add.Invoke(null, args);
                    }
                }
                catch (Exception e)
                {
                    Plugin.Log.LogWarning($"{t.Key} refused a live update: {e.Message}");
                }
            }
        }
    }
}

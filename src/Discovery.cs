using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using BepInEx.Configuration;

namespace Hoard
{
    /// <summary>One registration point found in some other mod.</summary>
    internal sealed class Target
    {
        internal string AssemblyName;
        internal MethodInfo Add;

        /// <summary>The de-registration method on the same type, if the mod has one. May be null.</summary>
        internal MethodInfo Remove;

        internal ConfigEntry<bool> Enabled;
        internal bool Patched;

        /// <summary>True when the mod is still registering because patching it threw.</summary>
        internal bool Broken;

        /// <summary>
        /// The mod's static container collections, minus any whose name marks it as a pending-
        /// removal queue. Read to tell whether the mod is currently tracking a given chest.
        /// </summary>
        internal List<FieldInfo> Registries = new List<FieldInfo>();

        /// <summary>
        /// Chests this mod asked to track but is not being allowed to, because they are sealed:
        /// either the registration was refused, or the chest was evicted when it got sealed. On
        /// unseal exactly these are handed back through the mod's own add method, so a chest the
        /// mod never wanted (its own access checks said no) is never forced on it.
        /// </summary>
        internal readonly HashSet<Container> Held = new HashSet<Container>();

        internal string Key => AssemblyName + "::" + Add.DeclaringType?.FullName + "::" + Add.Name;

        internal string Describe()
        {
            string state = Broken ? "broken" : Patched ? "active" : "off";
            string evict = Remove != null ? Remove.Name : "(none)";
            return $"[{state}] {Key}  evict via {evict}";
        }
    }

    /// <summary>
    /// Finds the container registries that other mods keep.
    ///
    /// Valheim has no container registry of its own and no per-chest "leave me alone" flag, so
    /// there is nothing in the game to hook -- every mod of this kind invents its own static list
    /// and fills it from a <c>Container.Awake</c> postfix. What they do have in common is the
    /// *shape* of that invention, and that is what this keys on:
    ///
    ///   a type holding a static generic collection with Container as a type argument,
    ///   plus a static void method on that type taking exactly one Container.
    ///
    /// The static-collection requirement does essentially all the filtering; note that no
    /// name matching is used to decide *inclusion*, only to tell an add from a remove. A mod that
    /// calls its method <c>Watch</c> or <c>Index</c> is still found, it just starts out disabled
    /// so you can look at it before it takes effect.
    /// </summary>
    internal static class Discovery
    {
        internal static readonly List<Target> Targets = new List<Target>();

        /// <summary>
        /// Registration points found in an earlier session, in a mod that is still installed, but
        /// not found now -- almost always a mod update that moved or renamed its chest list. Seals
        /// are not honoured by that mod until Hoard recognises it again.
        /// </summary>
        internal static readonly List<string> Lost = new List<string>();

        private static readonly HashSet<string> _seen = new HashSet<string>();

        private static bool _done;

        // Verbs that mean "stop tracking this". Checked first, because DeRegisterContainer would
        // otherwise be read as a Register.
        private static readonly string[] RemoveVerbs =
            { "remove", "deregister", "unregister", "unwatch", "untrack", "forget", "drop", "purge", "clear" };

        // Verbs that mean "start tracking this" confidently enough to switch on without asking.
        private static readonly string[] AddVerbs =
            { "add", "register", "track", "watch" };

        // Nothing here can own a container registry, and walking them costs real time at startup.
        // Split into exact names and dotted prefixes on purpose: a bare "System" or "Valheim"
        // prefix would also swallow a mod called SystemsOverhaul or ValheimFortress.
        private static readonly string[] SkippedAssemblyNames =
        {
            "mscorlib", "netstandard", "System", "0Harmony", "HarmonyX", "MonoMod",
            "Newtonsoft.Json", "YamlDotNet", "ICSharpCode.SharpZipLib", "SemanticVersioning",
        };

        private static readonly string[] SkippedAssemblyPrefixes =
        {
            "assembly_", "UnityEngine", "Unity.", "System.", "BepInEx", "Mono.", "MonoMod.",
            "Microsoft.", "Valheim.", "Splatform",
        };

        internal static void EnsureDiscovered()
        {
            if (_done)
            {
                return;
            }

            _done = true;

            try
            {
                Scan();
            }
            catch (Exception e)
            {
                // A mod that cannot discover anything is inert, which is safe. Never worth taking
                // the game down for.
                Plugin.Log.LogError($"Discovery failed, Hoard will do nothing this session: {e}");
                return;
            }

            Interception.ApplyAll();

            try
            {
                FindLostTargets();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning("Could not compare with earlier sessions: " + e.Message);
            }
        }

        /// <summary>
        /// Compares this session's finds with the target switches left in the config by earlier
        /// sessions. BepInEx keeps an entry that was read from the file but never bound this session
        /// as an orphan, and writes it back on save, so a target that has disappeared stays visible
        /// here until its mod is fixed or uninstalled. Only enabled targets of mods that are still
        /// loaded count: a removed mod or a target switched off is nothing to warn about.
        /// </summary>
        private static void FindLostTargets()
        {
            Dictionary<ConfigDefinition, string> orphans = Plugin.OrphanedConfigEntries();
            if (orphans == null || orphans.Count == 0)
            {
                return;
            }

            HashSet<string> loaded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    loaded.Add(asm.GetName().Name);
                }
                catch
                {
                    // An assembly that cannot report its name cannot be one we are looking for.
                }
            }

            // A mod whose registration point was found again under a new name is handled; its old
            // switch is just left over.
            HashSet<string> handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (Target t in Targets)
            {
                if (t.Patched)
                {
                    handled.Add(t.AssemblyName);
                }
            }

            foreach (KeyValuePair<ConfigDefinition, string> orphan in orphans)
            {
                if (orphan.Key.Section != "Targets")
                {
                    continue;
                }

                string key = orphan.Key.Key;
                int split = key.IndexOf("::", StringComparison.Ordinal);
                if (split <= 0)
                {
                    continue;
                }

                string assembly = key.Substring(0, split);
                if (!loaded.Contains(assembly) || handled.Contains(assembly))
                {
                    continue;
                }

                if (!bool.TryParse(orphan.Value?.Trim(), out bool enabled) || !enabled)
                {
                    continue;
                }

                Lost.Add(key);
                Plugin.Log.LogWarning(
                    key.Substring(0, split) + " is installed, but Hoard no longer recognises " + key.Substring(split + 2) +
                    ". Sealed chests are not protected from that mod. Its update probably changed how it keeps its chests.");
            }
        }

        private static void Scan()
        {
            Stopwatch clock = Stopwatch.StartNew();
            int assembliesWalked = 0;

            foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic || IsSkipped(asm))
                {
                    continue;
                }

                assembliesWalked++;

                foreach (Type type in TypesOf(asm))
                {
                    try
                    {
                        if (type == null || !HoldsContainerCollection(type))
                        {
                            continue;
                        }

                        CollectFrom(asm, type);
                    }
                    catch (Exception e)
                    {
                        // Isolated per type: one awkward type in one mod must not cost us the
                        // other mods' registries.
                        Plugin.Log.LogWarning($"Skipped {type?.FullName} while scanning: {e.Message}");
                    }
                }
            }

            clock.Stop();
            Plugin.Log.LogInfo(
                $"Scanned {assembliesWalked} assemblies in {clock.ElapsedMilliseconds}ms, " +
                $"found {Targets.Count} container registration point(s).");

            foreach (Target t in Targets)
            {
                Plugin.Log.LogInfo("  " + t.Key + (t.Remove != null ? "  (evict: " + t.Remove.Name + ")" : "  (no evict method)"));
            }
        }

        private static void CollectFrom(Assembly asm, Type type)
        {
            const BindingFlags Flags =
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            MethodInfo[] candidates;
            try
            {
                candidates = type.GetMethods(Flags).Where(TakesOneContainer).ToArray();
            }
            catch
            {
                return;
            }

            MethodInfo remove = candidates.FirstOrDefault(m => StartsWithAny(m.Name, RemoveVerbs));

            foreach (MethodInfo add in candidates)
            {
                if (StartsWithAny(add.Name, RemoveVerbs))
                {
                    continue;
                }

                string name = asm.GetName().Name;

                Target target = new Target
                {
                    AssemblyName = name,
                    Add = add,
                    Remove = remove,
                    Registries = RegistryFields(type),
                };

                // An assembly loaded from two paths would otherwise bind the same config key
                // twice, which BepInEx refuses, and that would abort the whole scan.
                if (!_seen.Add(target.Key))
                {
                    continue;
                }

                target.Enabled = Plugin.BindTargetToggle(target, DefaultFor(name, add));
                target.Enabled.SettingChanged += (_, __) => Interception.ApplyPatchState(target);

                Targets.Add(target);
            }
        }

        /// <summary>
        /// On by default when the method name says plainly that it is adding to a registry, and
        /// the mod is not on the opt-out list. Anything discovered by shape but not by verb starts
        /// off, so a heuristic can never silently change how one of your mods behaves.
        /// </summary>
        private static bool DefaultFor(string assemblyName, MethodInfo add)
        {
            if (Plugin.IsDefaultOffAssembly(assemblyName))
            {
                return false;
            }

            return StartsWithAny(add.Name, AddVerbs);
        }

        private static bool TakesOneContainer(MethodInfo m)
        {
            if (m.IsAbstract || m.IsGenericMethodDefinition || m.ContainsGenericParameters)
            {
                return false;
            }

            if (m.ReturnType != typeof(void))
            {
                return false;
            }

            ParameterInfo[] ps = m.GetParameters();
            return ps.Length == 1 && ps[0].ParameterType == typeof(Container);
        }

        /// <summary>
        /// True when the type keeps a static generic collection with Container somewhere in its
        /// type arguments -- List&lt;Container&gt;, HashSet&lt;Container&gt;,
        /// Dictionary&lt;Container, T&gt; and so on.
        /// </summary>
        private static bool HoldsContainerCollection(Type type)
        {
            const BindingFlags Flags =
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            FieldInfo[] fields;
            try
            {
                fields = type.GetFields(Flags);
            }
            catch
            {
                return false;
            }

            foreach (FieldInfo f in fields)
            {
                Type ft = f.FieldType;
                if (!ft.IsGenericType)
                {
                    continue;
                }

                foreach (Type arg in ft.GetGenericArguments())
                {
                    if (arg == typeof(Container))
                    {
                        return true;
                    }
                }
            }

            return false;
        }

        /// <summary>
        /// The static collections on a registry type that hold its tracked containers. Fields
        /// named like a removal queue (AzuAutoStore's and GrabMaterials' ContainersToRemove) are
        /// left out: a chest sitting in one of those is on its way out, not tracked.
        /// </summary>
        private static List<FieldInfo> RegistryFields(Type type)
        {
            const BindingFlags Flags =
                BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

            List<FieldInfo> result = new List<FieldInfo>();
            try
            {
                foreach (FieldInfo f in type.GetFields(Flags))
                {
                    Type ft = f.FieldType;
                    if (!ft.IsGenericType || f.Name.IndexOf("remove", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        continue;
                    }

                    if (ft.GetGenericArguments().Contains(typeof(Container)))
                    {
                        result.Add(f);
                    }
                }
            }
            catch
            {
                // Leaves the list empty, which Interception reads as "assume tracked".
            }

            return result;
        }

        private static IEnumerable<Type> TypesOf(Assembly asm)
        {
            try
            {
                return asm.GetTypes();
            }
            catch (ReflectionTypeLoadException e)
            {
                // Normal for a mod with an optional dependency that is not installed: the types
                // that did load are still worth looking at.
                return e.Types.Where(t => t != null);
            }
            catch
            {
                return Array.Empty<Type>();
            }
        }

        private static bool IsSkipped(Assembly asm)
        {
            // Hoard's own SealSync has exactly the shape this scan looks for, so the plugin's
            // assembly is excluded by identity, not by a name that could change.
            if (asm == typeof(Plugin).Assembly)
            {
                return true;
            }

            string name;
            try
            {
                name = asm.GetName().Name;
            }
            catch
            {
                return true;
            }

            foreach (string exact in SkippedAssemblyNames)
            {
                if (string.Equals(name, exact, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            foreach (string prefix in SkippedAssemblyPrefixes)
            {
                if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        private static bool StartsWithAny(string name, string[] verbs)
        {
            foreach (string v in verbs)
            {
                if (name.StartsWith(v, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }
    }
}

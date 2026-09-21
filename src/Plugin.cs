using System;
using System.Collections.Generic;
using System.Text;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using ServerSync;
using HarmonyLib;
using UnityEngine;

namespace Hoard
{
    /// <summary>
    /// Hoard. Seal a chest and the container-automation mods leave it alone.
    ///
    /// The problem it solves: mods like AzuAutoStore, AzuCraftyBoxes, GrabMaterials and PetPantry
    /// treat every chest you own as fair game. Their own configuration can only exclude a *prefab*
    /// -- every personal chest, or none -- which is no use when the thing you want left alone is
    /// one particular chest: an emergency stash, or the chest SeidrChest has bound.
    ///
    /// Valheim itself has no container registry and no per-chest opt-out, so there is no vanilla
    /// seam to hook. See Discovery for what is hooked instead and why.
    ///
    /// On multiplayer: the seal itself is a value on the chest's ZDO, so it already replicates to
    /// every peer and persists in the world save without any traffic of ours. What cannot be made
    /// server-side is the *enforcement* -- the automation mods run wholly on each client, and the
    /// server only ever sees the resulting inventory write, indistinguishable from a player
    /// dragging items in by hand. So a server that wants its seals respected has to require the
    /// mod, which is what ModRequired below does.
    /// </summary>
    [BepInPlugin(Guid, Name, Version)]
    [BepInProcess("valheim.exe")]
    // The server has to run it too: ServerSync pushes the rules from there, and it is the server
    // that refuses clients without Hoard. Nothing player-facing runs there -- the marking key waits
    // for a local player looking at a chest, which a dedicated server never has.
    [BepInProcess("valheim_server.exe")]
    public class Plugin : BaseUnityPlugin
    {
        public const string Guid = "isimp.Hoard";
        public const string Name = "Hoard";
        public const string Version = "0.1.1";

        /// <summary>
        /// The oldest Hoard a peer may run and still be let in. Raise it only when a release
        /// actually breaks agreement between clients -- not merely because the version moved.
        /// ServerSync demands each side be at least the other's minimum, so a minimum equal to the
        /// current version would lock out a player one patch *ahead* as firmly as one behind.
        ///
        /// Hoard has no RPCs and no protocol of its own; the only thing two clients must agree on
        /// is the ZDO key, which is fixed. So this should essentially never move.
        /// </summary>
        private const string MinimumVersion = "0.1.0";

        public static ManualLogSource Log;
        internal static Harmony Harmony;

        private static ConfigFile _config;
        private static ConfigSync _sync;

        private static ConfigEntry<bool> _lockConfig;
        private static ConfigEntry<bool> _enabled;
        private static ConfigEntry<KeyCode> _markKey;
        private static ConfigEntry<KeyCode> _markModifier;
        private static ConfigEntry<bool> _autoSealSeidrChest;
        private static ConfigEntry<bool> _showHoverHint;
        private static ConfigEntry<bool> _showSealGlow;
        private static ConfigEntry<Color> _sealGlowColor;
        private static ConfigEntry<float> _sealGlowIntensity;
        private static ConfigEntry<string> _defaultOffAssemblies;

        internal static bool Enabled => _enabled == null || _enabled.Value;
        internal static KeyCode MarkKey => _markKey?.Value ?? KeyCode.K;
        internal static KeyCode MarkModifier => _markModifier?.Value ?? KeyCode.None;
        internal static bool AutoSealSeidrChest => _autoSealSeidrChest != null && _autoSealSeidrChest.Value;
        internal static bool ShowHoverHint => _showHoverHint == null || _showHoverHint.Value;
        internal static bool ShowSealGlow => _showSealGlow != null && _showSealGlow.Value;
        internal static Color SealGlowColor => _sealGlowColor?.Value ?? new Color(0.56f, 0.83f, 1f);
        internal static float SealGlowIntensity => _sealGlowIntensity?.Value ?? 1f;

        /// <summary>
        /// Binds a setting that is a *rule* and hands it to ServerSync, so an admin's value governs
        /// everyone on their server.
        ///
        /// The split is between rules and preferences. A rule decides what a seal actually does, and
        /// has to hold for everyone or the seal is worthless -- one player whose client still lets
        /// AzuAutoStore see the chest empties it for the rest. A preference only has to be right on
        /// the machine it is set on: which key you press, whether you want the hover line and what
        /// it says. Those stay local, and a server has no business overriding them.
        /// </summary>
        private ConfigEntry<T> Rule<T>(string group, string name, T value, string description)
        {
            ConfigEntry<T> entry = Config.Bind(group, name, value, description);
            _sync.AddConfigEntry(entry).SynchronizedConfig = true;
            return entry;
        }

        private void Awake()
        {
            Log = Logger;
            _config = Config;

            // ModRequired is true, which is the whole point of this mod on a server: the automation
            // mods run on each client, so a player without Hoard is a player whose AzuAutoStore
            // empties your sealed chest regardless of what anyone else has agreed. There is no
            // server-side way to stop that -- refusing the connection is the only lever there is.
            _sync = new ConfigSync(Guid)
            {
                DisplayName = Name,
                CurrentVersion = Info.Metadata.Version.ToString(),
                MinimumRequiredVersion = MinimumVersion,
                ModRequired = true,
            };

            // Bound plainly rather than through Rule, but synchronised all the same:
            // AddLockingConfigEntry calls AddConfigEntry itself. It has to be -- a lock each client
            // could switch off would not be a lock.
            _lockConfig = Config.Bind("General", "LockConfiguration", true,
                "Whether the server's sealing rules override local settings. Admins are exempt. The key and " +
                "hover text settings always stay local.");
            _sync.AddLockingConfigEntry(_lockConfig);

            _enabled = Rule("General", "Enabled", true,
                "Whether seals are honoured. Turning this off releases every sealed chest to the automation " +
                "mods without clearing the seals, so turning it back on restores them.");

            // A raw KeyCode read through legacy Input rather than a BepInEx KeyboardShortcut: a
            // KeyboardShortcut refuses to fire while any other keyboard key is held, which would
            // make this silently dead whenever you press it while still walking.
            _markKey = Config.Bind("General", "MarkKey", KeyCode.K,
                "Look at a chest and press this to seal or unseal it.");

            _markModifier = Config.Bind("General", "MarkModifier", KeyCode.None,
                "Optional key that must be held along with MarkKey, such as LeftAlt.");

            _autoSealSeidrChest = Rule("General", "AutoSealSeidrChest", false,
                "Automatically seal the chest SeidrChest currently has bound. SeidrChest keeps that chest loaded " +
                "at its real position, so automation mods near your base treat it as an ordinary chest. The seal " +
                "lifts when the chest is unbound.");

            _showHoverHint = Config.Bind("Display", "ShowHoverHint", true,
                "Show the key and whether the chest is sealed in a chest's hover text. The message on sealing " +
                "or unsealing shows either way.");

            _defaultOffAssemblies = Rule("Targets", "DefaultOffAssemblies", "CarturMapPins",
                "Comma-separated assembly names whose registration points start switched off when discovered. " +
                "The default covers a map pin mod that only reads chests. This only sets the initial value; each " +
                "target's own entry below takes over afterwards.");

            _showSealGlow = Config.Bind("Display", "ShowSealGlow", false,
                "Give sealed chests a faint glow, so they can be told apart without hovering over them.");

            _sealGlowColor = Config.Bind("Display", "SealGlowColor", new Color(0.56f, 0.83f, 1f),
                "The colour of that glow.");

            _sealGlowIntensity = Config.Bind("Display", "SealGlowIntensity", 1f,
                new ConfigDescription("The brightness of that glow.", new AcceptableValueRange<float>(0.1f, 3f)));

            _showSealGlow.SettingChanged += (_, __) => SealGlow.ApplyAll();
            _sealGlowColor.SettingChanged += (_, __) => SealGlow.ApplyAll();
            _sealGlowIntensity.SettingChanged += (_, __) => SealGlow.ApplyAll();

            _enabled.SettingChanged += (_, __) =>
            {
                Interception.ApplyAll();
                SealGlow.ApplyAll();
            };

            Harmony = new Harmony(Guid);

            try
            {
                Harmony.PatchAll(typeof(Marking.ContainerGetHoverTextPatch));
                Harmony.PatchAll(typeof(SealSync.ContainerAwakePatch));
                Harmony.PatchAll(typeof(LostTargetNotice));
                Harmony.PatchAll(typeof(Bootstrap));
            }
            catch (Exception e)
            {
                Log.LogError($"Failed to apply own patches, Hoard will do nothing this session: {e}");
                return;
            }

            RegisterConsoleCommand();

            Log.LogInfo(Name + " " + Version + " loaded.");
        }

        private void Update()
        {
            Marking.Poll();
            SealSync.Tick();
        }

        private void OnDestroy()
        {
            Config.Save();
        }

        /// <summary>
        /// Creates the per-target on/off entry. These are bound during discovery rather than in
        /// Awake because until the scan runs there is nothing to name.
        /// </summary>
        internal static ConfigEntry<bool> BindTargetToggle(Target target, bool defaultValue)
        {
            ConfigEntry<bool> entry = _config.Bind("Targets", target.Key, defaultValue,
                "Whether a sealed chest is hidden from this registration point. " +
                (target.Remove == null
                    ? "This mod has no de-registration method, so changes here take effect after the chest reloads."
                    : "Sealing or unsealing a chest takes effect here immediately."));

            // A rule, not a preference: which mods honour a seal has to be the same for everyone,
            // or one player's client quietly empties the chest for the rest.
            //
            // These are bound during discovery, at the main menu, which is before any server
            // connection -- so they are registered in time to be synchronised. Clients legitimately
            // differ in which targets exist, because they differ in which mods they have installed;
            // ServerSync simply ignores a key the receiving side does not know, which is the right
            // outcome (a client without PetPantry has no use for PetPantry's toggle).
            if (_sync != null)
            {
                _sync.AddConfigEntry(entry).SynchronizedConfig = true;
            }

            return entry;
        }

        internal static bool IsDefaultOffAssembly(string assemblyName)
        {
            string raw = _defaultOffAssemblies?.Value;
            if (string.IsNullOrEmpty(raw))
            {
                return false;
            }

            foreach (string entry in raw.Split(','))
            {
                if (string.Equals(entry.Trim(), assemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// Registered with isCheat false, which also makes it reachable from chat as /hoard --
        /// Chat.InputText strips the slash and hands anything non-cheat to TryRunCommand.
        /// </summary>
        /// <summary>
        /// The config entries read from the file that nothing has bound this session. BepInEx keeps
        /// them in a private property; reading it is the only way to see what earlier sessions knew.
        /// </summary>
        internal static Dictionary<ConfigDefinition, string> OrphanedConfigEntries()
        {
            try
            {
                return AccessTools.Property(typeof(ConfigFile), "OrphanedEntries")?.GetValue(_config, null)
                    as Dictionary<ConfigDefinition, string>;
            }
            catch (Exception e)
            {
                Log.LogWarning("Could not read earlier config entries: " + e.Message);
                return null;
            }
        }

        /// <summary>
        /// Tells the player once per session, when their character first spawns, that a mod which
        /// used to honour seals no longer does. The log alone is easy to miss, and a seal that
        /// silently stopped working is the one failure this mod must not hide.
        /// </summary>
        [HarmonyPatch(typeof(Player), "OnSpawned")]
        private static class LostTargetNotice
        {
            private static bool _shown;

            private static void Postfix(Player __instance)
            {
                if (_shown || __instance != Player.m_localPlayer)
                {
                    return;
                }

                int broken = 0;
                foreach (Target t in Discovery.Targets)
                {
                    if (t.Broken)
                    {
                        broken++;
                    }
                }

                int problems = Discovery.Lost.Count + broken;
                if (problems == 0)
                {
                    return;
                }

                _shown = true;

                __instance.Message(MessageHud.MessageType.TopLeft,
                    "Hoard: " + problems + " supported mod(s) no longer honour seals. Type /hoard for details.");

                if (Chat.instance != null)
                {
                    foreach (string lost in Discovery.Lost)
                    {
                        Chat.instance.AddString("Hoard: no longer recognised, seals not honoured: " + lost);
                    }

                    foreach (Target t in Discovery.Targets)
                    {
                        if (t.Broken)
                        {
                            Chat.instance.AddString("Hoard: could not be patched, seals not honoured: " + t.Key);
                        }
                    }
                }
            }
        }

        private static void RegisterConsoleCommand()
        {
            new Terminal.ConsoleCommand("hoard", "List the container registration points Hoard found.",
                args =>
                {
                    List<Target> targets = Discovery.Targets;

                    if (targets.Count == 0 && Discovery.Lost.Count == 0)
                    {
                        args.Context?.AddString("Hoard: nothing discovered (no container-automation mods loaded).");
                        return;
                    }

                    StringBuilder sb = new StringBuilder();
                    sb.Append("Hoard: ").Append(targets.Count).Append(" registration point(s)");
                    foreach (Target t in targets)
                    {
                        sb.Append('\n').Append("  ").Append(t.Describe());
                    }

                    foreach (string lost in Discovery.Lost)
                    {
                        sb.Append('\n').Append("  [no longer found] ").Append(lost);
                    }

                    string report = sb.ToString();
                    args.Context?.AddString(report);
                    Log.LogInfo(report);
                });
        }

        /// <summary>
        /// Runs the discovery scan once, after every other plugin has loaded. BepInEx load order
        /// gives no guarantee that the mods being looked for exist yet during our own Awake, so
        /// this waits for the main menu -- and for Game.Start too, in case a session skips it.
        /// </summary>
        [HarmonyPatch]
        private static class Bootstrap
        {
            // String names, not nameof: both are private Unity messages, so there is nothing to
            // refer to from out here.
            [HarmonyPostfix]
            [HarmonyPatch(typeof(FejdStartup), "Awake")]
            private static void AfterMenu() => Discovery.EnsureDiscovered();

            [HarmonyPostfix]
            [HarmonyPatch(typeof(Game), "Start")]
            private static void AfterGameStart() => Discovery.EnsureDiscovered();
        }
    }
}

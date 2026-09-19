using HarmonyLib;
using UnityEngine;

namespace Hoard
{
    /// <summary>
    /// The player-facing half: look at a chest, press the key, and it is sealed. Plus the hover
    /// line that shows the key and whether the chest is sealed.
    ///
    /// Who may seal a chest is decided by the game's own rules for touching it: the vanilla ward
    /// (guard stone) covering it, and the chest's privacy setting. A player who is not permitted on
    /// the ward cannot change the seal of a chest inside it -- the same line vanilla draws for
    /// opening that chest.
    /// </summary>
    internal static class Marking
    {
        /// <summary>
        /// Called every frame from the plugin's Update. Cheap until the key is actually down.
        /// </summary>
        internal static void Poll()
        {
            if (!Plugin.Enabled || Player.m_localPlayer == null)
            {
                return;
            }

            if (!Input.GetKeyDown(Plugin.MarkKey))
            {
                return;
            }

            KeyCode modifier = Plugin.MarkModifier;
            if (modifier != KeyCode.None && !Input.GetKey(modifier))
            {
                return;
            }

            if (!InputAllowed())
            {
                return;
            }

            ToggleHovered();
        }

        private static void ToggleHovered()
        {
            Player player = Player.m_localPlayer;

            Container container = HoveredContainer(player);
            if (container == null)
            {
                // Silent: the key is pressed while looking at nothing far more often than at a chest.
                return;
            }

            if (Exemption.IsSealedBySeidrRule(container))
            {
                Message(player, "Sealed automatically while bound as a Seidr chest.");
                return;
            }

            // flash: true, so a ward that refuses lights up the way it does when you try to open
            // the chest.
            if (!HasAccess(container, flash: true))
            {
                Message(player, "$msg_cantopen");
                return;
            }

            // Sealing claims ownership of the chest's ZDO, and a chest's contents are only saved by
            // its owner. Taking ownership from a player who has it open would stop their item moves
            // being saved, so refuse the same way vanilla refuses a second opener.
            if (InUse(container))
            {
                Message(player, "$msg_inuse");
                return;
            }

            bool sealing = !Exemption.IsSealed(container);

            if (!Exemption.SetSealed(container, sealing))
            {
                Message(player, "That container is not ready yet.");
                return;
            }

            SealSync.Refresh(container);

            Message(player, sealing
                ? "Chest sealed: automation mods will leave it alone."
                : "Chest unsealed: automation mods can use it again.");
        }

        /// <summary>
        /// Whether anyone has the chest open. <c>IsInUse</c> is only true on the client that has it
        /// open; everyone else reads the flag its owner writes to the ZDO in <c>SetInUse</c>.
        /// </summary>
        private static bool InUse(Container container)
        {
            if (container.IsInUse())
            {
                return true;
            }

            ZNetView nview = Exemption.Nview(container);
            ZDO zdo = nview != null && nview.IsValid() ? nview.GetZDO() : null;
            return zdo != null && zdo.GetInt(ZDOVars.s_inUse) == 1;
        }

        /// <summary>
        /// The chest under the crosshair. Containers sit on child colliders and, on vehicles, off
        /// to one side of the root, so neither direction alone is enough.
        /// </summary>
        private static Container HoveredContainer(Player player)
        {
            GameObject hovered = player.GetHoverObject();
            if (hovered == null)
            {
                return null;
            }

            Container container = hovered.GetComponentInParent<Container>();
            if (container != null)
            {
                return container;
            }

            Transform root = hovered.transform.root;
            return root != null ? root.GetComponentInChildren<Container>() : null;
        }

        /// <summary>
        /// Whether this player may change the chest's seal: the vanilla ward around it, and the
        /// chest's own privacy setting.
        ///
        /// The ward is checked whether or not the chest itself opts into guard-stone checks
        /// (m_checkGuardStone). PrivateArea.CheckAccess answers true when no ward covers the point,
        /// so this only ever restricts inside a ward -- and there, being on the ward is the
        /// requirement. Default wardCheck:false gives the same answer vanilla's Container.Interact
        /// gets. Mods that patch this method, such as ProtectiveWards, are honoured along with it.
        ///
        /// Container.CheckAccess is private in the stock assembly, so the privacy half reproduces
        /// it -- it is three lines, and both pieces it reads are public.
        /// </summary>
        internal static bool HasAccess(Container container, bool flash)
        {
            if (!PrivateArea.CheckAccess(container.transform.position, 0f, flash))
            {
                return false;
            }

            switch (container.m_privacy)
            {
                case Container.PrivacySetting.Public:
                    return true;

                case Container.PrivacySetting.Private:
                    Piece piece = container.GetComponent<Piece>() ?? container.GetComponentInParent<Piece>();
                    return piece != null && piece.IsCreator();

                default:
                    return false;
            }
        }

        /// <summary>
        /// Character.TakeInput is protected in the stock assembly, so this is the same set of
        /// "is the player actually driving right now" checks spelled out through public API.
        /// </summary>
        private static bool InputAllowed()
        {
            Player player = Player.m_localPlayer;

            if (Chat.instance != null && Chat.instance.HasFocus())
            {
                return false;
            }

            if (global::Console.IsVisible() || TextInput.IsVisible() || StoreGui.IsVisible() || InventoryGui.IsVisible())
            {
                return false;
            }

            if (TextViewer.instance != null && TextViewer.instance.IsVisible())
            {
                return false;
            }

            if (Minimap.IsOpen() || GameCamera.InFreeFly())
            {
                return false;
            }

            return !player.InCutscene()
                   && !player.InBed()
                   && !player.IsTeleporting()
                   && !player.IsDead()
                   && !player.InPlaceMode();
        }

        /// <summary>The key as the hover line shows it, with the modifier when one is set.</summary>
        private static string KeyLabel()
        {
            KeyCode modifier = Plugin.MarkModifier;
            string key = modifier == KeyCode.None
                ? KeyLabels.Of(Plugin.MarkKey)
                : KeyLabels.Of(modifier) + " + " + KeyLabels.Of(Plugin.MarkKey);

            return "[<color=yellow><b>" + key + "</b></color>]";
        }

        // MessageHud localizes what it is handed, which is why vanilla passes raw "$msg_" tokens
        // to Message. Doing the same avoids touching Localization.instance, which is not
        // guaranteed to exist at every moment this can be called.
        private static void Message(Player player, string text)
        {
            player.Message(MessageHud.MessageType.Center, text);
        }

        /// <summary>
        /// Adds a line to every chest's hover text: the key and what it will do, or that the chest
        /// is sealed. Appended after vanilla has localized its own string, so it is plain text.
        ///
        /// This has to run after every other postfix on the method, because chest hover text is a
        /// popular thing to rewrite and not everyone appends. MyLittleUI's
        /// ChestHoverText.Container_GetHoverText_Duration rebuilds the string from scratch and
        /// assigns it -- so if it runs after us, our line is simply gone. It also caches what it
        /// built, keyed on the ZDO; going last means the cache only ever holds MyLittleUI's own
        /// text and our line is re-appended on every call, cache hit or not.
        /// </summary>
        [HarmonyPatch(typeof(Container), nameof(Container.GetHoverText))]
        internal static class ContainerGetHoverTextPatch
        {
            [HarmonyPriority(Priority.Last)]
            [HarmonyAfter("shudnal.MyLittleUI")]
            private static void Postfix(Container __instance, ref string __result)
            {
                if (!Plugin.Enabled || !Plugin.ShowHoverHint || __instance == null)
                {
                    return;
                }

                if (Exemption.IsSealedBySeidrRule(__instance))
                {
                    __result += "\n<color=#8fd3ff>Sealed while bound as a Seidr chest</color>";
                    return;
                }

                bool isSealed = Exemption.IsSealed(__instance);

                // Runs every frame while hovering, so no flash here. Without access the key would
                // only be refused, so it is not offered -- the status still shows.
                bool canToggle = HasAccess(__instance, flash: false);

                if (isSealed)
                {
                    __result += canToggle
                        ? "\n<color=#8fd3ff>Sealed</color> — " + KeyLabel() + " unseal"
                        : "\n<color=#8fd3ff>Sealed</color>";
                }
                else if (canToggle)
                {
                    __result += "\n" + KeyLabel() + " Seal from automation";
                }
            }
        }
    }
}

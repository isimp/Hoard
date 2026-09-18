using System;
using HarmonyLib;
using UnityEngine;

namespace Hoard
{
    /// <summary>
    /// Whether a given chest is sealed, and how that fact is stored.
    ///
    /// The flag lives in the container's own ZDO, which is the only store with the three
    /// properties this needs: it is per-instance (a config list can only ever name a prefab, and
    /// "this personal chest but not that one" is the entire point), it rides along in the world
    /// save, and it reaches other players on a server without any traffic of our own.
    ///
    /// A ZDO value is forgeable by any peer -- <c>ZDOMan.RPC_ZDOData</c> has no authorisation
    /// check at all -- but nothing here is a security boundary. The worst a forged packet can do
    /// is unseal a chest, which is the pre-mod behaviour.
    /// </summary>
    internal static class Exemption
    {
        /// <summary>The ZDO key. Prefixed with the plugin GUID so it cannot collide with anyone.</summary>
        internal const string ZdoKey = "isimp.Hoard.sealed";

        internal static readonly int ZdoHash = ZdoKey.GetStableHashCode();

        /// <summary>
        /// The name SeidrChest writes onto a Container's <c>m_name</c> while it is bound, and
        /// clears again on unbind. Matching on it rather than reaching into SeidrChest's private
        /// statics means no reflection, no assembly reference, and the rule un-applies itself the
        /// moment the chest is unbound.
        /// </summary>
        private const string SeidrChestName = "$piece_neobotics_sc_seidr_chest";

        // Container.m_nview is private in the stock assembly. The automation mods reach it
        // directly because they build against a publicized copy; this project does not, so it goes
        // through a Harmony field ref -- which is also what you want in a path that runs on every
        // container registration.
        //
        // Built on first use rather than in a field initializer: if a game update ever renamed the
        // field, a throwing initializer would turn every later call into a
        // TypeInitializationException instead of the quiet no-op that is wanted here.
        private static AccessTools.FieldRef<Container, ZNetView> _nviewRef;
        private static bool _nviewRefResolved;

        /// <summary>The container's ZNetView, or null if it has not been wired up yet.</summary>
        internal static ZNetView Nview(Container container)
        {
            if (container == null)
            {
                return null;
            }

            if (!_nviewRefResolved)
            {
                _nviewRefResolved = true;
                try
                {
                    _nviewRef = AccessTools.FieldRefAccess<Container, ZNetView>("m_nview");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError(
                        "Container.m_nview could not be resolved, so no chest can be read as sealed: " + e.Message);
                }
            }

            if (_nviewRef == null)
            {
                return null;
            }

            try
            {
                return _nviewRef(container);
            }
            catch
            {
                return null;
            }
        }

        internal static bool IsSealed(Container container)
        {
            if (container == null)
            {
                return false;
            }

            if (Plugin.AutoSealSeidrChest && container.m_name == SeidrChestName)
            {
                return true;
            }

            ZNetView nview = Nview(container);
            if (nview == null || !nview.IsValid())
            {
                return false;
            }

            ZDO zdo = nview.GetZDO();
            return zdo != null && zdo.GetBool(ZdoHash, false);
        }

        /// <summary>
        /// True when the seal came from the SeidrChest rule rather than from the ZDO flag, so
        /// the marking key can explain why toggling appears to do nothing.
        /// </summary>
        internal static bool IsSealedBySeidrRule(Container container)
        {
            return container != null
                   && Plugin.AutoSealSeidrChest
                   && container.m_name == SeidrChestName;
        }

        /// <summary>
        /// Writes the flag. Claims ownership first: a ZDO write by a non-owner is overwritten the
        /// next time the real owner syncs, so without this the seal would silently evaporate.
        /// Callers must not use this on a chest that is in use: see <c>Marking.InUse</c>.
        /// </summary>
        /// <returns>False if the container had no usable network view to write to.</returns>
        internal static bool SetSealed(Container container, bool isSealed)
        {
            ZNetView nview = Nview(container);
            if (nview == null || !nview.IsValid())
            {
                return false;
            }

            nview.ClaimOwnership();

            ZDO zdo = nview.GetZDO();
            if (zdo == null)
            {
                return false;
            }

            zdo.Set(ZdoHash, isSealed);
            return true;
        }
    }
}

using System;
using System.Collections.Generic;
using UnityEngine;

namespace Hoard
{
    /// <summary>
    /// What to call a key on screen: the label printed on it under the player's keyboard layout.
    ///
    /// Keys are bound as KeyCodes, which Unity names by their position on a US keyboard, so on a
    /// German keyboard KeyCode.Y is the key labelled Z. The label comes from
    /// ZInput.KeyCodeToDisplayName, the same name Valheim's own controls settings show.
    ///
    /// Only a single visible character is accepted, which covers the letters, digits and
    /// punctuation that move between layouts. Anything else, including any failure, falls back
    /// to Unity's name, so keys such as LeftShift read exactly as before.
    /// </summary>
    internal static class KeyLabels
    {
        private static readonly Dictionary<KeyCode, string> Cache = new Dictionary<KeyCode, string>();

        public static string Of(KeyCode key)
        {
            if (Cache.TryGetValue(key, out var known)) return known;

            var name = key.ToString();
            try
            {
                var shown = ZInput.KeyCodeToDisplayName(key);

                // The game answers "$KeyCode ... did not have corresponding ButtonControl" before a
                // keyboard exists. That is not an answer to remember, so it is not cached.
                if (shown == null || shown.StartsWith("$KeyCode ", StringComparison.Ordinal)) return name;

                var label = Pick(shown.Trim(), name);
                Cache[key] = label;
                return label;
            }
            catch
            {
                return name;
            }
        }

        private static string Pick(string shown, string name)
        {
            if (shown.Length != 1) return name;

            var c = shown[0];
            if (char.IsWhiteSpace(c) || char.IsControl(c)) return name;

            return char.ToUpperInvariant(c).ToString();
        }
    }
}

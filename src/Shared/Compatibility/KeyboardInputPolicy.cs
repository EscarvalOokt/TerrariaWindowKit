using System;
using System.Collections.Generic;

namespace Escarval.TerrariaWindowKit
{
    internal static class KeyboardInputPolicy
    {
        internal const string EscapeBlockPrefix = "escarval.terrariawindowkit.escape.";

        internal static bool CanReceiveKeyboard(
            IEnumerable<string> blockers,
            bool textActive,
            bool releasePending,
            bool writingText,
            bool nativeTextOwner,
            bool chatOpen)
        {
            if (textActive || releasePending || nativeTextOwner || chatOpen) return false;
            var ownEscapeBlock = false;
            foreach (string blocker in blockers)
            {
                if (blocker == null || !blocker.StartsWith(EscapeBlockPrefix, StringComparison.Ordinal))
                    return false;
                ownEscapeBlock = true;
            }

            // Core sets WritingText while our Escape lease is held. It must not
            // veto the action that acquired that lease between the two phases.
            return !writingText || ownEscapeBlock;
        }

        // Mirrors Core's higher-panel hit-test while excluding only toolkit roots that pass Escape through.
        internal static bool IsHigherPriorityPanelBlocking(
            string id,
            IList<string> panelOrder,
            IEnumerable<string> registeredPanelIds,
            IEnumerable<string> passThroughIds,
            Func<string, bool> isMouseOverPanel)
        {
            if (panelOrder == null) throw new ArgumentNullException(nameof(panelOrder));
            if (registeredPanelIds == null) throw new ArgumentNullException(nameof(registeredPanelIds));
            if (passThroughIds == null) throw new ArgumentNullException(nameof(passThroughIds));
            if (isMouseOverPanel == null) throw new ArgumentNullException(nameof(isMouseOverPanel));
            int index = panelOrder.IndexOf(id);
            if (index < 0) return false;
            var registered = new HashSet<string>(registeredPanelIds, StringComparer.Ordinal);
            var passThrough = new HashSet<string>(passThroughIds, StringComparer.Ordinal);
            for (int i = index + 1; i < panelOrder.Count; i++)
            {
                string higherId = panelOrder[i];
                if (!registered.Contains(higherId) || passThrough.Contains(higherId)) continue;
                if (isMouseOverPanel(higherId)) return true;
            }

            return false;
        }
    }
}
using System;
using System.Collections.Generic;
using System.Reflection;
using Terraria;
using Terraria.GameInput;
using TerrariaModder.Core.UI;

namespace Escarval.TerrariaWindowKit
{
    // CoreRuntime validates required Core contracts before this private metadata is used.
    internal static class CoreInputPolicy
    {
        private static readonly FieldInfo _keyBlockers = CoreCompatibilityChecks.RequireField(
            typeof(UIRenderer),
            "_keyInputBlockers",
            typeof(HashSet<string>));

        private static readonly FieldInfo _textActive = CoreCompatibilityChecks.RequireField(
            typeof(UIRenderer),
            "_textInputActive",
            typeof(bool));

        private static readonly FieldInfo _textReleasePending = CoreCompatibilityChecks.RequireField(
            typeof(UIRenderer),
            "_textInputReleasePending",
            typeof(bool));

        private static readonly FieldInfo _panelOrder = CoreCompatibilityChecks.RequireField(
            typeof(UIRenderer),
            "_panelZOrder",
            typeof(List<string>));

        private static readonly FieldInfo _panels = CoreCompatibilityChecks.RequireField(
            typeof(UIRenderer),
            "_registeredPanels",
            typeof(Dictionary<string, ValueTuple<int, int, int, int>>));

        internal static void ValidateMetadata()
        {
            _ = _keyBlockers;
            _ = _textActive;
            _ = _textReleasePending;
            _ = _panelOrder;
            _ = _panels;
        }

        internal static bool CanReceiveKeyboard(string id, string[] passThroughIds)
        {
            if (!KeyboardInputPolicy.CanReceiveKeyboard(
                    (IEnumerable<string>)_keyBlockers.GetValue(null),
                    (bool)_textActive.GetValue(null),
                    (bool)_textReleasePending.GetValue(null),
                    PlayerInput.WritingText,
                    Main.CurrentInputTextTakerOverride != null,
                    Main.drawingPlayerChat)) return false;

            // Preserve the native eligibility result unless an excluded toolkit panel caused a block.
            if (!UIRenderer.ShouldBlockForHigherPriorityPanel(id)) return true;
            if (passThroughIds.Length == 0) return false;
            var order = (List<string>)_panelOrder.GetValue(null);
            if (!order.Contains(id)) return false;
            var panels = (Dictionary<string, ValueTuple<int, int, int, int>>)_panels.GetValue(null);
            return !KeyboardInputPolicy.IsHigherPriorityPanelBlocking(
                id,
                order,
                panels.Keys,
                passThroughIds,
                UIRenderer.IsMouseOverPanel);
        }

        internal static bool IsPointerBlocked(string id, int x, int y)
        {
            var order = (List<string>)_panelOrder.GetValue(null);
            var panels = (Dictionary<string, ValueTuple<int, int, int, int>>)_panels.GetValue(null);
            int index = order.IndexOf(id);
            if (index < 0) return false;
            for (int i = index + 1; i < order.Count; i++)
            {
                if (!panels.TryGetValue(order[i], out (int, int, int, int) bounds)) continue;
                if (x >= bounds.Item1 &&
                    x < (long)bounds.Item1 + bounds.Item3 &&
                    y >= bounds.Item2 &&
                    y < (long)bounds.Item2 + bounds.Item4)
                    return true;
            }

            return false;
        }
    }
}
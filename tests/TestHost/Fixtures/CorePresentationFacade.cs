using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Terraria;
using Terraria.GameInput;

namespace TerrariaModder.Core.UI
{
    // Synthetic Core API: production renderers and backend run without graphics or the game loop.
    // Preserve the real dependency type name for the linked production sources.
    // ReSharper disable once InconsistentNaming
    internal static class UIRenderer
    {
        // Names and CLR types mirror the private metadata consumed by production CoreInputPolicy.
        // CoreInputPolicy queries this set through reflection.
        // ReSharper disable once CollectionNeverQueried.Local
        private static readonly HashSet<string> _keyInputBlockers = new HashSet<string>();
        private static readonly List<string> _panelZOrder = new List<string>();

        private static readonly Dictionary<string, ValueTuple<int, int, int, int>> _registeredPanels =
            new Dictionary<string, ValueTuple<int, int, int, int>>();

        private static readonly HashSet<string> _blockingPanels = new HashSet<string>();
        private static readonly Harmony _nativeInputHarmony = new Harmony("tests.synthetic-core.ignoremouse");
        private static bool _nativeInputPatchInstalled;

        internal static readonly List<RectangleDraw> Rectangles = new List<RectangleDraw>();
        internal static readonly List<TextDraw> Text = new List<TextDraw>();
        internal static Func<string, int> Measure = value => value.Length * 7;

        internal static Dictionary<string, ValueTuple<int, int, int, int>> RegisteredPanels => _registeredPanels;
        internal static HashSet<string> BlockingPanels => _blockingPanels;

        private static bool IsBlocking => _blockingPanels.Count > 0;

        internal static void ResetPanels()
        {
            _keyInputBlockers.Clear();
            _panelZOrder.Clear();
            _registeredPanels.Clear();
            _blockingPanels.Clear();
            _textInputActive = false;
            _textInputReleasePending = false;
            if (_nativeInputPatchInstalled)
            {
                // Backend leases dispose first. Remove only synthetic Core's own postfix,
                // preserving any other Harmony patches on the native getter.
                _nativeInputHarmony.Unpatch(NativeInputGetter(), NativeInputPostfix());
                _nativeInputPatchInstalled = false;
            }
        }

        public static void RegisterPanelDraw(string id, Action _)
        {
            if (!_panelZOrder.Contains(id)) _panelZOrder.Add(id);
        }

        public static void UnregisterPanelDraw(string id)
        {
            _panelZOrder.Remove(id);
        }

        public static void RegisterPanelBounds(string id, int x, int y, int width, int height)
        {
            _registeredPanels[id] = (x, y, width, height);
            _blockingPanels.Add(id);
            EnsureNativeInputPatch();
        }

        public static void UnregisterPanelBounds(string id)
        {
            _registeredPanels.Remove(id);
            _blockingPanels.Remove(id);
        }

        public static void BringToFront(string id)
        {
            _panelZOrder.Remove(id);
            _panelZOrder.Add(id);
        }

        public static bool IsMouseOverPanel(string id)
        {
            if (!_registeredPanels.TryGetValue(id, out ValueTuple<int, int, int, int> bounds)) return false;
            return Main.mouseX >= bounds.Item1 &&
                   Main.mouseX < (long)bounds.Item1 + bounds.Item3 &&
                   Main.mouseY >= bounds.Item2 &&
                   Main.mouseY < (long)bounds.Item2 + bounds.Item4;
        }

        public static bool ShouldBlockForHigherPriorityPanel(string id)
        {
            int index = _panelZOrder.IndexOf(id);
            if (index < 0) return false;
            for (int i = index + 1; i < _panelZOrder.Count; i++)
                if (IsMouseOverPanel(_panelZOrder[i]))
                    return true;
            return false;
        }

        public static void RegisterKeyInputBlock(string id) => _keyInputBlockers.Add(id);
        public static void UnregisterKeyInputBlock(string id) => _keyInputBlockers.Remove(id);

        private static bool IsMouseOverAnyPanel()
        {
            foreach (string id in _registeredPanels.Keys)
                if (IsMouseOverPanel(id))
                    return true;
            return false;
        }

        // Mirror the real Core target, including its lack of a NoInlining annotation.
        // Harmony generates the getter wrapper that calls it, as it does in the game.
        // ReSharper disable once InconsistentNaming
        private static void IgnoreMouseInterfacePostfix(ref bool __result)
        {
            if (!__result && IsBlocking && IsMouseOverAnyPanel()) __result = true;
        }

        private static void EnsureNativeInputPatch()
        {
            if (_nativeInputPatchInstalled) return;
            _nativeInputHarmony.Patch(NativeInputGetter(), postfix: new HarmonyMethod(NativeInputPostfix()));
            _nativeInputPatchInstalled = true;
        }

        private static MethodInfo NativeInputGetter()
        {
            PropertyInfo property = typeof(PlayerInput).GetProperty(
                                        nameof(PlayerInput.IgnoreMouseInterface),
                                        BindingFlags.Public | BindingFlags.Static) ??
                                    throw new MissingMemberException(
                                        typeof(PlayerInput).FullName,
                                        nameof(PlayerInput.IgnoreMouseInterface));
            return property.GetGetMethod() ??
                   throw new MissingMethodException(typeof(PlayerInput).FullName, "get_IgnoreMouseInterface");
        }

        private static MethodInfo NativeInputPostfix() =>
            typeof(UIRenderer).GetMethod(
                nameof(IgnoreMouseInterfacePostfix),
                BindingFlags.NonPublic | BindingFlags.Static) ??
            throw new MissingMethodException(typeof(UIRenderer).FullName, nameof(IgnoreMouseInterfacePostfix));

        internal static void Reset()
        {
            Rectangles.Clear();
            Text.Clear();
            Measure = value => value.Length * 7;
        }

        public static int MeasureText(string text) => Measure(text);

        public static void DrawRect(int x, int y, int width, int height, Color4 color) =>
            Rectangles.Add(new RectangleDraw(x, y, width, height, color, false));

        public static void DrawRectOutline(int x, int y, int width, int height, Color4 color) =>
            Rectangles.Add(new RectangleDraw(x, y, width, height, color, true));

        public static void DrawText(string text, int x, int y, Color4 color) =>
            Text.Add(new TextDraw(text, x, y, color));
#pragma warning disable CS0414 // Read by CoreInputPolicy through reflection.
        private static bool _textInputActive;
        private static bool _textInputReleasePending;
#pragma warning restore CS0414
    }

    internal sealed class Color4
    {
    }

    // Preserve the real dependency type name for the linked production renderer.
    // ReSharper disable once InconsistentNaming
    internal static class UIColors
    {
        public static readonly Color4 ItemActiveBg = new Color4();
        public static readonly Color4 ButtonHover = new Color4();
        public static readonly Color4 Button = new Color4();
        public static readonly Color4 Accent = new Color4();
        public static readonly Color4 Border = new Color4();
        public static readonly Color4 Text = new Color4();
    }

    internal sealed class RectangleDraw(int x, int y, int width, int height, Color4 color, bool outline)
    {
        internal readonly Color4 Color = color;
        internal readonly int Height = height;
        internal readonly bool Outline = outline;
        internal readonly int Width = width;
        internal readonly int X = x;
        internal readonly int Y = y;
    }

    internal sealed class TextDraw(string value, int x, int y, Color4 color)
    {
        internal readonly Color4 Color = color;
        internal readonly string Value = value;
        internal readonly int X = x;
        internal readonly int Y = y;
    }
}

namespace TerrariaModder.Core.UI.Widgets
{
    internal static class TextUtil
    {
        internal static string LastText;
        internal static int LastWidth;
        internal static string TruncatedText;

        internal static void Reset()
        {
            LastText = null;
            LastWidth = 0;
            TruncatedText = null;
        }

        public static string Truncate(string text, int maxPixelWidth)
        {
            LastText = text;
            LastWidth = maxPixelWidth;
            return TruncatedText ?? text;
        }
    }
}
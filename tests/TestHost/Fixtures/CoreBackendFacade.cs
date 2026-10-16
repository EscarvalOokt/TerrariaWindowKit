using System;
using System.Runtime.CompilerServices;

namespace Terraria
{
    internal static class Main
    {
        // These identifiers must match Terraria's API used by the linked production adapter.
        // ReSharper disable InconsistentNaming
        public static int screenWidth = 1200;
        public static int screenHeight = 900;
        public static float UIScale = 1f;
        public static int mouseX = 110;
        public static int mouseY = 110;
        public static bool ingameOptionsWindow;
        public static object CurrentInputTextTakerOverride;

        public static bool drawingPlayerChat;
        // ReSharper restore InconsistentNaming
    }
}

namespace Terraria.GameInput
{
    internal static class PlayerInput
    {
        public static int RealScreenWidth = 1200;
        public static int RealScreenHeight = 900;
        public static bool WritingText;
        internal static bool NativeIgnoreMouseInterface;

        public static bool IgnoreMouseInterface
        {
            [MethodImpl(MethodImplOptions.NoInlining)]
            get => NativeIgnoreMouseInterface;
        }
    }
}

namespace TerrariaModder.Core.UI.Widgets
{
    internal static class WidgetInput
    {
        public static bool BlockInput { get; set; }
        public static bool MouseLeftClick { get; set; }
    }
}

namespace Escarval.TerrariaWindowKit
{
    // Runtime event subscriptions/scroll patches have their own tests. These tests link the
    // production host, backend, input policy and content boundary against synthetic dependencies.
    internal static class CoreRuntime
    {
        internal static IDisposable Attach(SharedCoordinator coordinator)
        {
            if (coordinator == null) throw new ArgumentNullException(nameof(coordinator));
            return new ActionLease(() => { });
        }
    }
}
using System;
using System.Reflection;
using HarmonyLib;
using Terraria;
using Terraria.GameInput;
using TerrariaModder.Core.Events;
using TerrariaModder.Core.Input;
using TerrariaModder.Core.UI;
using TerrariaModder.Core.UI.Widgets;

namespace Escarval.TerrariaWindowKit
{
    /// <summary>Shared event/patch provider leased by compatible source-only consumers in one AppDomain.</summary>
    internal sealed class CoreRuntime : IDisposable
    {
        private const string RuntimeFamily = "escarval.terrariawindowkit.core.runtime";

        // This revision describes the toolkit's runtime behavior, not an external DLL version.
        private const string RuntimeContract = "escarval.terrariawindowkit.core.runtime/2;scroll=1;input=3";
        private readonly SharedCoordinator _coordinator;
        private bool _disposed;
        private long _frame;
        private IDisposable _scrollBoundary;

        private CoreRuntime(SharedCoordinator coordinator, MethodInfo[] targets)
        {
            _coordinator = coordinator;
            try
            {
                _scrollBoundary = HarmonyScrollBoundary.Patch(
                    targets,
                    HasScrollInput,
                    () => _coordinator.SuspendBoundsForScroll(GetUpdateMouseX(), GetUpdateMouseY()));
                FrameEvents.OnPreUpdate += OnPreUpdate;
                FrameEvents.OnPostUpdate += OnPostUpdate;
                Main.OnTickForThirdPartySoftwareOnly += OnThirdPartySoftwareTick;
                GameEvents.OnWorldUnload += OnWorldUnload;
                GameEvents.OnReturnToMenu += OnWorldUnload;
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            FrameEvents.OnPreUpdate -= OnPreUpdate;
            FrameEvents.OnPostUpdate -= OnPostUpdate;
            Main.OnTickForThirdPartySoftwareOnly -= OnThirdPartySoftwareTick;
            GameEvents.OnWorldUnload -= OnWorldUnload;
            GameEvents.OnReturnToMenu -= OnWorldUnload;
            _scrollBoundary?.Dispose();
            _scrollBoundary = null;
            _disposed = true;
        }

        internal static IDisposable Attach(SharedCoordinator coordinator)
        {
            if (coordinator == null) throw new ArgumentNullException(nameof(coordinator));
            CoreCompatibilityChecks.ValidateCoreContracts(
                typeof(UIRenderer),
                typeof(WidgetInput),
                typeof(UIRenderer).Assembly.GetName().Version);
            CoreCompatibilityChecks.ValidateHarmonyContracts(
                typeof(Harmony),
                typeof(HarmonyMethod),
                typeof(Patches),
                typeof(Harmony).Assembly.GetName().Version);
            CoreInputPolicy.ValidateMetadata();
            // Validate this consumer's targets even when another consumer already owns the runtime.
            MethodInfo[] targets = ResolveTargets();
            // Source-only copies share one runtime only when their external CLR types are identical.
            string signature = CoreCompatibilityChecks.CreateRuntimeSignature(
                RuntimeContract,
                typeof(UIRenderer),
                typeof(WidgetInput),
                typeof(FrameEvents),
                typeof(GameEvents),
                typeof(InputState),
                typeof(Harmony),
                typeof(HarmonyMethod),
                typeof(Patches),
                typeof(Main),
                typeof(PlayerInput),
                typeof(Player));
            return coordinator.AcquireRuntime(RuntimeFamily, signature, () => new CoreRuntime(coordinator, targets));
        }

        private static MethodInfo[] ResolveTargets()
        {
            return
            [
                CoreCompatibilityChecks.ResolveTarget(
                    typeof(UIRenderer),
                    "OnPreUpdateConsumeScroll",
                    typeof(void),
                    Type.EmptyTypes),
                CoreCompatibilityChecks.ResolveTarget(
                    typeof(UIRenderer),
                    "PlayerUpdateScrollBlockPrefix",
                    typeof(void),
                    [typeof(Player)]),
                CoreCompatibilityChecks.ResolveTarget(
                    typeof(UIRenderer),
                    "DoScrollingInInventoryPrefix",
                    typeof(bool),
                    Type.EmptyTypes)
            ];
        }

        private static bool HasScrollInput()
        {
            return InputState.ScrollWheelDelta != 0 ||
                   PlayerInput.ScrollWheelDelta != 0 ||
                   PlayerInput.ScrollWheelDeltaForUI != 0;
        }

        private static float UpdateScale()
        {
            float scale = Main.UIScale;
            return scale > 0f && !float.IsNaN(scale) && !float.IsInfinity(scale) ? scale : 1f;
        }

        private static int GetUpdateMouseX() => (int)(PlayerInput.MouseX / UpdateScale());
        private static int GetUpdateMouseY() => (int)(PlayerInput.MouseY / UpdateScale());

        private void OnPreUpdate()
        {
            _frame = _coordinator.NextFrame();
            _coordinator.BeginFrame(
                _frame,
                InputState.IsKeyDown(KeyCode.Escape),
                InputState.IsKeyJustPressed(KeyCode.Escape),
                Main.ingameOptionsWindow);
        }

        private void OnThirdPartySoftwareTick() => _coordinator.CompleteFrame(_frame);

        private void OnPostUpdate()
        {
            if (Main.ingameOptionsWindow)
                _coordinator.CloseAll((int)CloseReason.GameMenu);
        }

        private void OnWorldUnload() => _coordinator.CloseAll((int)CloseReason.WorldUnload);
    }
}
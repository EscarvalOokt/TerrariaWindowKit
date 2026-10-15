using System;
using System.Collections.Generic;
using Terraria;
using Terraria.GameInput;
using TerrariaModder.Core.UI;
using TerrariaModder.Core.UI.Widgets;

namespace Escarval.TerrariaWindowKit
{
    /// <summary>
    /// Adapter for TerrariaModder Core with structural checks of required Core and Harmony contracts.
    /// </summary>
    /// <remarks>
    /// Create client-side after content is ready, on the owning UI thread. One backend may
    /// serve multiple hosts; dispose all those hosts first. Each backend owns one runtime lease.
    /// Compatible consumers in the same AppDomain share the runtime installation.
    /// Structural checks do not establish runtime behavior for untested configurations.
    /// </remarks>
    internal sealed class CoreWindowBackend : IWindowBackend, IWindowUpdateInput, IWindowKeyboardInput, IDisposable
    {
        private readonly HashSet<string> _boundsIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly SharedCoordinator _coordinator;
        private readonly HashSet<string> _drawIds = new HashSet<string>(StringComparer.Ordinal);
        private readonly IDisposable _runtimeLease;
        private bool _disposed;

        /// <summary>Acquires a compatible shared Core runtime lease through a connected coordinator.</summary>
        /// <exception cref="ArgumentNullException">The coordinator is null.</exception>
        /// <exception cref="NotSupportedException">A required public Core/Harmony contract or shared runtime signature is incompatible.</exception>
        /// <exception cref="InvalidOperationException">The UI thread differs, runtime installation is reentrant, or previous runtime cleanup is incomplete.</exception>
        /// <exception cref="MissingMethodException">A required private Core scroll-patch target is missing or has an incompatible signature.</exception>
        /// <exception cref="TypeInitializationException">Required private Core metadata could not be initialized; <c>InnerException</c> identifies the underlying failure.</exception>
        internal CoreWindowBackend(SharedCoordinator coordinator)
        {
            _coordinator = coordinator ?? throw new ArgumentNullException(nameof(coordinator));
            _runtimeLease = CoreRuntime.Attach(coordinator);
        }

        /// <summary>Removes remaining owned registrations and releases the runtime lease; repeated successful calls do nothing.</summary>
        /// <remarks>If runtime cleanup fails, a later Dispose may retry. Hosts must already be disposed.</remarks>
        public void Dispose()
        {
            if (_disposed) return;
            foreach (string id in new List<string>(_boundsIds)) UnregisterBounds(id);
            foreach (string id in new List<string>(_drawIds)) UnregisterDraw(id);
            _runtimeLease.Dispose();
            _disposed = true;
        }

        /// <summary>UI width derived from original screen pixels and validated UI scale.</summary>
        public int ViewportWidth =>
            UiViewport.GetDimension(PlayerInput.RealScreenWidth, Main.screenWidth, Main.UIScale);

        /// <summary>UI height derived from original screen pixels and validated UI scale.</summary>
        public int ViewportHeight =>
            UiViewport.GetDimension(PlayerInput.RealScreenHeight, Main.screenHeight, Main.UIScale);

        /// <summary>Reports the in-game options menu; inventory and ordinary pause are not menu closing triggers.</summary>
        public bool IsMenuOpen => Main.ingameOptionsWindow;

        /// <summary>Uses the current Core draw-phase block and higher-panel priority.</summary>
        public bool IsInputBlocked(string id)
        {
            ThrowIfDisposed();
            return WidgetInput.BlockInput || UIRenderer.ShouldBlockForHigherPriorityPanel(id);
        }

        /// <summary>Wraps draw in Core's input priority, synchronizes click activation and restores the previous block.</summary>
        public void RegisterDraw(string id, Action draw)
        {
            ThrowIfDisposed();
            if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("A panel ID is required.", nameof(id));
            if (draw == null) throw new ArgumentNullException(nameof(draw));
            UIRenderer.RegisterPanelDraw(
                id,
                () =>
                {
                    bool previous = WidgetInput.BlockInput;
                    bool blocked = previous || UIRenderer.ShouldBlockForHigherPriorityPanel(id);
                    WidgetInput.BlockInput = blocked;
                    try
                    {
                        if (!blocked && WidgetInput.MouseLeftClick && UIRenderer.IsMouseOverPanel(id))
                            _coordinator.Activate(id);
                        draw();
                    }
                    finally
                    {
                        WidgetInput.BlockInput = previous;
                    }
                });
            _drawIds.Add(id);
        }

        /// <summary>Removes an owned draw callback, if registered.</summary>
        public void UnregisterDraw(string id)
        {
            if (!_drawIds.Remove(id)) return;
            UIRenderer.UnregisterPanelDraw(id);
        }

        /// <summary>Registers or updates this backend's Core input rectangle in UI coordinates.</summary>
        public void RegisterBounds(string id, WindowBounds bounds)
        {
            ThrowIfDisposed();
            UIRenderer.RegisterPanelBounds(id, bounds.X, bounds.Y, bounds.Width, bounds.Height);
            _boundsIds.Add(id);
        }

        /// <summary>Removes an owned Core input rectangle, if registered.</summary>
        public void UnregisterBounds(string id)
        {
            if (!_boundsIds.Remove(id)) return;
            UIRenderer.UnregisterPanelBounds(id);
        }

        /// <summary>Raises the Core panel; WindowHost.Activate also updates coordinator ordering.</summary>
        public void BringToFront(string id)
        {
            ThrowIfDisposed();
            UIRenderer.BringToFront(id);
        }

        /// <summary>Acquires a uniquely named Core keyboard block owned by the shared Escape operation.</summary>
        public IDisposable AcquireEscapeBlock()
        {
            ThrowIfDisposed();
            var lease = new EscapeLease(KeyboardInputPolicy.EscapeBlockPrefix + Guid.NewGuid().ToString("N"));
            UIRenderer.RegisterKeyInputBlock(lease.Id);
            return lease;
        }

        /// <summary>Rejects foreign keyboard/text owners, Core text input/release and higher-panel priority.</summary>
        /// <remarks>
        /// Higher-panel checks ignore only toolkit roots with no Escape action; other panels retain priority.
        /// The toolkit's own Escape block does not disqualify its pending action.
        /// </remarks>
        public bool CanReceiveKeyboard(string id)
        {
            ThrowIfDisposed();
            return CoreInputPolicy.CanReceiveKeyboard(id, _coordinator.GetEscapePassThroughIds());
        }

        /// <summary>Checks higher Core panel bounds at the supplied update-phase UI coordinates.</summary>
        public bool IsInputBlockedAt(string id, int x, int y)
        {
            ThrowIfDisposed();
            // Scroll boundaries run in update coordinates; draw MouseX/Y and
            // the transient WidgetInput.BlockInput flag are not valid here.
            return CoreInputPolicy.IsPointerBlocked(id, x, y);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(CoreWindowBackend));
        }

        private sealed class EscapeLease : IDisposable
        {
            internal readonly string Id;
            private bool _disposed;

            internal EscapeLease(string id)
            {
                Id = id;
            }

            public void Dispose()
            {
                if (_disposed) return;
                UIRenderer.UnregisterKeyInputBlock(Id);
                _disposed = true;
            }
        }
    }
}
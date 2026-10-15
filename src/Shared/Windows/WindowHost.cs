using System;
using System.Collections.Generic;

namespace Escarval.TerrariaWindowKit
{
    /// <summary>Owns the lifecycle, geometry and coordinated input of one root window.</summary>
    /// <remarks>
    /// Use on the owning UI thread. Rendering, hit testing, widgets and content state belong
    /// to the consumer. Dispose this host before its backend; the host does not own the backend.
    /// </remarks>
    internal sealed class WindowHost : IDisposable
    {
        private readonly IWindowBackend _backend;
        private readonly WindowCallbacks _callbacks;
        private readonly SharedCoordinator _coordinator;
        private readonly WindowOptions _options;
        private readonly PointerOperation _pointer;
        private readonly IDisposable _registration;
        private bool _boundsRegistered;
        private bool _boundsSuspended;
        private bool _disposed;

        /// <summary>Registers a closed window and its draw callback, copying the supplied options.</summary>
        /// <param name="modId">Stable mod ID, nonempty and without a colon.</param>
        /// <param name="windowId">Stable window ID within the mod, nonempty and without a colon.</param>
        /// <param name="options">Initial geometry and explicitly enabled interactions.</param>
        /// <param name="backend">Caller-owned UI integration backend.</param>
        /// <param name="callbacks">Optional consumer callbacks, retained by reference.</param>
        /// <exception cref="ArgumentException">An ID is empty or contains a colon.</exception>
        /// <exception cref="ArgumentNullException">Options or backend is null.</exception>
        /// <exception cref="InvalidOperationException">The combined ID is already registered or the UI thread differs.</exception>
        /// <exception cref="NotSupportedException">The shared coordinator is incompatible.</exception>
        internal WindowHost(
            string modId,
            string windowId,
            WindowOptions options,
            IWindowBackend backend,
            WindowCallbacks callbacks = null)
        {
            if (string.IsNullOrWhiteSpace(modId) ||
                string.IsNullOrWhiteSpace(windowId) ||
                modId.Contains(":") ||
                windowId.Contains(":"))
                throw new ArgumentException("Nonempty mod/window IDs must not contain ':'.");
            if (options == null) throw new ArgumentNullException(nameof(options));
            Id = modId + ":" + windowId;
            _backend = backend ?? throw new ArgumentNullException(nameof(backend));
            _callbacks = callbacks ?? new WindowCallbacks();
            _options = new WindowOptions(options.InitialBounds)
            {
                MinimumWidth = options.MinimumWidth, MinimumHeight = options.MinimumHeight,
                Draggable = options.Draggable, Resizable = options.Resizable, Pinnable = options.Pinnable
            };
            _pointer = new PointerOperation(_options.MinimumWidth, _options.MinimumHeight);
            Bounds = _options.InitialBounds.Fit(
                _backend.ViewportWidth,
                _backend.ViewportHeight,
                _options.MinimumWidth,
                _options.MinimumHeight);
            _coordinator = SharedCoordinator.Connect();
            _registration = _coordinator.Register(
                new Dictionary<string, object>
                {
                    ["id"] = Id, ["isOpen"] = new Func<bool>(() => IsOpen),
                    ["canKeyboard"] = new Func<bool>(CanReceiveKeyboard),
                    ["escapeAction"] = new Func<int>(GetEscapeAction), ["escape"] = new Action<int>(HandleEscape),
                    ["escapeBlock"] = new Func<IDisposable>(_backend.AcquireEscapeBlock),
                    ["contains"] = new Func<int, int, bool>((x, y) => IsOpen && Bounds.Contains(x, y)),
                    ["inputBlocked"] = new Func<bool>(() => IsOpen && _backend.IsInputBlocked(Id)),
                    ["inputBlockedAt"] = new Func<int, int, bool>(IsWheelInputBlocked),
                    ["wheelAt"] =
                        new Func<int, int, int>((x, y) => IsOpen ? (_callbacks.WheelAt?.Invoke(x, y) ?? 0) : 0),
                    ["suspend"] = new Func<IDisposable>(SuspendBounds),
                    ["close"] = new Action<int>(reason => CloseCore((CloseReason)reason)),
                    ["hasTextFocus"] = new Func<bool>(() => IsOpen && (_callbacks.HasTextFocus?.Invoke() ?? false)),
                    ["releaseTextFocus"] = new Action(() => _callbacks.ReleaseTextFocus?.Invoke())
                });
            try
            {
                _backend.RegisterDraw(Id, Draw);
            }
            catch
            {
                _registration.Dispose();
                throw;
            }
        }

        /// <summary>Combined coordinator and backend ID in the form modId:windowId.</summary>
        internal string Id { get; }

        /// <summary>Current rectangle in UI coordinates, retained when the window closes.</summary>
        internal WindowBounds Bounds { get; private set; }

        /// <summary>Whether the root window is visible and participating in input.</summary>
        internal bool IsOpen { get; private set; }

        /// <summary>Whether root closing through Escape is disabled; menu/world closing still applies.</summary>
        internal bool IsPinned { get; private set; }

        /// <summary>Whether a drag operation is active.</summary>
        internal bool IsDragging => _pointer.IsDragging;

        /// <summary>Whether a bottom-right resize operation is active.</summary>
        internal bool IsResizing => _pointer.IsResizing;

        /// <summary>Closes the window and removes draw/coordinator registrations. Repeated calls do nothing.</summary>
        /// <remarks>
        /// All cleanup steps are attempted; failures are reported in an <see cref="AggregateException"/>.
        /// The host remains disposed after a cleanup failure, so later calls do not retry.
        /// </remarks>
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            SharedCoordinator.RunAll(
            [
                () => CloseCore(CloseReason.Disposed),
                () => _backend.UnregisterDraw(Id),
                _registration.Dispose
            ]);
        }

        /// <summary>On a closed-to-open transition, fits and activates the window, preserving bounds and resetting pin.</summary>
        /// <returns>False while the backend reports the game menu; true if open or already open.</returns>
        /// <exception cref="ObjectDisposedException">The host has been disposed.</exception>
        internal bool Open()
        {
            ThrowIfDisposed();
            if (_backend.IsMenuOpen) return false;
            if (IsOpen) return true;
            IsOpen = true;
            IsPinned = false;
            try
            {
                RefreshGeometry();
                RegisterBounds();
                Activate();
            }
            catch
            {
                CloseCore(CloseReason.User);
                throw;
            }

            return true;
        }

        /// <summary>Closes an open window or attempts to open a closed one.</summary>
        internal void Toggle()
        {
            ThrowIfDisposed();
            if (IsOpen) Close();
            else Open();
        }

        /// <summary>Closes for the user reason, cancels pointer operations and resets pin.</summary>
        /// <remarks>
        /// While undisposed, runs owned focus/popup cleanup even when already closed.
        /// Does nothing after disposal.
        /// </remarks>
        internal void Close()
        {
            if (!_disposed) CloseCore(CloseReason.User);
        }

        /// <summary>Brings an open window to the front and releases other participating windows' text focus.</summary>
        internal void Activate()
        {
            ThrowIfDisposed();
            if (!IsOpen) return;
            _coordinator.Activate(Id);
            _backend.BringToFront(Id);
        }

        /// <summary>Sets pin on an open window; clearing pin does not require Pinnable.</summary>
        /// <exception cref="InvalidOperationException">True is requested without Pinnable enabled.</exception>
        /// <exception cref="ObjectDisposedException">The host has been disposed.</exception>
        internal void SetPinned(bool pinned)
        {
            ThrowIfDisposed();
            if (pinned && !_options.Pinnable) throw new InvalidOperationException("Pin is opt-in for this window.");
            IsPinned = IsOpen && pinned;
        }

        /// <summary>Starts dragging from a consumer-tested hit in UI coordinates, when enabled and unblocked.</summary>
        internal void BeginDrag(int x, int y)
        {
            ThrowIfDisposed();
            if (!IsOpen || !_options.Draggable || _backend.IsInputBlocked(Id)) return;
            Activate();
            _pointer.BeginDrag(x, y, Bounds);
        }

        /// <summary>Starts bottom-right resizing from a consumer-tested hit in UI coordinates, when enabled and unblocked.</summary>
        internal void BeginResize(int x, int y)
        {
            ThrowIfDisposed();
            if (!IsOpen || !_options.Resizable || _backend.IsInputBlocked(Id)) return;
            Activate();
            _pointer.BeginResize(x, y, Bounds);
        }

        /// <summary>Advances a pointer operation in UI coordinates; release or blocked input cancels it.</summary>
        /// <remarks>Call every draw, including blocked draws. The backend's input block is also checked.</remarks>
        internal void UpdatePointer(int x, int y, bool down, bool blocked)
        {
            ThrowIfDisposed();
            if (!IsOpen)
            {
                _pointer.Cancel();
                return;
            }

            if (_pointer.TryUpdate(
                    x,
                    y,
                    down,
                    blocked || _backend.IsInputBlocked(Id),
                    _backend.ViewportWidth,
                    _backend.ViewportHeight,
                    out WindowBounds bounds))
                SetBounds(bounds);
        }

        /// <summary>Fits current bounds to the current viewport and configured minimum size.</summary>
        /// <remarks>Open and draw call this automatically; changed bounds notify BoundsChanged.</remarks>
        internal void RefreshGeometry()
        {
            ThrowIfDisposed();
            SetBounds(
                Bounds.Fit(
                    _backend.ViewportWidth,
                    _backend.ViewportHeight,
                    _options.MinimumWidth,
                    _options.MinimumHeight));
        }

        private void SetBounds(WindowBounds bounds)
        {
            if (Bounds.Equals(bounds)) return;
            Bounds = bounds;
            RegisterBounds();
            _callbacks.BoundsChanged?.Invoke(bounds);
        }

        private void RegisterBounds()
        {
            if (!IsOpen || _boundsSuspended) return;
            _backend.RegisterBounds(Id, Bounds);
            _boundsRegistered = true;
        }

        private bool IsWheelInputBlocked(int x, int y)
        {
            if (!IsOpen) return false;
            if (_callbacks.WheelInputBlocked != null) return _callbacks.WheelInputBlocked(x, y);
            // The optional backend implementation may be outside the shared project's analysis context.
            // ReSharper disable once SuspiciousTypeConversion.Global
            if (_backend is IWindowUpdateInput input) return input.IsInputBlockedAt(Id, x, y);
            return _backend.IsInputBlocked(Id);
        }

        private bool CanReceiveKeyboard()
        {
            if (_callbacks.CanReceiveKeyboard != null) return _callbacks.CanReceiveKeyboard();
            // The optional backend implementation may be outside the shared project's analysis context.
            // ReSharper disable once SuspiciousTypeConversion.Global
            return !(_backend is IWindowKeyboardInput input) || input.CanReceiveKeyboard(Id);
        }

        private void UnregisterBounds()
        {
            if (!_boundsRegistered) return;
            _boundsRegistered = false;
            _backend.UnregisterBounds(Id);
        }

        private IDisposable SuspendBounds()
        {
            if (!_boundsRegistered || _boundsSuspended) return new ActionLease(() => { });
            _boundsSuspended = true;
            try
            {
                UnregisterBounds();
            }
            catch
            {
                _boundsSuspended = false;
                RegisterBounds();
                throw;
            }

            return new ActionLease(() =>
            {
                _boundsSuspended = false;
                RegisterBounds();
            });
        }

        private int GetEscapeAction()
        {
            if (_callbacks.HasTextFocus?.Invoke() == true) return 1;
            if (_callbacks.HasPopup?.Invoke() == true) return 2;
            return IsPinned ? 0 : 3;
        }

        private void HandleEscape(int action)
        {
            if (action == 1) _callbacks.ReleaseTextFocus?.Invoke();
            else if (action == 2) _callbacks.ClosePopup?.Invoke();
            else if (action == 3 && !IsPinned) CloseCore(CloseReason.Escape);
        }

        private void Draw()
        {
            if (!IsOpen || _disposed) return;
            if (_backend.IsMenuOpen)
            {
                CloseCore(CloseReason.GameMenu);
                return;
            }

            RefreshGeometry();
            _callbacks.Draw?.Invoke();
        }

        private void CloseCore(CloseReason reason)
        {
            bool wasOpen = IsOpen;
            IsOpen = false;
            IsPinned = false;
            _pointer.Cancel();
            SharedCoordinator.RunAll(
            [
                UnregisterBounds,
                // Cleanup callbacks must be idempotent and release only their own state.
                // Visibility-dependent queries are already false after IsOpen is cleared.
                () => _callbacks.ReleaseTextFocus?.Invoke(),
                () => _callbacks.ClosePopup?.Invoke(),
                () =>
                {
                    if (wasOpen) _callbacks.Closed?.Invoke(reason);
                }
            ]);
        }

        private void ThrowIfDisposed()
        {
            if (_disposed) throw new ObjectDisposedException(nameof(WindowHost));
        }
    }
}
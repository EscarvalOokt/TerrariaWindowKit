using System;

namespace Escarval.TerrariaWindowKit
{
    /// <summary>Reason supplied to Closed after the window becomes invisible and its bounds are removed.</summary>
    internal enum CloseReason
    {
        /// <summary>Explicit Close or Toggle.</summary>
        User = 0,

        /// <summary>Unpinned root closed by the coordinated Escape action.</summary>
        Escape = 1,

        /// <summary>The in-game options menu opened, regardless of pin.</summary>
        GameMenu = 2,

        /// <summary>The world was unloaded or the game returned to the main menu.</summary>
        WorldUnload = 3,

        /// <summary>The host was disposed.</summary>
        Disposed = 4
    }

    /// <summary>Wheel ownership decision returned as an int by WindowCallbacks.WheelAt.</summary>
    internal enum WheelDisposition
    {
        /// <summary>Leave wheel input to the game/other UI; do not fall through to a lower participating window.</summary>
        PassThrough = 0,

        /// <summary>Own the wheel in a content region; the consumer's widget handles delivery.</summary>
        Deliver = 1,

        /// <summary>Own the wheel without content delivery, such as a popup barrier; the consumer suppresses its widgets.</summary>
        CaptureOnly = 2
    }

    // Local interfaces are intentionally never passed through the AppDomain ABI.
    /// <summary>Local UI renderer/input integration consumed by WindowHost.</summary>
    /// <remarks>Calls run on the owning UI thread. Implementations must track and release only their own registrations.</remarks>
    internal interface IWindowBackend
    {
        /// <summary>Current viewport width in UI coordinates.</summary>
        int ViewportWidth { get; }

        /// <summary>Current viewport height in UI coordinates.</summary>
        int ViewportHeight { get; }

        /// <summary>Whether opening is forbidden and all root windows must close.</summary>
        bool IsMenuOpen { get; }

        /// <summary>Whether draw-phase pointer input is blocked for this window.</summary>
        bool IsInputBlocked(string id);

        /// <summary>Registers the callback the host gates on visibility and menu state.</summary>
        void RegisterDraw(string id, Action draw);

        /// <summary>Removes this backend's draw registration; repeated removal is safe.</summary>
        void UnregisterDraw(string id);

        /// <summary>Registers or updates the open window's input rectangle in UI coordinates.</summary>
        void RegisterBounds(string id, WindowBounds bounds);

        /// <summary>Removes this backend's input rectangle; repeated removal is safe.</summary>
        void UnregisterBounds(string id);

        /// <summary>Raises the window in the renderer's ordering.</summary>
        void BringToFront(string id);

        /// <summary>Acquires a non-null, idempotent lease blocking the game's Escape handling until disposed.</summary>
        IDisposable AcquireEscapeBlock();
    }

    /// <summary>Optional point-aware input priority for update-phase wheel selection.</summary>
    internal interface IWindowUpdateInput
    {
        /// <summary>Checks the supplied UI point, without relying on draw-phase mouse coordinates.</summary>
        bool IsInputBlockedAt(string id, int x, int y);
    }

    /// <summary>Optional keyboard eligibility, checked before scheduling and completing an Escape action.</summary>
    internal interface IWindowKeyboardInput
    {
        /// <summary>Whether the window may receive Escape without interfering with another input owner.</summary>
        bool CanReceiveKeyboard(string id);
    }

    /// <summary>Optional consumer behavior retained by WindowHost; unset callbacks are ignored.</summary>
    /// <remarks>Queries must not mutate registrations. Cleanup must be idempotent and release only owned state.</remarks>
    // Callback setters are used by independent source-only consumers outside this solution.
    // ReSharper disable UnusedAutoPropertyAccessor.Global
    internal sealed class WindowCallbacks
    {
        /// <summary>Renders the open window after geometry is fitted. Consumer performs pointer hit testing here.</summary>
        internal Action Draw { get; set; }

        /// <summary>Notifies when geometry changes, after updating any active input bounds registration.</summary>
        /// <remarks>Input bounds may be absent while the window is closed or temporarily suspended.</remarks>
        internal Action<WindowBounds> BoundsChanged { get; set; }

        /// <summary>Reports this content's text focus, the first Escape action if keyboard eligibility allows it.</summary>
        internal Func<bool> HasTextFocus { get; set; }

        /// <summary>Releases owned text focus on Escape, focus transfer and every close, even when already closed.</summary>
        internal Action ReleaseTextFocus { get; set; }

        /// <summary>Reports this content's popup, the second Escape action after text focus.</summary>
        internal Func<bool> HasPopup { get; set; }

        /// <summary>Closes owned popup state on Escape and every close, even when already closed.</summary>
        internal Action ClosePopup { get; set; }

        /// <summary>Overrides the optional backend keyboard policy entirely; null uses IWindowKeyboardInput or true.</summary>
        internal Func<bool> CanReceiveKeyboard { get; set; }

        /// <summary>Pure UI-point query: 0 passes through, 1 owns content delivery, 2 captures without delivery.</summary>
        /// <remarks>Null means pass-through. Keep content ownership at scroll limits and without overflow.</remarks>
        internal Func<int, int, int> WheelAt { get; set; }

        /// <summary>Overrides update-point priority entirely; null uses IWindowUpdateInput, then IsInputBlocked.</summary>
        internal Func<int, int, bool> WheelInputBlocked { get; set; }

        /// <summary>Runs once per open-to-closed transition, after state reset and attempted bounds/content cleanup.</summary>
        internal Action<CloseReason> Closed { get; set; }
    }

    // ReSharper restore UnusedAutoPropertyAccessor.Global

    internal sealed class ActionLease : IDisposable
    {
        private readonly object _gate = new object();
        private Action _release;
        private bool _releasing;

        internal ActionLease(Action release)
        {
            _release = release ?? throw new ArgumentNullException(nameof(release));
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_release == null || _releasing) return;
                _releasing = true;
                try
                {
                    _release();
                    _release = null;
                }
                finally
                {
                    _releasing = false;
                }
            }
        }
    }
}
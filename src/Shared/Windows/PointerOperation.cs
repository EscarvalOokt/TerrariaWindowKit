using System;

namespace Escarval.TerrariaWindowKit
{
    /// <summary>Mutually exclusive drag and bottom-right resize operations.</summary>
    internal sealed class PointerOperation
    {
        private readonly int _minimumHeight;
        private readonly int _minimumWidth;
        private Operation _operation;
        private WindowBounds _startBounds;
        private int _startPointerX;
        private int _startPointerY;

        public PointerOperation(int minimumWidth = 1, int minimumHeight = 1)
        {
            if (minimumWidth <= 0)
                throw new ArgumentOutOfRangeException(nameof(minimumWidth));
            if (minimumHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(minimumHeight));
            _minimumWidth = minimumWidth;
            _minimumHeight = minimumHeight;
        }

        public bool IsDragging => _operation == Operation.Drag;
        public bool IsResizing => _operation == Operation.Resize;

        public void BeginDrag(int pointerX, int pointerY, WindowBounds bounds)
        {
            Begin(Operation.Drag, pointerX, pointerY, bounds);
        }

        public void BeginResize(int pointerX, int pointerY, WindowBounds bounds)
        {
            Begin(Operation.Resize, pointerX, pointerY, bounds);
        }

        public bool TryUpdate(
            int pointerX,
            int pointerY,
            bool pointerDown,
            bool blocked,
            int viewportWidth,
            int viewportHeight,
            out WindowBounds bounds)
        {
            bounds = _startBounds;
            if (_operation == Operation.None)
                return false;

            if (!pointerDown || blocked)
            {
                Cancel();
                return false;
            }

            viewportWidth = Math.Max(1, viewportWidth);
            viewportHeight = Math.Max(1, viewportHeight);
            WindowBounds fitted = _startBounds.Fit(viewportWidth, viewportHeight, _minimumWidth, _minimumHeight);
            long deltaX = (long)pointerX - _startPointerX;
            long deltaY = (long)pointerY - _startPointerY;

            if (_operation == Operation.Drag)
            {
                int x = Clamp(_startBounds.X + deltaX, 0, viewportWidth - fitted.Width);
                int y = Clamp(_startBounds.Y + deltaY, 0, viewportHeight - fitted.Height);
                bounds = new WindowBounds(x, y, fitted.Width, fitted.Height);
            }
            else
            {
                int width = Clamp(_startBounds.Width + deltaX, Math.Min(_minimumWidth, viewportWidth), viewportWidth);
                int height = Clamp(
                    _startBounds.Height + deltaY,
                    Math.Min(_minimumHeight, viewportHeight),
                    viewportHeight);
                bounds = new WindowBounds(_startBounds.X, _startBounds.Y, width, height).Fit(
                    viewportWidth,
                    viewportHeight,
                    _minimumWidth,
                    _minimumHeight);
            }

            return true;
        }

        public void Cancel() => _operation = Operation.None;

        private void Begin(Operation operation, int pointerX, int pointerY, WindowBounds bounds)
        {
            if (bounds.Width <= 0 || bounds.Height <= 0)
                throw new ArgumentOutOfRangeException(nameof(bounds));
            _startPointerX = pointerX;
            _startPointerY = pointerY;
            _startBounds = bounds;
            _operation = operation;
        }

        private static int Clamp(long value, int minimum, int maximum)
        {
            return (int)Math.Max(minimum, Math.Min(value, maximum));
        }

        private enum Operation
        {
            None,
            Drag,
            Resize
        }
    }
}
using System;

namespace Escarval.TerrariaWindowKit
{
    /// <summary>A rectangle measured in UI coordinates.</summary>
    internal readonly struct WindowBounds : IEquatable<WindowBounds>
    {
        /// <summary>Creates a rectangle with positive size. Its origin may lie outside the viewport before Fit.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Width or height is not positive.</exception>
        public WindowBounds(int x, int y, int width, int height)
        {
            if (width <= 0)
                throw new ArgumentOutOfRangeException(nameof(width));
            if (height <= 0)
                throw new ArgumentOutOfRangeException(nameof(height));

            X = x;
            Y = y;
            Width = width;
            Height = height;
        }

        /// <summary>Left edge in UI coordinates.</summary>
        public int X { get; }

        /// <summary>Top edge in UI coordinates.</summary>
        public int Y { get; }

        /// <summary>Width in UI coordinates.</summary>
        public int Width { get; }

        /// <summary>Height in UI coordinates.</summary>
        public int Height { get; }

        /// <summary>Tests a UI point, including left/top edges and excluding right/bottom edges.</summary>
        public bool Contains(int x, int y)
        {
            return Width > 0 && Height > 0 && x >= X && y >= Y && x < (long)X + Width && y < (long)Y + Height;
        }

        /// <summary>
        /// Fits the rectangle in the viewport. A viewport smaller than the configured
        /// minimum takes precedence over that minimum. Viewport dimensions are clamped to at least 1.
        /// </summary>
        /// <exception cref="ArgumentOutOfRangeException">A minimum dimension is not positive.</exception>
        public WindowBounds Fit(int viewportWidth, int viewportHeight, int minimumWidth, int minimumHeight)
        {
            if (minimumWidth <= 0)
                throw new ArgumentOutOfRangeException(nameof(minimumWidth));
            if (minimumHeight <= 0)
                throw new ArgumentOutOfRangeException(nameof(minimumHeight));

            viewportWidth = Math.Max(1, viewportWidth);
            viewportHeight = Math.Max(1, viewportHeight);
            int width = Math.Min(viewportWidth, Math.Max(Width, minimumWidth));
            int height = Math.Min(viewportHeight, Math.Max(Height, minimumHeight));
            int x = Math.Max(0, Math.Min(X, viewportWidth - width));
            int y = Math.Max(0, Math.Min(Y, viewportHeight - height));
            return new WindowBounds(x, y, width, height);
        }

        public bool Equals(WindowBounds other)
        {
            return X == other.X && Y == other.Y && Width == other.Width && Height == other.Height;
        }

        public override bool Equals(object obj) => obj is WindowBounds other && Equals(other);

        public override int GetHashCode()
        {
            unchecked
            {
                int hash = X;
                hash = (hash * 397) ^ Y;
                hash = (hash * 397) ^ Width;
                return (hash * 397) ^ Height;
            }
        }

        public static bool operator ==(WindowBounds left, WindowBounds right) => left.Equals(right);
        public static bool operator !=(WindowBounds left, WindowBounds right) => !left.Equals(right);
    }
}
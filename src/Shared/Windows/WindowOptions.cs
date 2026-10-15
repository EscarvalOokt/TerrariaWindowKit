using System;

namespace Escarval.TerrariaWindowKit
{
    /// <summary>Initial geometry and opt-in interaction features, copied when a WindowHost is created.</summary>
    internal sealed class WindowOptions
    {
        private int _minimumHeight = 1;
        private int _minimumWidth = 1;

        /// <summary>Creates options with minimum size 1x1 and all opt-in interactions disabled.</summary>
        /// <exception cref="ArgumentOutOfRangeException">Initial width or height is not positive.</exception>
        public WindowOptions(WindowBounds initialBounds)
        {
            if (initialBounds.Width <= 0 || initialBounds.Height <= 0)
                throw new ArgumentOutOfRangeException(nameof(initialBounds));
            InitialBounds = initialBounds;
        }

        /// <summary>Initial rectangle in UI coordinates; the host fits it to its viewport.</summary>
        public WindowBounds InitialBounds { get; }

        /// <summary>Positive minimum width in UI coordinates; defaults to 1. A smaller viewport takes precedence.</summary>
        public int MinimumWidth
        {
            get => _minimumWidth;
            set
            {
                if (value <= 0)
                    throw new ArgumentOutOfRangeException(nameof(value));
                _minimumWidth = value;
            }
        }

        /// <summary>Positive minimum height in UI coordinates; defaults to 1. A smaller viewport takes precedence.</summary>
        public int MinimumHeight
        {
            get => _minimumHeight;
            set
            {
                if (value <= 0)
                    throw new ArgumentOutOfRangeException(nameof(value));
                _minimumHeight = value;
            }
        }

        /// <summary>Enables BeginDrag; defaults to false. Header hit testing belongs to the consumer.</summary>
        public bool Draggable { get; set; }

        /// <summary>Enables BeginResize; defaults to false. Grip drawing and hit testing belong to the consumer.</summary>
        public bool Resizable { get; set; }

        /// <summary>Allows pin to suppress root closing through Escape; defaults to false.</summary>
        public bool Pinnable { get; set; }
    }
}
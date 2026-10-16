using System;
using TerrariaModder.Core.UI;

namespace Escarval.TerrariaWindowKit
{
    /// <summary>Optional default resize-grip presentation for Core-backed consumers.</summary>
    /// <remarks>
    /// The caller owns grip hit testing, hover eligibility, tooltip and resize input.
    /// A consumer may draw its own presentation instead of calling this renderer.
    /// </remarks>
    internal static class CoreResizeGripRenderer
    {
        internal const int DefaultGripSize = 18;

        private const string Glyph = "◢";
        private const float GlyphScale = 0.75f;
        private const int TextHeight = 16;
        private const int GlyphPadding = 2;

        /// <summary>Draws the centered grip in a caller-provided UI-space rectangle, if it fits.</summary>
        internal static void Draw(int x, int y, int width, int height, bool hovered, bool isResizing)
        {
            if (width <= 0 || height <= 0)
                return;

            var glyphWidth = (int)Math.Ceiling(UIRenderer.MeasureText(Glyph) * GlyphScale);
            var glyphHeight = (int)Math.Ceiling(TextHeight * GlyphScale);
            if (width < glyphWidth + GlyphPadding * 2 || height < glyphHeight + GlyphPadding * 2)
                return;

            UIRenderer.DrawTextSmall(
                Glyph,
                x + (width - glyphWidth) / 2,
                y + (height - glyphHeight) / 2,
                hovered || isResizing ? UIColors.Accent : UIColors.TextDim);
        }
    }
}
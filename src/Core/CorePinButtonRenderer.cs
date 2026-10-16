using System;
using TerrariaModder.Core.UI;
using TerrariaModder.Core.UI.Widgets;

namespace Escarval.TerrariaWindowKit
{
    /// <summary>Optional default pin-button presentation for Core-backed consumers.</summary>
    /// <remarks>
    /// The caller owns button layout, hit testing, hover eligibility, tooltip and pin input.
    /// Pin state is owned by WindowHost, not by this renderer.
    /// </remarks>
    internal static class CorePinButtonRenderer
    {
        internal const int DefaultMinimumWidth = 36;

        private const int HorizontalTextPadding = 10;
        private const int TextHeight = 16;

        /// <summary>Measures a stable button width for both caller-provided labels.</summary>
        internal static int MeasureWidth(string pinText, string unpinText)
        {
            int pinWidth = Math.Max(0, UIRenderer.MeasureText(pinText ?? string.Empty));
            int unpinWidth = Math.Max(0, UIRenderer.MeasureText(unpinText ?? string.Empty));
            long desiredWidth = Math.Max(pinWidth, unpinWidth) + HorizontalTextPadding * 2L;
            return (int)Math.Min(int.MaxValue, Math.Max(DefaultMinimumWidth, desiredWidth));
        }

        /// <summary>Draws the pin button in a caller-provided UI-space rectangle.</summary>
        internal static void Draw(
            int x,
            int y,
            int width,
            int height,
            bool isPinned,
            bool hovered,
            string pinText,
            string unpinText)
        {
            if (width <= 0 || height <= 0)
                return;

            UIRenderer.DrawRect(
                x,
                y,
                width,
                height,
                isPinned ? UIColors.ItemActiveBg : hovered ? UIColors.ButtonHover : UIColors.Button);
            UIRenderer.DrawRectOutline(x, y, width, height, isPinned ? UIColors.Accent : UIColors.Border);

            if (height < TextHeight)
                return;

            string text = isPinned ? unpinText : pinText;
            string visibleText = TextUtil.Truncate(text ?? string.Empty, width);
            if (visibleText.Length == 0)
                return;

            int textWidth = UIRenderer.MeasureText(visibleText);
            // Core may retain one character even when it exceeds the requested truncation width.
            if (textWidth > width)
                return;

            UIRenderer.DrawText(
                visibleText,
                x + Math.Max(0, (width - textWidth) / 2),
                y + Math.Max(0, (height - TextHeight) / 2),
                UIColors.Text);
        }
    }
}
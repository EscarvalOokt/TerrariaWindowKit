using System;

namespace Escarval.TerrariaWindowKit
{
    internal static class UiViewport
    {
        // Terraria temporarily scales Main.screenWidth/Height while drawing UI.
        // Prefer the original pixel dimensions to keep open/draw geometry equal.
        internal static int GetDimension(int originalPixels, int currentPixels, float scale)
        {
            if (scale <= 0f || float.IsNaN(scale) || float.IsInfinity(scale)) scale = 1f;
            int pixels = originalPixels > 0 ? originalPixels : currentPixels;
            float units = pixels / scale;
            if (units >= int.MaxValue) return int.MaxValue;
            return Math.Max(1, (int)units);
        }
    }
}
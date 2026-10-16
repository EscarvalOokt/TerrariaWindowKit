using NUnit.Framework;
using TerrariaModder.Core.UI;
using TerrariaModder.Core.UI.Widgets;

namespace Escarval.TerrariaWindowKit.TestHost
{
    [TestFixture]
    [NonParallelizable]
    public sealed class CorePinButtonRendererTests
    {
        [SetUp]
        public void ResetCorePresentation()
        {
            UIRenderer.Reset();
            TextUtil.Reset();
        }

        [TestCase(1)]
        [TestCase(6)]
        public void OversizedCoreTruncationResultDoesNotEscapeTheButton(int width)
        {
            // Core's narrow-width fallback retains one character, even when it does not fit.
            TextUtil.TruncatedText = "P";

            CorePinButtonRenderer.Draw(10, 20, width, 16, false, false, "Pin", "Unpin");

            Assert.That(TextUtil.LastText, Is.EqualTo("Pin"));
            Assert.That(TextUtil.LastWidth, Is.EqualTo(width));
            Assert.That(
                UIRenderer.Rectangles,
                Has.Count.EqualTo(2),
                "The button still has its background and outline.");
            Assert.That(UIRenderer.Text, Is.Empty, "A glyph wider than the rectangle must be omitted.");
        }

        [Test]
        public void TruncationResultThatExactlyFitsIsDrawn()
        {
            TextUtil.TruncatedText = "P";

            CorePinButtonRenderer.Draw(10, 20, 7, 16, false, false, "Pin", "Unpin");

            Assert.That(UIRenderer.Text, Has.Count.EqualTo(1));
            Assert.That(UIRenderer.Text[0].Value, Is.EqualTo("P"));
            Assert.That(UIRenderer.Text[0].X, Is.EqualTo(10));
            Assert.That(UIRenderer.Text[0].Y, Is.EqualTo(20));
        }

        [TestCase(false, false)]
        [TestCase(false, true)]
        [TestCase(true, false)]
        [TestCase(true, true)]
        public void FittingLabelsUseTheCurrentPinStateAndRemainCentered(bool pinned, bool hovered)
        {
            CorePinButtonRenderer.Draw(10, 20, 48, 20, pinned, hovered, "Pin", "Unpin");

            string expectedText = pinned ? "Unpin" : "Pin";
            Assert.That(TextUtil.LastText, Is.EqualTo(expectedText));
            Assert.That(UIRenderer.Text, Has.Count.EqualTo(1));
            TextDraw draw = UIRenderer.Text[0];
            Assert.That(draw.Value, Is.EqualTo(expectedText));
            Assert.That(draw.X, Is.EqualTo(10 + (48 - expectedText.Length * 7) / 2));
            Assert.That(draw.Y, Is.EqualTo(22));
            Assert.That(draw.Color, Is.SameAs(UIColors.Text));
            Assert.That(
                UIRenderer.Rectangles[0].Color,
                Is.SameAs(pinned ? UIColors.ItemActiveBg : hovered ? UIColors.ButtonHover : UIColors.Button));
            Assert.That(UIRenderer.Rectangles[1].Color, Is.SameAs(pinned ? UIColors.Accent : UIColors.Border));
        }

        [TestCase(0, 16)]
        [TestCase(-1, 16)]
        [TestCase(48, 0)]
        [TestCase(48, -1)]
        public void NonpositiveRectanglesDoNotDraw(int width, int height)
        {
            CorePinButtonRenderer.Draw(10, 20, width, height, false, false, "Pin", "Unpin");

            Assert.That(UIRenderer.Rectangles, Is.Empty);
            Assert.That(UIRenderer.Text, Is.Empty);
            Assert.That(TextUtil.LastText, Is.Null);
        }

        [Test]
        public void ShortRectanglesKeepTheButtonWithoutText()
        {
            CorePinButtonRenderer.Draw(10, 20, 48, 15, false, false, "Pin", "Unpin");

            Assert.That(UIRenderer.Rectangles, Has.Count.EqualTo(2));
            Assert.That(UIRenderer.Text, Is.Empty);
            Assert.That(TextUtil.LastText, Is.Null);
        }

        [Test]
        public void EmptyTruncationResultDoesNotDrawText()
        {
            TextUtil.TruncatedText = string.Empty;

            CorePinButtonRenderer.Draw(10, 20, 48, 16, false, false, "Pin", "Unpin");

            Assert.That(UIRenderer.Text, Is.Empty);
        }

        [Test]
        public void WidthIncludesBothLabelsAndPadding()
        {
            Assert.That(CorePinButtonRenderer.MeasureWidth("Pin", "Unpin"), Is.EqualTo(55));
            Assert.That(CorePinButtonRenderer.MeasureWidth("Unpin", "Pin"), Is.EqualTo(55));
            Assert.That(
                CorePinButtonRenderer.MeasureWidth(null, null),
                Is.EqualTo(CorePinButtonRenderer.DefaultMinimumWidth));
        }

        [Test]
        public void WidthMeasurementDoesNotOverflowForVeryWideLabels()
        {
            UIRenderer.Measure = _ => int.MaxValue;

            Assert.That(CorePinButtonRenderer.MeasureWidth("Pin", "Unpin"), Is.EqualTo(int.MaxValue));
        }
    }
}
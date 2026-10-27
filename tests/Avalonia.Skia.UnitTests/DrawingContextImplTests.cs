using Avalonia.Media;
using Avalonia.Skia.Helpers;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests
{
    public class DrawingContextImplTests
    {
        [Fact]
        public void DrawLine_With_Zero_Thickness_Pen_Does_Not_Throw()
        {
            var target = CreateTarget();
            target.DrawLine(new Pen(Brushes.Black, 0), new Point(0, 0), new Point(10, 10));
        }

        [Fact]
        public void DrawRectangle_With_Zero_Thickness_Pen_Does_Not_Throw()
        {
            var target = CreateTarget();
            target.DrawRectangle(Brushes.Black, new Pen(Brushes.Black, 0), new RoundedRect(new Rect(0, 0, 100, 100), new CornerRadius(4)));
        }

        [Fact]
        public void PostTransform_Is_Kept_When_Transform_Is_Replaced()
        {
            using var bitmap = new SKBitmap(100, 100);
            using var canvas = new SKCanvas(bitmap);
            using var target = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
            {
                Canvas = canvas,
                PostTransform = Matrix.CreateTranslation(10, 20)
            });

            AssertMaps(canvas, new Point(0, 0), new Point(10, 20));

            target.Transform = Matrix.CreateScale(2, 2);
            AssertMaps(canvas, new Point(1, 1), new Point(12, 22));

            target.Transform = Matrix.Identity;
            AssertMaps(canvas, new Point(0, 0), new Point(10, 20));
        }

        [Fact]
        public void PostTransform_Combines_With_Fractional_Dpi_Scaling()
        {
            using var bitmap = new SKBitmap(100, 100);
            using var canvas = new SKCanvas(bitmap);
            using var target = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
            {
                Canvas = canvas,
                ScaleDrawingToDpi = true,
                Dpi = new Vector(144, 144),
                PostTransform = Matrix.CreateTranslation(10, 20)
            });

            AssertMaps(canvas, new Point(0, 0), new Point(10, 20));
            AssertMaps(canvas, new Point(2, 2), new Point(13, 23));

            target.Transform = Matrix.CreateTranslation(5, 5);
            AssertMaps(canvas, new Point(0, 0), new Point(17.5, 27.5));

            target.Transform = Matrix.Identity;
            AssertMaps(canvas, new Point(0, 0), new Point(10, 20));
        }

        private static void AssertMaps(SKCanvas canvas, Point source, Point expected)
        {
            var actual = canvas.TotalMatrix.MapPoint((float)source.X, (float)source.Y);
            Assert.Equal(expected.X, actual.X, 3);
            Assert.Equal(expected.Y, actual.Y, 3);
        }

        private static DrawingContextImpl CreateTarget()
        {
            var canvas = new SKCanvas(new SKBitmap(100, 100));
            return (DrawingContextImpl)DrawingContextHelper.WrapSkiaCanvas(canvas, new Vector(96, 96));
        }
    }
}

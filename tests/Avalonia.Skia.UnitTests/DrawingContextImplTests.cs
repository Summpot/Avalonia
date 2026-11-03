using Avalonia.Media;
using Avalonia.Platform;
using Avalonia.Skia.Helpers;
using SkiaSharp;
using Xunit;

namespace Avalonia.Skia.UnitTests
{
    public class DrawingContextImplTests
    {
        [Theory]
        [InlineData(false)]
        [InlineData(true)]
        public void Surface_Access_Restriction_Preserves_Lease_Drawing(bool restrictSurface)
        {
            using var surface = SKSurface.Create(new SKImageInfo(64, 64));
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Blue);
            canvas.ClipRect(SKRect.Create(10, 20, 30, 30));
            using var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
            {
                Surface = surface,
                SurfaceOffset = new PixelPoint(10, 20),
                DisableSurfaceAccess = restrictSurface
            });
            var matrix = canvas.TotalMatrix;
            var saveCount = canvas.SaveCount;
            using (var lease = context.GetFeature<ISkiaSharpApiLeaseFeature>()!.Lease())
            {
                Assert.Equal(matrix, lease.SkCanvas.TotalMatrix);
                if (restrictSurface)
                {
                    Assert.Null(lease.SkSurface);
                    Assert.Null(lease.SkCanvas.Surface);
                }
                else
                {
                    Assert.Same(surface, lease.SkSurface);
                    Assert.Same(surface, lease.SkCanvas.Surface);
                }
                lease.SkCanvas.Save();
                lease.SkCanvas.Translate(2, 3);
                using var paint = new SKPaint { Color = SKColors.Red };
                lease.SkCanvas.DrawRect(SKRect.Create(0, 0, 4, 5), paint);
                lease.SkCanvas.Restore();
            }
            Assert.Equal(matrix, canvas.TotalMatrix);
            Assert.Equal(saveCount, canvas.SaveCount);
            using var image = surface.Snapshot();
            using var pixels = SKBitmap.FromImage(image);
            Assert.Equal(SKColors.Red, pixels.GetPixel(12, 23));
            Assert.Equal(SKColors.Blue, pixels.GetPixel(11, 23));
            Assert.Equal(SKColors.Blue, pixels.GetPixel(16, 23));
        }

        [Fact]
        public void Restricted_Lease_Restores_Clips_And_Allows_Layer_Snapshots()
        {
            using var surface = SKSurface.Create(new SKImageInfo(32, 32));
            using var context = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
            {
                Surface = surface,
                DisableSurfaceAccess = true
            });
            var bounds = surface.Canvas.DeviceClipBounds;
            using (var lease = context.GetFeature<ISkiaSharpApiLeaseFeature>()!.Lease())
            {
                lease.SkCanvas.Save();
                lease.SkCanvas.ClipRect(SKRect.Create(1, 2, 3, 4));
            }
            Assert.Equal(bounds, surface.Canvas.DeviceClipBounds);

            using var layer = context.CreateLayer(new PixelSize(16, 16));
            using var drawing = layer.CreateDrawingContext();
            drawing.Clear(Colors.Red);
            using var layerLease = drawing.GetFeature<ISkiaSharpApiLeaseFeature>()!.Lease();
            Assert.NotNull(layerLease.SkSurface);
            using var snapshot = layerLease.SkSurface.Snapshot();
            using var pixels = SKBitmap.FromImage(snapshot);
            Assert.Equal(SKColors.Red, pixels.GetPixel(0, 0));
        }

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
        public void SurfaceOffset_Is_Kept_When_Transform_Is_Replaced()
        {
            using var bitmap = new SKBitmap(100, 100);
            using var canvas = new SKCanvas(bitmap);
            using var target = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
            {
                Canvas = canvas,
                SurfaceOffset = new PixelPoint(10, 20)
            });

            AssertMaps(canvas, new Point(0, 0), new Point(10, 20));

            target.Transform = Matrix.CreateScale(2, 2);
            AssertMaps(canvas, new Point(1, 1), new Point(12, 22));

            target.Transform = Matrix.Identity;
            AssertMaps(canvas, new Point(0, 0), new Point(10, 20));
        }

        [Fact]
        public void SurfaceOffset_Combines_With_Fractional_Dpi_Scaling()
        {
            using var bitmap = new SKBitmap(100, 100);
            using var canvas = new SKCanvas(bitmap);
            using var target = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
            {
                Canvas = canvas,
                ScaleDrawingToDpi = true,
                Dpi = new Vector(144, 144),
                SurfaceOffset = new PixelPoint(10, 20)
            });

            AssertMaps(canvas, new Point(0, 0), new Point(10, 20));
            AssertMaps(canvas, new Point(2, 2), new Point(13, 23));

            target.Transform = Matrix.CreateTranslation(5, 5);
            AssertMaps(canvas, new Point(0, 0), new Point(17.5, 27.5));

            target.Transform = Matrix.Identity;
            AssertMaps(canvas, new Point(0, 0), new Point(10, 20));
        }

        [Fact]
        public void Restoring_Clip_Does_Not_Expose_Surface_Offset_Or_Dpi_In_Transform()
        {
            using var bitmap = new SKBitmap(100, 100);
            using var canvas = new SKCanvas(bitmap);
            using var target = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
            {
                Canvas = canvas,
                SurfaceOffset = new PixelPoint(10, 20),
                ScaleDrawingToDpi = true,
                Dpi = new Vector(144, 144)
            });
            var transform = Matrix.CreateTranslation(4, 6);
            target.Transform = transform;
            target.PushClip(new Rect(0, 0, 10, 10));
            target.Transform = Matrix.CreateScale(2, 2);
            target.PopClip();

            Assert.Equal(transform, target.Transform);
            target.Transform *= Matrix.CreateTranslation(2, 2);
            AssertMaps(canvas, new Point(0, 0), new Point(19, 32));
        }

        [Theory]
        [InlineData(0, 0, 96)]
        [InlineData(10, 20, 96)]
        [InlineData(10, 20, 144)]
        public void Blit_Preserves_Surface_Offset_Without_Scaling_Layer_Pixels(int x, int y, double dpi)
        {
            using var bitmap = new SKBitmap(80, 80);
            using var canvas = new SKCanvas(bitmap);
            canvas.Clear(SKColors.Blue);
            canvas.ClipRect(SKRect.Create(x, y, 40, 40));
            using var target = new DrawingContextImpl(new DrawingContextImpl.CreateInfo
            {
                Canvas = canvas,
                SurfaceOffset = new PixelPoint(x, y),
                Dpi = new Vector(dpi, dpi),
                ScaleDrawingToDpi = true
            });
            using var layer = target.CreateLayer(new PixelSize(40, 40));
            using (var drawing = layer.CreateDrawingContext())
                drawing.Clear(Colors.Red);

            target.Transform = Matrix.CreateScale(2, 2);
            var matrix = canvas.TotalMatrix;
            layer.Blit(target);

            Assert.Equal(matrix, canvas.TotalMatrix);
            for (var row = 0; row < bitmap.Height; row++)
            for (var column = 0; column < bitmap.Width; column++)
                Assert.Equal(column >= x && column < x + 40 && row >= y && row < y + 40
                    ? SKColors.Red : SKColors.Blue, bitmap.GetPixel(column, row));
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

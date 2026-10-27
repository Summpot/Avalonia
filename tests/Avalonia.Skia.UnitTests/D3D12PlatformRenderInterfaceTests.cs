using System;
using Avalonia.Direct3D12;
using Avalonia.Platform;
using Xunit;

namespace Avalonia.Skia.UnitTests;

public class D3D12PlatformRenderInterfaceTests
{
    /// <summary>
    /// Dummy context that throws when native handles are read so we can assert the
    /// ID3D12PlatformGraphicsContext branch without calling into CreateDirect3D.
    /// </summary>
    private class RecognizingD3D12Context : ID3D12PlatformGraphicsContext
    {
        public const string Marker = "reached D3D12 backend path";

        public IntPtr Adapter => throw new InvalidOperationException(Marker);
        public IntPtr Device => throw new InvalidOperationException(Marker);
        public IntPtr CommandQueue => throw new InvalidOperationException(Marker);
        public bool IsLost => false;
        public IDisposable EnsureCurrent() => Disposable.Empty;
        public object? TryGetFeature(Type featureType) => null;
        public void Dispose() { }

        private class Disposable : IDisposable
        {
            public static readonly Disposable Empty = new();
            public void Dispose() { }
        }
    }

    private class UnsupportedGraphicsContext : IPlatformGraphicsContext
    {
        public bool IsLost => false;
        public IDisposable EnsureCurrent() => Disposable.Empty;
        public object? TryGetFeature(Type featureType) => null;
        public void Dispose() { }

        private class Disposable : IDisposable
        {
            public static readonly Disposable Empty = new();
            public void Dispose() { }
        }
    }

    [Fact]
    public void CreateBackendContext_Recognizes_ID3D12PlatformGraphicsContext()
    {
        var renderInterface = new PlatformRenderInterface();
        var dummy = new RecognizingD3D12Context();

        var ex = Assert.Throws<InvalidOperationException>(() => renderInterface.CreateBackendContext(dummy));
        Assert.Equal(RecognizingD3D12Context.Marker, ex.Message);
    }

    [Fact]
    public void CreateBackendContext_Unsupported_Type_Throws_ArgumentException()
    {
        var renderInterface = new PlatformRenderInterface();
        var ex = Assert.Throws<ArgumentException>(() => renderInterface.CreateBackendContext(new UnsupportedGraphicsContext()));
        Assert.Contains("not supported", ex.Message, StringComparison.OrdinalIgnoreCase);
    }
}

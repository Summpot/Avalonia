using System;
using Avalonia.Platform;
using Avalonia.Win32.DComposition;
using Avalonia.Win32.DirectX;
using Avalonia.Win32.WinRT;

namespace Avalonia.Win32.Direct3D12;

/// <summary>
/// Composition visual host that displays an <see cref="ICompositionSurface"/> /
/// <see cref="IDCompositionSurface"/> drawing surface (not a DXGI swapchain).
/// </summary>
internal interface ID3D12DrawingSurfaceVisualHost : IDisposable
{
    IDisposable BeginTransaction();
    D3D12DrawingSurfaceBeginDrawResult BeginDrawSurface(
        IntPtr d3d11Device, DXGI_FORMAT format, in IRenderTarget.RenderTargetSceneInfo sceneInfo);
    void EndDrawSurface();
    void DestroySurface();
}

internal readonly struct D3D12DrawingSurfaceBeginDrawResult
{
    public IntPtr D3D11Texture2D { get; init; }
    public PixelPoint Offset { get; init; }
    public PixelSize Size { get; init; }
}

using System;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;
using Avalonia.Direct3D12;
using Avalonia.OpenGL.Egl;
using Avalonia.Platform;
using Avalonia.Rendering.Composition;
using Avalonia.Win32.DComposition;
using Avalonia.Win32.DirectX;
using Avalonia.Win32.Interop;
using Avalonia.Win32.WinRT;
using Avalonia.Win32.WinRT.Composition;
using MicroCom.Runtime;

namespace Avalonia.Win32.Direct3D12;

/// <summary>
/// Presents D3D12 frames through a WinUI or DirectComposition drawing surface,
/// using the format selected for the current device generation.
/// </summary>
internal sealed class D3D12DrawingSurfaceWindowSurface : ID3D12PlatformSurface, ICompositionEffectsSurface, IDisposable
{
    private readonly D3D12PlatformGraphics _graphics;
    private readonly Func<ID3D12DrawingSurfaceVisualHost> _createHost;
    private readonly Func<BlurEffect, bool> _isBlurSupported;
    private ID3D12DrawingSurfaceVisualHost? _host;
    private IDisposable? _hostDisposable;

    private D3D12DrawingSurfaceWindowSurface(
        D3D12PlatformGraphics graphics,
        Func<ID3D12DrawingSurfaceVisualHost> createHost,
        Func<BlurEffect, bool> isBlurSupported)
    {
        _graphics = graphics;
        _createHost = createHost;
        _isBlurSupported = isBlurSupported;
    }

    public static D3D12DrawingSurfaceWindowSurface CreateWinUi(
        WinUiCompositionShared shared,
        EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo info,
        D3D12PlatformGraphics graphics)
    {
        var options = AvaloniaLocator.Current.GetService<Win32PlatformOptions>();
        return new D3D12DrawingSurfaceWindowSurface(
            graphics,
            () => new WinUiDrawingSurfaceHost(info, shared, options?.WinUICompositionBackdropCornerRadius),
            effect => effect switch
            {
                BlurEffect.None => true,
                BlurEffect.Acrylic => Win32Platform.WindowsVersion >= WinUiCompositionShared.MinAcrylicVersion,
                BlurEffect.MicaLight => Win32Platform.WindowsVersion >= WinUiCompositionShared.MinHostBackdropVersion,
                BlurEffect.MicaDark => Win32Platform.WindowsVersion >= WinUiCompositionShared.MinHostBackdropVersion,
                _ => false
            });
    }

    public static D3D12DrawingSurfaceWindowSurface CreateDComp(
        DirectCompositionShared shared,
        EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo info,
        D3D12PlatformGraphics graphics)
    {
        return new D3D12DrawingSurfaceWindowSurface(
            graphics,
            () => new DCompDrawingSurfaceHost(info, shared),
            effect => effect == BlurEffect.None);
    }

    public bool IsBlurSupported(BlurEffect effect) => _isBlurSupported(effect);

    public ID3D12PlatformSurfaceRenderTarget CreateD3D12RenderTarget(ID3D12PlatformGraphicsContext context)
    {
        if (context.Device != _graphics.DeviceHandle)
            throw new ArgumentException("Render target context must use the same D3D12 device as this surface.", nameof(context));

        _host ??= _createHost();
        if (_hostDisposable is null && _host is IDisposable disposable)
            _hostDisposable = disposable;

        return new D3D12DrawingSurfaceRenderTarget(_host, _graphics);
    }

    public void Dispose()
    {
        _hostDisposable?.Dispose();
        _hostDisposable = null;
        _host = null;
    }
}

/// <summary>
/// Composition visual host that displays an <see cref="ICompositionSurface"/> /
/// <see cref="IDCompositionSurface"/> drawing surface (not a DXGI swapchain).
/// </summary>
internal interface ID3D12DrawingSurfaceVisualHost
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

internal sealed class WinUiDrawingSurfaceHost : ID3D12DrawingSurfaceVisualHost, IDisposable
{
    private readonly WinUiCompositedWindow _window;
    private readonly ICompositor _compositor;
    private ICompositionGraphicsDevice? _compositionDevice;
    private ICompositionGraphicsDevice2? _compositionDevice2;
    private ICompositionSurface? _surface;
    private ICompositionDrawingSurfaceInterop? _surfaceInterop;
    private ICompositionDrawingSurface? _drawingSurface;
    private PixelSize _size;
    private bool _isSurfaceSupportTransparency;
    private DXGI_FORMAT _surfaceFormat;
    private IntPtr _boundD3D11Device;

    public WinUiDrawingSurfaceHost(
        EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo info,
        WinUiCompositionShared shared,
        float? backdropCornerRadius)
    {
        _window = new WinUiCompositedWindow(info, shared, backdropCornerRadius);
        _compositor = shared.Compositor.CloneReference();
    }

    public IDisposable BeginTransaction() => _window.BeginTransaction();

    private void EnsureCompositionDevice(IntPtr d3d11Device)
    {
        if (_compositionDevice is not null && _boundD3D11Device == d3d11Device)
            return;

        DestroySurface();
        _compositionDevice2?.Dispose();
        _compositionDevice?.Dispose();
        _compositionDevice2 = null;
        _compositionDevice = null;

        using var d3d11 = MicroComRuntime.CreateProxyFor<IUnknown>(d3d11Device, false);
        using var interop = _compositor.QueryInterface<ICompositorInterop>();
        _compositionDevice = interop.CreateGraphicsDevice(d3d11);
        _compositionDevice2 = _compositionDevice.QueryInterface<ICompositionGraphicsDevice2>();
        _boundD3D11Device = d3d11Device;
    }

    [MemberNotNull(nameof(_drawingSurface), nameof(_surface), nameof(_surfaceInterop))]
    private void CreateSurface(DXGI_FORMAT format, in IRenderTarget.RenderTargetSceneInfo sceneInfo)
    {
        bool isTransparency = sceneInfo.TransparencyLevel != CompositionTransparencyLevel.None;
        var surfaceSize = sceneInfo.Size;
        var alphaMode = isTransparency ? DirectXAlphaMode.Premultiplied : DirectXAlphaMode.Ignore;
        _drawingSurface = _compositionDevice2!.CreateDrawingSurface2(new UnmanagedMethods.SIZE
            {
                X = surfaceSize.Width,
                Y = surfaceSize.Height,
            },
            (DirectXPixelFormat)format, alphaMode);
        _surface = _drawingSurface.QueryInterface<ICompositionSurface>();
        _surfaceInterop = _drawingSurface.QueryInterface<ICompositionDrawingSurfaceInterop>();
        _isSurfaceSupportTransparency = isTransparency;
        _surfaceFormat = format;
        _size = surfaceSize;
    }

    public unsafe D3D12DrawingSurfaceBeginDrawResult BeginDrawSurface(
        IntPtr d3d11Device, DXGI_FORMAT format, in IRenderTarget.RenderTargetSceneInfo sceneInfo)
    {
        EnsureCompositionDevice(d3d11Device);

        bool isTransparency = sceneInfo.TransparencyLevel != CompositionTransparencyLevel.None;
        if (_surface is null || _surfaceInterop is null || _drawingSurface is null
            || _isSurfaceSupportTransparency != isTransparency || _surfaceFormat != format)
        {
            _surface?.Dispose();
            _surfaceInterop?.Dispose();
            _drawingSurface?.Dispose();
            _surface = null;
            _surfaceInterop = null;
            _drawingSurface = null;
            CreateSurface(format, in sceneInfo);
        }

        var size = sceneInfo.Size;
        _window.ResizeIfNeeded(size);
        _window.ApplyEffects(sceneInfo.TransparencyLevel,
            (sceneInfo.PlatformSpecificSceneInfo as Win32TopLevelSceneInfo)?.ThemeVariant ??
            PlatformThemeVariant.Light);
        _window.SetSurface(_surface!);

        if (_size != size)
        {
            _surfaceInterop!.Resize(new UnmanagedMethods.POINT
            {
                X = size.Width,
                Y = size.Height
            });
            _size = size;
        }

        void* pTexture;
        var iid = D3D12Interop.IID_ID3D11Texture2D;
        var off = _surfaceInterop!.BeginDraw(null, &iid, &pTexture);
        return new D3D12DrawingSurfaceBeginDrawResult
        {
            D3D11Texture2D = new IntPtr(pTexture),
            Offset = new PixelPoint(off.X, off.Y),
            Size = size
        };
    }

    public void EndDrawSurface() => _surfaceInterop?.EndDraw();

    public void DestroySurface()
    {
        _surface?.Dispose();
        _surfaceInterop?.Dispose();
        _drawingSurface?.Dispose();
        _surface = null;
        _surfaceInterop = null;
        _drawingSurface = null;
    }

    public void Dispose()
    {
        DestroySurface();
        _compositionDevice2?.Dispose();
        _compositionDevice?.Dispose();
        _compositionDevice2 = null;
        _compositionDevice = null;
        _compositor.Dispose();
        _window.Dispose();
    }
}

internal sealed class DCompDrawingSurfaceHost : ID3D12DrawingSurfaceVisualHost, IDisposable
{
    private readonly DirectCompositedWindow _window;
    private readonly DirectCompositionShared _shared;
    private IDCompositionSurface? _surface;
    private PixelSize _size;
    private bool _isSurfaceSupportTransparency;
    private DXGI_FORMAT _surfaceFormat;
    private IntPtr _boundD3D11Device;

    public DCompDrawingSurfaceHost(
        EglGlPlatformSurface.IEglWindowGlPlatformSurfaceInfo info,
        DirectCompositionShared shared)
    {
        _shared = shared;
        _window = new DirectCompositedWindow(info, shared);
    }

    public IDisposable BeginTransaction() => _window.BeginTransaction();

    [MemberNotNull(nameof(_surface))]
    private void CreateSurface(IntPtr d3d11Device, DXGI_FORMAT format, PixelSize surfaceSize, bool isTransparency)
    {
        using var d3d11 = MicroComRuntime.CreateProxyFor<IUnknown>(d3d11Device, false);
        using var surfaceFactory = _shared.Device.CreateSurfaceFactory(d3d11);

        var alphaMode = isTransparency
            ? DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_PREMULTIPLIED
            : DXGI_ALPHA_MODE.DXGI_ALPHA_MODE_IGNORE;

        // Fixed-size surface (not virtual). BeginDraw of the full rect may still return a
        // non-zero offset (DComp/WinUI); the render target draws directly into the composition
        // texture or copies an offscreen RT into that region.
        _surface = surfaceFactory.CreateSurface(
            (uint)Math.Max(1, surfaceSize.Width),
            (uint)Math.Max(1, surfaceSize.Height),
            format,
            alphaMode);
        _isSurfaceSupportTransparency = isTransparency;
        _surfaceFormat = format;
        _size = surfaceSize;
        _boundD3D11Device = d3d11Device;
    }

    public unsafe D3D12DrawingSurfaceBeginDrawResult BeginDrawSurface(
        IntPtr d3d11Device, DXGI_FORMAT format, in IRenderTarget.RenderTargetSceneInfo sceneInfo)
    {
        bool isTransparency = sceneInfo.TransparencyLevel != CompositionTransparencyLevel.None;
        var size = new PixelSize(Math.Max(1, sceneInfo.Size.Width), Math.Max(1, sceneInfo.Size.Height));

        if (_surface is null
            || _boundD3D11Device != d3d11Device
            || isTransparency != _isSurfaceSupportTransparency
            || _surfaceFormat != format
            || _size != size)
        {
            _surface?.Dispose();
            _surface = null;
            CreateSurface(d3d11Device, format, size, isTransparency);
        }

        _window.SetSurface(_surface!);

        void* pTexture;
        var rect = new UnmanagedMethods.RECT { right = size.Width, bottom = size.Height };
        var iid = D3D12Interop.IID_ID3D11Texture2D;
        var off = _surface!.BeginDraw(&rect, &iid, &pTexture);
        return new D3D12DrawingSurfaceBeginDrawResult
        {
            D3D11Texture2D = new IntPtr(pTexture),
            Offset = new PixelPoint(off.X, off.Y),
            Size = size
        };
    }

    public void EndDrawSurface() => _surface?.EndDraw();

    public void DestroySurface()
    {
        _surface?.Dispose();
        _surface = null;
        _boundD3D11Device = IntPtr.Zero;
    }

    public void Dispose()
    {
        DestroySurface();
        _window.Dispose();
    }
}

/// <summary>
/// Renders into composition textures directly, or through an offscreen copy when
/// direct rendering is unavailable for the current device generation.
/// Presentation failures invalidate this target so it can be recreated on the same
/// drawing-surface path.
/// </summary>
internal class D3D12DrawingSurfaceRenderTarget : ID3D12PlatformSurfaceRenderTarget
{
    private readonly ID3D12DrawingSurfaceVisualHost _host;
    private readonly D3D12PlatformGraphics _graphics;
    private readonly int _deviceGeneration;
    private readonly IntPtr _createdOnDevice;
    private readonly DXGI_FORMAT _format;
    private readonly bool _directRendering;
    private readonly D3D12PerBufferCommandResources _commands = new();
    private uint _commandIndex;
    private IntPtr _offscreen;
    private IntPtr _wrappedOffscreen;
    private PixelSize _offscreenSize;
    private bool _disposed;
    private bool _lost;

    public D3D12DrawingSurfaceRenderTarget(ID3D12DrawingSurfaceVisualHost host, D3D12PlatformGraphics graphics)
    {
        _host = host;
        _graphics = graphics;
        _deviceGeneration = graphics.DeviceGeneration;
        _createdOnDevice = graphics.DeviceHandle;
        _format = graphics.DrawingSurfaceFormat;
        _directRendering = graphics.DrawingSurfaceCapabilities.DirectRendering;
    }

    public PlatformRenderTargetState State
    {
        get
        {
            if (_disposed)
                return PlatformRenderTargetState.Disposed;
            if (_lost
                || _graphics.IsLost
                || _graphics.DeviceGeneration != _deviceGeneration
                || _graphics.DeviceHandle != _createdOnDevice)
                return PlatformRenderTargetState.Corrupted;
            return PlatformRenderTargetState.Ready;
        }
    }

    private void EnsureOffscreen(PixelSize size)
    {
        if (_offscreen != IntPtr.Zero && _wrappedOffscreen != IntPtr.Zero && _offscreenSize == size)
            return;

        // Drain GPU before releasing a wrap/offscreen still referenced by prior frames.
        if (_offscreen != IntPtr.Zero
            && !_graphics.IsLost
            && _graphics.DeviceGeneration == _deviceGeneration
            && _graphics.DeviceHandle == _createdOnDevice)
        {
            try { _graphics.WaitForGpu(); }
            catch (PlatformGraphicsContextLostException) { }
        }

        ReleaseOffscreen();
        var offscreen = _graphics.CreateCommittedTexture2D(
            (uint)_format,
            (uint)Math.Max(1, size.Width),
            (uint)Math.Max(1, size.Height),
            D3D12Interop.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET,
            D3D12Interop.D3D12_RESOURCE_STATE_RENDER_TARGET);
        IntPtr wrapped = IntPtr.Zero;
        try
        {
            wrapped = _graphics.CreateWrappedOffscreenTexture(offscreen);
        }
        catch
        {
            D3D12Interop.Release(offscreen);
            throw;
        }

        _offscreen = offscreen;
        _wrappedOffscreen = wrapped;
        _offscreenSize = size;
    }

    private void ReleaseOffscreen()
    {
        if (_wrappedOffscreen != IntPtr.Zero)
        {
            D3D12Interop.Release(_wrappedOffscreen);
            _wrappedOffscreen = IntPtr.Zero;
        }
        if (_offscreen != IntPtr.Zero)
        {
            D3D12Interop.Release(_offscreen);
            _offscreen = IntPtr.Zero;
        }
        _offscreenSize = default;
    }

    public ID3D12PlatformSurfaceRenderingSession BeginRendering(IRenderTarget.RenderTargetSceneInfo sceneInfo)
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(D3D12DrawingSurfaceRenderTarget));
        if (_graphics.DeviceGeneration != _deviceGeneration
            || _graphics.DeviceHandle != _createdOnDevice)
        {
            _lost = true;
            throw new PlatformGraphicsContextLostException();
        }
        if (_graphics.IsLost)
        {
            _lost = true;
            throw new PlatformGraphicsContextLostException();
        }
        if (_lost)
            throw new RenderTargetCorruptedException();

        var transaction = _host.BeginTransaction();
        var needsEndDraw = false;
        IntPtr d3d11Texture = IntPtr.Zero;
        D3D12CompositionTexture? compositionTexture = null;
        try
        {
            var d3d11Device = _graphics.D3D11DeviceHandle;
            D3D12DrawingSurfaceBeginDrawResult draw;
            try
            {
                draw = _host.BeginDrawSurface(d3d11Device, _format, in sceneInfo);
            }
            catch (Exception e)
            {
                _lost = true;
                throw new RenderTargetCorruptedException(e);
            }

            needsEndDraw = true;
            d3d11Texture = draw.D3D11Texture2D;

            try
            {
                if (_directRendering)
                    compositionTexture = new D3D12CompositionTexture(
                        _graphics, d3d11Texture, draw.Offset, draw.Size, _commands, _commandIndex);
                else
                    EnsureOffscreen(draw.Size);
            }
            catch (Exception e)
            {
                _lost = true;
                throw new RenderTargetCorruptedException(e);
            }

            var session = new D3D12DrawingSurfaceRenderingSession(
                this,
                _graphics,
                _host,
                d3d11Texture,
                _wrappedOffscreen,
                _offscreen,
                draw.Offset,
                draw.Size,
                _format,
                sceneInfo.Scaling,
                transaction,
                compositionTexture);
            compositionTexture = null;
            _commandIndex = (_commandIndex + 1) % (uint)_commands.AllocatorFenceValues.Length;
            transaction = null;
            d3d11Texture = IntPtr.Zero;
            needsEndDraw = false;
            return session;
        }
        finally
        {
            try
            {
                compositionTexture?.Dispose();
            }
            finally
            {
                if (d3d11Texture != IntPtr.Zero)
                    Marshal.Release(d3d11Texture);
                if (transaction != null)
                {
                    try
                    {
                        if (needsEndDraw)
                            _host.EndDrawSurface();
                    }
                    catch
                    {
                        // Best-effort: pair EndDraw after BeginDraw on the failure path.
                    }
                    finally
                    {
                        transaction.Dispose();
                    }
                }
            }
        }
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        try
        {
            _host.DestroySurface();
        }
        finally
        {
            try
            {
                if (!_graphics.IsLost
                    && _graphics.DeviceGeneration == _deviceGeneration
                    && _graphics.DeviceHandle == _createdOnDevice)
                {
                    _graphics.WaitForGpu();
                }
            }
            catch (PlatformGraphicsContextLostException)
            {
            }
            finally
            {
                _commands.Dispose();
                ReleaseOffscreen();
            }
        }
    }

    internal void MarkCorrupted() => _lost = true;
}

/// <summary>
/// Completes direct rendering or the offscreen copy before ending the composition update.
/// Presentation failure marks the owning target Corrupted for rebuild.
/// </summary>
internal class D3D12DrawingSurfaceRenderingSession : ID3D12PlatformSurfaceRenderingSession
{
    private readonly D3D12DrawingSurfaceRenderTarget _target;
    private readonly D3D12PlatformGraphics _graphics;
    private readonly ID3D12DrawingSurfaceVisualHost _host;
    private readonly IntPtr _d3d11Texture;
    private readonly IntPtr _wrappedOffscreen;
    private readonly IntPtr _offscreen;
    private readonly D3D12CompositionTexture? _compositionTexture;
    private readonly PixelPoint _offset;
    private readonly PixelSize _size;
    private readonly DXGI_FORMAT _format;
    private readonly double _scaling;
    private readonly IDisposable _transaction;
    private bool _disposed;

    public D3D12DrawingSurfaceRenderingSession(
        D3D12DrawingSurfaceRenderTarget target,
        D3D12PlatformGraphics graphics,
        ID3D12DrawingSurfaceVisualHost host,
        IntPtr d3d11Texture,
        IntPtr wrappedOffscreen,
        IntPtr offscreen,
        PixelPoint offset,
        PixelSize size,
        DXGI_FORMAT format,
        double scaling,
        IDisposable transaction,
        D3D12CompositionTexture? compositionTexture = null)
    {
        _target = target;
        _graphics = graphics;
        _host = host;
        _d3d11Texture = d3d11Texture;
        _wrappedOffscreen = wrappedOffscreen;
        _offscreen = offscreen;
        _offset = offset;
        _size = size;
        _format = format;
        _scaling = scaling;
        _transaction = transaction;
        _compositionTexture = compositionTexture;
    }

    public IntPtr Resource => _compositionTexture?.Resource ?? _offscreen;
    public uint ResourceState => D3D12Interop.D3D12_RESOURCE_STATE_RENDER_TARGET;
    public uint Format => (uint)_format;
    public PixelSize Size => _size;
    public PixelSize ResourceSize => _compositionTexture?.ResourceSize ?? _size;
    public PixelPoint Offset => _compositionTexture is null ? default : _offset;
    public double Scaling => _scaling;
    public bool IsYFlipped => false;

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        var failure = new D3D12PresentationFailure();
        try
        {
            if (_compositionTexture is not null)
                _compositionTexture.Dispose();
            else
                _graphics.CopyWrappedOffscreenToComposition(_wrappedOffscreen, _d3d11Texture, _offset);
        }
        catch (Exception ex)
        {
            failure.Capture(ex);
        }
        finally
        {
            // Every successful BeginDraw must be paired with EndDraw, including failed frames.
            try
            {
                _host.EndDrawSurface();
            }
            catch (Exception endEx)
            {
                failure.Capture(endEx);
            }

            try
            {
                if (_d3d11Texture != IntPtr.Zero)
                    Marshal.Release(_d3d11Texture);
            }
            catch (Exception ex)
            {
                failure.Capture(ex);
            }

            try
            {
                _transaction.Dispose();
            }
            catch (Exception txEx)
            {
                failure.Capture(txEx);
            }
        }

        if (failure.HasFailure)
            failure.Throw(_target.MarkCorrupted, _graphics.MarkLost);
    }
}

using System;
using System.Runtime.InteropServices;
using Avalonia.Direct3D12;
using Avalonia.Platform;
using Avalonia.Win32.DirectX;

namespace Avalonia.Win32.Direct3D12;

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
    private readonly DXGI_FORMAT _format;
    private readonly bool _directRendering;
    private readonly D3D12PerBufferCommandResources _commands = new();
    private uint _commandIndex;
    private IntPtr _offscreen;
    private IntPtr _wrappedOffscreen;
    private PixelSize _offscreenSize;
    private bool _disposed;
    private bool _lost;
    private bool _resourcesReleased;

    public D3D12DrawingSurfaceRenderTarget(ID3D12DrawingSurfaceVisualHost host, D3D12PlatformGraphics graphics)
    {
        _host = host;
        _graphics = graphics;
        _deviceGeneration = graphics.DeviceGeneration;
        _format = graphics.DrawingSurfaceFormat;
        _directRendering = graphics.DrawingSurfaceCapabilities.DirectRendering;
        graphics.BeforeDeviceRelease += ReleaseDeviceResources;
    }

    public PlatformRenderTargetState State
    {
        get
        {
            if (_disposed)
                return PlatformRenderTargetState.Disposed;
            if (_lost
                || _graphics.IsLost
                || _graphics.DeviceGeneration != _deviceGeneration)
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
            && _graphics.DeviceGeneration == _deviceGeneration)
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
        if (_graphics.DeviceGeneration != _deviceGeneration)
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

    private void ReleaseDeviceResources()
    {
        lock (_graphics.SyncRoot)
        {
            if (_resourcesReleased)
                return;
            _resourcesReleased = true;
            _lost = true;
            _graphics.BeforeDeviceRelease -= ReleaseDeviceResources;
            try
            {
                _host.DestroySurface();
            }
            finally
            {
                _commands.Dispose();
                ReleaseOffscreen();
            }
        }
    }

    public void Dispose()
    {
        lock (_graphics.SyncRoot)
        {
            if (_disposed)
                return;
            _disposed = true;
            try
            {
                if (!_resourcesReleased && !_graphics.IsLost && _graphics.DeviceGeneration == _deviceGeneration)
                    _graphics.WaitForGpu();
            }
            catch (PlatformGraphicsContextLostException)
            {
            }
            finally
            {
                ReleaseDeviceResources();
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
    public D3D12DxgiFormat Format => (D3D12DxgiFormat)_format;
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

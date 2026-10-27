using System;
using System.Diagnostics.Tracing;
using System.Linq;
using Avalonia.Logging;
using Avalonia.OpenGL;
using Avalonia.Platform;
using Avalonia.Vulkan;
using Avalonia.Win32.DComposition;
using Avalonia.Win32.Direct3D12;
using Avalonia.Win32.DirectX;
using Avalonia.Win32.OpenGl;
using Avalonia.Win32.OpenGl.Angle;
using Avalonia.Win32.Vulkan;
using Avalonia.Win32.WinRT.Composition;

namespace Avalonia.Win32;

static class Win32GlManager
{
    public static IPlatformGraphics? Initialize()
    {
        var gl = InitializeCore();

        if (gl is not null)
        {
            AvaloniaLocator.CurrentMutable.Bind<IPlatformGraphics>().ToConstant(gl);
        }
        if (gl is IPlatformGraphicsOpenGlContextFactory openGlFactory)
        {
            AvaloniaLocator.CurrentMutable.Bind<IPlatformGraphicsOpenGlContextFactory>().ToConstant(openGlFactory);
        }

        return gl;
    }
        
    private static IPlatformGraphics? InitializeCore()
    {
        var opts = AvaloniaLocator.Current.GetService<Win32PlatformOptions>() ?? new Win32PlatformOptions();
        return InitializeCore(opts, mode => TryInitializeMode(opts, mode));
    }

    internal static IPlatformGraphics? InitializeCore(Win32PlatformOptions opts,
        Func<Win32RenderingMode, IPlatformGraphics?> initializeMode)
    {
        if (opts.RenderingMode is null || !opts.RenderingMode.Any())
        {
            throw new InvalidOperationException($"{nameof(Win32PlatformOptions)}.{nameof(Win32PlatformOptions.RenderingMode)} must not be empty or null");
        }

        foreach (var renderingMode in opts.RenderingMode)
        {
            if (renderingMode == Win32RenderingMode.Software)
                return null;
            if (initializeMode(renderingMode) is { } graphics)
                return graphics;
        }

        throw new InvalidOperationException($"{nameof(Win32PlatformOptions)}.{nameof(Win32PlatformOptions.RenderingMode)} has a value of \"{string.Join(", ", opts.RenderingMode)}\", but no options were applied.");
    }

    private static IPlatformGraphics? TryInitializeMode(Win32PlatformOptions opts, Win32RenderingMode renderingMode)
    {
        if (renderingMode == Win32RenderingMode.AngleEgl)
        {
            var egl = AngleWin32PlatformGraphicsFactory.TryCreate(AvaloniaLocator.Current.GetService<AngleOptions>() ?? new());

            if (egl is D3D11AngleWin32PlatformGraphics)
            {
                if (!TryRegisterComposition(opts))
                {
                    throw new InvalidOperationException($"{nameof(Win32PlatformOptions)}.{nameof(Win32PlatformOptions.CompositionMode)} has a value of \"{string.Join(", ", opts.CompositionMode)}\", but no options were applied.");
                }
                return egl;
            }
        }

        if (renderingMode == Win32RenderingMode.Wgl)
        {
            if (WglPlatformOpenGlInterface.TryCreate() is { } wgl)
            {
                return wgl;
            }
        }

        if (renderingMode == Win32RenderingMode.Vulkan)
        {
            var vulkan = VulkanSupport.TryInitialize(AvaloniaLocator.Current.GetService<VulkanOptions>() ?? new());
            if (vulkan != null)
                return vulkan;
        }

        if (renderingMode == Win32RenderingMode.Direct3D12)
            return TryInitializeDirect3D12(opts);

        return null;
    }

    // A composition connection is published only after the drawing surface probe succeeds,
    // so a failed attempt leaves no surface factory or render loop for the next rendering mode.
    private static D3D12PlatformGraphics? TryInitializeDirect3D12(Win32PlatformOptions opts)
    {
        var d3d12 = D3D12PlatformGraphics.TryCreate();
        if (d3d12 is null)
            return null;

        IWindowsCompositionConnection? composition = null;
        var succeeded = false;
        try
        {
            if (!TryCreateComposition(opts, forDirect3D12: true, out composition))
            {
                Logger.TryGet(LogEventLevel.Warning, "Direct3D12")
                    ?.Log(null, "None of the configured composition modes ({0}) can be used with Direct3D 12, falling back to the next rendering mode.",
                        string.Join(", ", opts.CompositionMode));
                return null;
            }

            try
            {
                InitializeComposition(composition,
                    () =>
                    {
                        if (composition is ID3D12DrawingSurfaceFactory drawingSurfaceFactory
                            && opts.UseCompositionSwapChain != true)
                            d3d12.InitializeDrawingSurface(drawingSurfaceFactory.ProbeDrawingSurface);
                    },
                    () => AvaloniaLocator.CurrentMutable.Bind<IWindowsSurfaceFactory>().ToConstant(d3d12));
            }
            catch (Exception e)
            {
                Logger.TryGet(LogEventLevel.Warning, "Direct3D12")
                    ?.Log(null, "Failed to initialize Direct3D 12 presentation, falling back to next rendering mode: {0}", e);
                return null;
            }

            succeeded = true;
            return d3d12;
        }
        finally
        {
            if (!succeeded)
            {
                d3d12.Dispose();
            }
        }
    }

    internal static void InitializeComposition(IWindowsCompositionConnection? composition,
        Action initialize, Action publishWithoutComposition)
    {
        try
        {
            initialize();
            if (composition is not null)
                composition.Publish();
            else
                publishWithoutComposition();
        }
        catch
        {
            try
            {
                composition?.Stop();
            }
            catch (Exception e)
            {
                Logger.TryGet(LogEventLevel.Warning, "Direct3D12")
                    ?.Log(null, "Failed to release an unpublished composition connection: {0}", e);
            }
            throw;
        }
    }

    private static bool TryRegisterComposition(Win32PlatformOptions opts) =>
        TryCreateComposition(opts, forDirect3D12: false, out _);

    /// <summary>
    /// Creates a composition connection for the first applicable configured mode.
    /// Empty/null <see cref="Win32PlatformOptions.CompositionMode"/> always throws.
    /// Returns false when no mode applies. <see cref="Win32CompositionMode.RedirectionSurface"/>
    /// returns true with a null <paramref name="connection"/>.
    /// Direct3D 12 leaves the connection unpublished for the caller; otherwise it is published here.
    /// <see cref="Win32CompositionMode.LowLatencyDxgiSwapChain"/> is only available to ANGLE.
    /// </summary>
    internal static bool TryCreateComposition(Win32PlatformOptions opts, bool forDirect3D12,
        out IWindowsCompositionConnection? connection)
    {
        connection = null;
        if (opts.CompositionMode is null || !opts.CompositionMode.Any())
        {
            throw new InvalidOperationException($"{nameof(Win32PlatformOptions)}.{nameof(Win32PlatformOptions.CompositionMode)} must not be empty or null");
        }

        foreach (var compositionMode in opts.CompositionMode)
        {
            if (compositionMode == Win32CompositionMode.RedirectionSurface)
            {
                return true;
            }

            IWindowsCompositionConnection? created = null;
            if (compositionMode == Win32CompositionMode.WinUIComposition
                && WinUiCompositorConnection.IsSupported())
            {
                created = WinUiCompositorConnection.TryCreate();
            }
            else if (compositionMode == Win32CompositionMode.DirectComposition
                     && DirectCompositionConnection.IsSupported())
            {
                created = DirectCompositionConnection.TryCreate();
            }

            if (created is not null)
            {
                if (!forDirect3D12)
                    created.Publish();
                connection = created;
                return true;
            }

            if (compositionMode == Win32CompositionMode.LowLatencyDxgiSwapChain
                && !forDirect3D12
                && DxgiConnection.TryCreateAndRegister())
            {
                return true;
            }
        }

        return false;
    }
}

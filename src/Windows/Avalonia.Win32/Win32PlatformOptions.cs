using System;
using System.Collections.Generic;
using Avalonia.OpenGL;
using Avalonia.Platform;

namespace Avalonia;

/// <summary>
/// Represents the rendering mode for platform graphics.
/// </summary>
public enum Win32RenderingMode
{
    /// <summary>
    /// Avalonia is rendered into a framebuffer.
    /// </summary>
    Software = 1,

    /// <summary>
    /// Enables ANGLE EGL for Windows with GPU rendering.
    /// </summary>
    AngleEgl = 2,

    /// <summary>
    /// Avalonia would try to use native Windows OpenGL with GPU rendering.
    /// </summary>
    Wgl = 3,

    /// <summary>
    /// Avalonia would try to use native Windows Vulkan with GPU rendering.
    /// </summary>
    Vulkan = 4,

    /// <summary>
    /// Avalonia would try to use native Windows Direct3D 12 with GPU rendering.
    /// </summary>
    /// <seealso cref="Win32PlatformOptions.RenderingMode"/>
    Direct3D12 = 5
}

/// <summary>
/// Represents the DPI Awareness for the application.
/// </summary>
public enum Win32DpiAwareness
{
    /// <summary>
    /// The application is DPI unaware.
    /// </summary>
    Unaware,

    /// <summary>
    /// The application is system DPI aware. It will query DPI once and will not adjust to new DPI changes
    /// </summary>
    SystemDpiAware,

    /// <summary>
    /// The application is per-monitor DPI aware. It adjust its scale factor whenever DPI changes.
    /// </summary>
    PerMonitorDpiAware
}

/// <summary>
/// Represents the Win32 window composition mode.
/// </summary>
public enum Win32CompositionMode
{
    /// <summary>
    /// Render Avalonia to a texture inside the Windows.UI.Composition tree.
    /// </summary>
    /// <remarks>
    /// Supported on Windows 10 build 17134 and above. Ignored on other versions.
    /// This is recommended option, as it allows window acrylic effects and high refresh rate rendering.<br/>
    /// Can be applied with <see cref="Win32PlatformOptions.RenderingMode"/>=<see cref="Win32RenderingMode.AngleEgl"/>
    /// or <see cref="Win32RenderingMode.Direct3D12"/>.
    /// </remarks>
    WinUIComposition = 1,

    /// <summary>
    /// Render Avalonia to a texture inside the DirectComposition tree.
    /// </summary>
    /// <remarks>
    /// Supported on Windows 8 and above. Ignored on other versions.<br/>
    /// Can be applied with <see cref="Win32PlatformOptions.RenderingMode"/>=<see cref="Win32RenderingMode.AngleEgl"/>
    /// or <see cref="Win32RenderingMode.Direct3D12"/>.
    /// </remarks>
    DirectComposition = 2,

    /// <summary>
    /// When <see cref="LowLatencyDxgiSwapChain"/> is active, renders Avalonia through a low-latency Dxgi Swapchain.
    /// </summary>
    /// <remarks>
    /// Requires Feature Level 11_3 to be active, Windows 8.1+ Any Subversion. 
    /// This is only recommended if low input latency is desirable, and there is no need for the transparency
    /// and styling / blurring offered by <see cref="WinUIComposition"/>.<br/>
    /// Can only be applied with <see cref="Win32PlatformOptions.RenderingMode"/>=<see cref="Win32RenderingMode.AngleEgl"/>.
    /// </remarks>
    LowLatencyDxgiSwapChain = 3,

    /// <summary>
    /// Selects window presentation without a composition visual.
    /// </summary>
    /// <remarks>
    /// This option is kept only for compatibility with older systems. Some Avalonia features might not work.
    /// With <see cref="Win32RenderingMode.Direct3D12"/>, an opaque HWND swapchain presents the window
    /// without a redirection bitmap. Other rendering modes use a redirection surface.
    /// </remarks>
    RedirectionSurface,
}

/// <summary>
/// Platform-specific options which apply to Windows.
/// </summary>
public class Win32PlatformOptions
{
    /// <summary>
    /// Embeds popups to the window when set to true. The default value is false.
    /// </summary>
    public bool OverlayPopups { get; set; }

    /// <summary>
    /// Gets or sets Avalonia rendering modes with fallbacks.
    /// The first element in the array has the highest priority.
    /// The default value is: <see cref="Win32RenderingMode.AngleEgl"/>, <see cref="Win32RenderingMode.Software"/>.
    /// </summary>
    /// <remarks>
    /// If application should work on as wide range of devices as possible, at least add <see cref="Win32RenderingMode.Software"/> as a fallback value.
    /// <see cref="Win32RenderingMode.Direct3D12"/> with WinUI or DirectComposition renders directly into the composition
    /// texture when the system supports it, and otherwise renders offscreen and copies into the drawing surface.
    /// The choice is made when the graphics device is created or recreated.
    /// If neither works during initial startup, the next rendering mode is tried.
    /// A failure during device recovery leaves the graphics context lost; it does not select another rendering mode.
    /// </remarks>
    /// <exception cref="System.InvalidOperationException">Thrown if no values were matched.</exception>
    public IReadOnlyList<Win32RenderingMode> RenderingMode { get; set; } = new[]
    {
        Win32RenderingMode.AngleEgl, Win32RenderingMode.Software
    };

    /// <summary>
    /// Gets or sets Avalonia composition modes with fallbacks.
    /// The first element in the array has the highest priority.
    /// The default value is: <see cref="Win32CompositionMode.WinUIComposition"/>, <see cref="Win32CompositionMode.DirectComposition"/>, <see cref="Win32CompositionMode.RedirectionSurface"/>.
    /// </summary>
    /// <remarks>
    /// If application should work on as wide range of devices as possible, at least add <see cref="Win32CompositionMode.RedirectionSurface"/> as a fallback value.
    /// With <see cref="Win32RenderingMode.Direct3D12"/>, Acrylic and Mica require <see cref="Win32CompositionMode.WinUIComposition"/>;
    /// <see cref="Win32CompositionMode.DirectComposition"/> supports no backdrop effects.
    /// <see cref="Win32CompositionMode.RedirectionSurface"/> presents through an opaque swapchain, so the window cannot be transparent.
    /// </remarks>
    /// <exception cref="System.InvalidOperationException">Thrown if no values were matched.</exception>
    public IReadOnlyList<Win32CompositionMode> CompositionMode { get; set; } = new[]
    {
        Win32CompositionMode.WinUIComposition, Win32CompositionMode.DirectComposition, Win32CompositionMode.RedirectionSurface
    };

    /// <summary>
    /// When <see cref="CompositionMode"/> resolves to <see cref="Win32CompositionMode.WinUIComposition"/> or
    /// <see cref="Win32CompositionMode.DirectComposition"/>, render into a DXGI flip-model swapchain attached
    /// as composition visual content instead of a composition drawing surface.
    /// This allows DWM to use optimized presentation modes (independent flip) for fullscreen windows
    /// and reduces latency, at the cost of every window owning multiple full-size buffers and, on the
    /// WinUI composition path, looser visual synchronization during interactive resize.
    /// Applies to <see cref="Win32RenderingMode.AngleEgl"/> and <see cref="Win32RenderingMode.Direct3D12"/>.
    /// The default value is null, which lets the framework decide (currently: disabled).
    /// </summary>
    public bool? UseCompositionSwapChain { get; set; }

    /// <summary>
    /// When <see cref="CompositionMode"/> is set to <see cref="Win32CompositionMode.WinUIComposition"/>, create rounded corner blur brushes
    /// If set to null the brushes will be created using default settings (sharp corners)
    /// This can be useful when you need a rounded-corner blurred Windows 10 app, or borderless Windows 11 app.
    /// </summary>
    public float? WinUICompositionBackdropCornerRadius { get; set; }

    /// <summary>
    /// Render directly on the UI thread instead of using a dedicated render thread.
    /// Only applicable if <see cref="CompositionMode"/> is set to <see cref="Win32CompositionMode.RedirectionSurface"/>.
    /// This setting is only recommended for interop with systems that must render on the UI thread, such as WPF.
    /// This setting is false by default.
    /// </summary>
    public bool ShouldRenderOnUIThread { get; set; }

    /// <summary>
    /// Windows OpenGL profiles used when <see cref="RenderingMode"/> is set to <see cref="Win32RenderingMode.Wgl"/>.
    /// This setting is 4.0 and 3.2 by default.
    /// </summary>
    public IList<GlVersion> WglProfiles { get; set; } = new List<GlVersion>
    {
        new(GlProfileType.OpenGL, 4, 0), new(GlProfileType.OpenGL, 3, 2)
    };

    /// <summary>
    /// Provides a way to use a custom-implemented graphics context such as a custom ISkiaGpu.
    /// When this property set <see cref="RenderingMode"/> is ignored
    /// and <see cref="CompositionMode"/> only accepts null or <see cref="Win32CompositionMode.RedirectionSurface"/>.
    /// </summary>
    public IPlatformGraphics? CustomPlatformGraphics { get; set; }

    /// <summary>
    /// Gets or sets the application's DPI awareness.
    /// </summary>
    public Win32DpiAwareness DpiAwareness { get; set; } = Win32DpiAwareness.PerMonitorDpiAware;
    
    /// <summary>
    /// Specifies a callback called when the compositor needs to create a platform graphics device.
    /// Called for <see cref="Win32RenderingMode.AngleEgl"/> when DirectX 11 is used and for <see cref="Win32RenderingMode.Direct3D12"/>.
    /// The callback receives the available adapters, including software ones, and returns the index of the adapter to use.
    /// </summary>
    public Func<IReadOnlyList<PlatformGraphicsDeviceAdapterDescription>, int>?
        GraphicsAdapterSelectionCallback { get; set; }
}

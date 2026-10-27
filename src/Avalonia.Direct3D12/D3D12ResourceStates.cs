using Avalonia.Metadata;

namespace Avalonia.Direct3D12;

/// <summary>
/// D3D12 resource states used by platform contracts and Skia interop.
/// Values match D3D12_RESOURCE_STATES in the Windows SDK.
/// </summary>
[PrivateApi]
public static class D3D12ResourceStates
{
    public const uint Common = 0;
    public const uint Present = 0;
    public const uint RenderTarget = 0x4;
    public const uint CopySource = 0x800;
}

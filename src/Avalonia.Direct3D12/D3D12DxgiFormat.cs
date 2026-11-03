using Avalonia.Metadata;

namespace Avalonia.Direct3D12;

/// <summary>DXGI formats supported by the D3D12 platform rendering contracts.</summary>
[PrivateApi]
public enum D3D12DxgiFormat : uint
{
    Unknown = 0,
    R8G8B8A8_UNORM = 28,
    B8G8R8A8_UNORM = 87
}

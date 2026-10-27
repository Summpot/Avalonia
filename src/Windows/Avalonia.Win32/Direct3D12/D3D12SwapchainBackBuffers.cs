using System;
using Avalonia.Win32.DirectX;

namespace Avalonia.Win32.Direct3D12;

/// <summary>
/// Owned DXGI swapchain back-buffer <c>ID3D12Resource</c> pointers plus per-buffer
/// frame fence values for flip-model presentation (triple-buffered for CPU/GPU overlap).
/// </summary>
internal sealed unsafe class D3D12SwapchainBackBuffers
{
    public const uint BufferCount = 3;

    private readonly IntPtr[] _resources = new IntPtr[BufferCount];
    private readonly ulong[] _frameFenceValues = new ulong[BufferCount];

    public ulong[] FrameFenceValues => _frameFenceValues;

    public IntPtr GetResource(uint index) => _resources[index];

    public void Acquire(IDXGISwapChain1 swapChain)
    {
        for (uint i = 0; i < BufferCount; i++)
        {
            var resGuid = D3D12Interop.IID_ID3D12Resource;
            _resources[i] = (IntPtr)swapChain.GetBuffer(i, &resGuid);
        }
    }

    public void Release()
    {
        for (var i = 0; i < BufferCount; i++)
        {
            if (_resources[i] != IntPtr.Zero)
            {
                D3D12Interop.Release(_resources[i]);
                _resources[i] = IntPtr.Zero;
            }
        }
    }

    public void ClearFrameFenceValues() =>
        Array.Clear(_frameFenceValues, 0, _frameFenceValues.Length);
}

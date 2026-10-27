using System;

namespace Avalonia.Win32.Direct3D12;

/// <summary>
/// Per back-buffer command allocator/list pair — avoids a process-wide allocator bottleneck
/// when multiple windows / frames-in-flight share one D3D12 device and queue.
/// </summary>
internal sealed class D3D12PerBufferCommandResources : IDisposable
{
    private readonly IntPtr[] _commandAllocators;
    private readonly IntPtr[] _commandLists;
    private readonly ulong[] _allocatorFenceValues;
    private bool _created;

    public D3D12PerBufferCommandResources(uint bufferCount = D3D12SwapchainBackBuffers.BufferCount)
    {
        _commandAllocators = new IntPtr[bufferCount];
        _commandLists = new IntPtr[bufferCount];
        _allocatorFenceValues = new ulong[bufferCount];
    }

    public ulong[] AllocatorFenceValues => _allocatorFenceValues;

    public IntPtr GetAllocator(uint index) => _commandAllocators[index];

    public IntPtr GetCommandList(uint index) => _commandLists[index];

    public void EnsureCreated(D3D12PlatformGraphics graphics)
    {
        if (_created)
            return;
        Create(graphics);
    }

    private void Create(D3D12PlatformGraphics graphics)
    {
        try
        {
            for (var i = 0; i < _commandAllocators.Length; i++)
            {
                _commandAllocators[i] = graphics.CreateCommandAllocator();
                _commandLists[i] = graphics.CreateClosedCommandList(_commandAllocators[i]);
            }

            _created = true;
        }
        catch
        {
            Release();
            throw;
        }
    }

    public void Release()
    {
        for (var i = 0; i < _commandAllocators.Length; i++)
        {
            if (_commandLists[i] != IntPtr.Zero)
            {
                D3D12Interop.Release(_commandLists[i]);
                _commandLists[i] = IntPtr.Zero;
            }

            if (_commandAllocators[i] != IntPtr.Zero)
            {
                D3D12Interop.Release(_commandAllocators[i]);
                _commandAllocators[i] = IntPtr.Zero;
            }

            _allocatorFenceValues[i] = 0;
        }

        _created = false;
    }

    public void Dispose() => Release();
}

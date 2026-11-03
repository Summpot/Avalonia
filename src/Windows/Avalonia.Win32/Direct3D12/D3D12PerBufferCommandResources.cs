using System;
using Avalonia.Win32.DirectX;

namespace Avalonia.Win32.Direct3D12;

/// <summary>
/// Per back-buffer command allocator/list pair — avoids a process-wide allocator bottleneck
/// when multiple windows / frames-in-flight share one D3D12 device and queue.
/// </summary>
internal sealed class D3D12PerBufferCommandResources : IDisposable
{
    private readonly ID3D12CommandAllocator?[] _commandAllocators;
    private readonly ID3D12GraphicsCommandList?[] _commandLists;
    private readonly ulong[] _allocatorFenceValues;
    private bool _created;

    public D3D12PerBufferCommandResources(uint bufferCount = D3D12SwapchainBackBuffers.BufferCount)
    {
        _commandAllocators = new ID3D12CommandAllocator?[bufferCount];
        _commandLists = new ID3D12GraphicsCommandList?[bufferCount];
        _allocatorFenceValues = new ulong[bufferCount];
    }

    public ulong[] AllocatorFenceValues => _allocatorFenceValues;

    public ID3D12CommandAllocator GetAllocator(uint index) => _commandAllocators[index]!;

    public ID3D12GraphicsCommandList GetCommandList(uint index) => _commandLists[index]!;

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
                _commandLists[i] = graphics.CreateClosedCommandList(_commandAllocators[i]!);
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
            _commandLists[i]?.Dispose();
            _commandLists[i] = null;
            _commandAllocators[i]?.Dispose();
            _commandAllocators[i] = null;

            _allocatorFenceValues[i] = 0;
        }

        _created = false;
    }

    public void Dispose() => Release();
}

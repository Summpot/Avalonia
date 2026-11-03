using System;

namespace Avalonia.Win32.Direct3D12;

/// <summary>
/// Checks out a composition texture for D3D12 rendering and returns it in COMMON state.
/// The frame's allocator remains protected by its completion fence after returning the texture.
/// </summary>
internal sealed class D3D12CompositionTexture : IDisposable
{
    private readonly D3D12PlatformGraphics _graphics;
    private readonly IntPtr _texture;
    private readonly D3D12PerBufferCommandResources _commands;
    private readonly uint _index;
    private bool _renderTargetState;

    public IntPtr Resource { get; private set; }
    public PixelSize ResourceSize { get; }

    public D3D12CompositionTexture(D3D12PlatformGraphics graphics, IntPtr texture,
        PixelPoint offset, PixelSize size, D3D12PerBufferCommandResources commands, uint index)
    {
        _graphics = graphics;
        _texture = texture;
        _commands = commands;
        _index = index;
        commands.EnsureCreated(graphics);
        Resource = graphics.UnwrapCompositionTexture(texture);
        try
        {
            var description = D3D12Interop.GetResourceDescription(Resource);
            ResourceSize = new PixelSize(checked((int)description.Width), checked((int)description.Height));
            if (offset.X < 0 || offset.Y < 0 || size.Width <= 0 || size.Height <= 0
                || (long)offset.X + size.Width > ResourceSize.Width
                || (long)offset.Y + size.Height > ResourceSize.Height
                || description.SampleCount != 1
                || (description.Flags & D3D12Interop.D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET) == 0)
                throw new InvalidOperationException("The composition texture cannot contain the requested render region.");

            commands.AllocatorFenceValues[index] = graphics.ExecuteTransition(
                commands.GetAllocator(index), commands.GetCommandList(index), Resource,
                D3D12Interop.D3D12_RESOURCE_STATE_COMMON, D3D12Interop.D3D12_RESOURCE_STATE_RENDER_TARGET,
                commands.AllocatorFenceValues[index]);
            _renderTargetState = true;
        }
        catch
        {
            // The constructor's failure is the one to report. A failed return of the texture
            // already marks the device lost, so a secondary failure here is not rethrown.
            try
            {
                Dispose();
            }
            catch
            {
            }

            throw;
        }
    }

    public void Dispose()
    {
        if (Resource == IntPtr.Zero)
            return;

        try
        {
            ulong fence = 0;
            if (_renderTargetState)
            {
                fence = _graphics.ExecuteTransition(
                    _commands.GetAllocator(_index), _commands.GetCommandList(_index), Resource,
                    D3D12Interop.D3D12_RESOURCE_STATE_RENDER_TARGET, D3D12Interop.D3D12_RESOURCE_STATE_COMMON,
                    _commands.AllocatorFenceValues[_index], resetAllocator: false);
                _commands.AllocatorFenceValues[_index] = fence;
            }
            _graphics.ReturnCompositionTexture(_texture, fence);
        }
        catch
        {
            // Failed checkout completion leaves translation-layer ownership uncertain.
            // Recreate the device before scheduling further D3D11On12 work.
            _graphics.MarkLost();
            throw;
        }
        finally
        {
            D3D12Interop.Release(Resource);
            Resource = IntPtr.Zero;
        }
    }
}

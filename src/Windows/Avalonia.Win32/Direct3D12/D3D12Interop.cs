using System;
using System.Runtime.InteropServices;
using Avalonia.Win32.DirectX;

namespace Avalonia.Win32.Direct3D12;

// Hand-written COM vtable thunks for the required D3D12 / DXGI subset.
// Method slot offsets match the Microsoft Windows SDK D3D12/DXGI headers directly.
// Avalonia already binds DXGI via MicroCom; generating full MicroCom IDL for ID3D12*
// would pull a large surface area this backend does not use.

internal static unsafe class D3D12Interop
{
    public static readonly Guid IID_ID3D12Device = Guid.Parse("189819f1-1db6-4b57-be54-1821339b85f7");
    public static readonly Guid IID_ID3D12CommandQueue = Guid.Parse("0ec870a6-5d7e-4c22-8cfc-5baae07616ed");
    public static readonly Guid IID_ID3D12CommandAllocator = Guid.Parse("6102dee4-af59-4b09-b999-b44d73f09b24");
    public static readonly Guid IID_ID3D12GraphicsCommandList = Guid.Parse("5b160d0f-ac1b-4185-8ba8-b3ae42a5a455");
    public static readonly Guid IID_ID3D12Fence = Guid.Parse("0a753dcf-c4d8-4b91-adf6-be5a60d95a76");
    public static readonly Guid IID_ID3D12Resource = Guid.Parse("696442be-a72e-4059-bc79-5b5c98040fad");
    public static readonly Guid IID_IDXGISwapChain3 = Guid.Parse("94d99bdb-f1f8-4ab0-b236-7da0170edab1");

    [DllImport("d3d12.dll", ExactSpelling = true)]
    public static extern int D3D12CreateDevice(
        IntPtr pAdapter,
        D3D_FEATURE_LEVEL minimumFeatureLevel,
        in Guid riid,
        out IntPtr ppDevice);

    public const uint D3D12_COMMAND_LIST_TYPE_DIRECT = 0;
    public const uint D3D12_COMMAND_QUEUE_FLAG_NONE = 0;
    public const uint D3D12_FENCE_FLAG_NONE = 0;

    public const uint D3D12_RESOURCE_STATE_COMMON = 0;
    public const uint D3D12_RESOURCE_STATE_PRESENT = 0;
    public const uint D3D12_RESOURCE_STATE_RENDER_TARGET = 4;
    public const uint D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES = 0xffffffff;

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D12_COMMAND_QUEUE_DESC
    {
        public uint Type;
        public int Priority;
        public uint Flags;
        public uint NodeMask;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D12_RESOURCE_TRANSITION_BARRIER
    {
        public IntPtr pResource;
        public uint Subresource;
        public uint StateBefore;
        public uint StateAfter;
    }

    // Native D3D12_RESOURCE_BARRIER is Type(4)+Flags(4)+union; Explicit TRANSITION arm
    // sizes to 32 bytes via natural field alignment (no forced Size).
    [StructLayout(LayoutKind.Explicit)]
    public struct D3D12_RESOURCE_BARRIER
    {
        [FieldOffset(0)]
        public uint Type; // 0 = TRANSITION
        [FieldOffset(4)]
        public uint Flags; // 0 = NONE
        [FieldOffset(8)]
        public D3D12_RESOURCE_TRANSITION_BARRIER Transition;

        public static D3D12_RESOURCE_BARRIER CreateTransition(IntPtr pResource, uint stateBefore, uint stateAfter)
        {
            var barrier = new D3D12_RESOURCE_BARRIER();
            barrier.Type = 0;
            barrier.Flags = 0;
            barrier.Transition.pResource = pResource;
            barrier.Transition.Subresource = D3D12_RESOURCE_BARRIER_ALL_SUBRESOURCES;
            barrier.Transition.StateBefore = stateBefore;
            barrier.Transition.StateAfter = stateAfter;
            return barrier;
        }
    }

    public static int Release(IntPtr pUnk)
    {
        if (pUnk == IntPtr.Zero) return 0;
        return ((delegate* unmanaged[Stdcall]<IntPtr, int>)(*(void***)pUnk)[2])(pUnk); // IUnknown::Release
    }

    // ID3D12Device slots:
    public static int CreateCommandQueue(IntPtr device, in D3D12_COMMAND_QUEUE_DESC desc, in Guid riid, out IntPtr ppCommandQueue)
    {
        fixed (D3D12_COMMAND_QUEUE_DESC* pDesc = &desc)
        fixed (Guid* pRiid = &riid)
        fixed (IntPtr* ppQueue = &ppCommandQueue)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, void*, void*, void*, int>)
                (*(void***)device)[8])(device, pDesc, pRiid, ppQueue); // ID3D12Device::CreateCommandQueue
        }
    }

    public static int CreateCommandAllocator(IntPtr device, uint type, in Guid riid, out IntPtr ppCommandAllocator)
    {
        fixed (Guid* pRiid = &riid)
        fixed (IntPtr* ppAlloc = &ppCommandAllocator)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, void*, void*, int>)
                (*(void***)device)[9])(device, type, pRiid, ppAlloc); // ID3D12Device::CreateCommandAllocator
        }
    }

    public static int CreateCommandList(IntPtr device, uint nodeMask, uint type, IntPtr pCommandAllocator, IntPtr pInitialState, in Guid riid, out IntPtr ppCommandList)
    {
        fixed (Guid* pRiid = &riid)
        fixed (IntPtr* ppList = &ppCommandList)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr, IntPtr, void*, void*, int>)
                (*(void***)device)[12])( // ID3D12Device::CreateCommandList
                device, nodeMask, type, pCommandAllocator, pInitialState, pRiid, ppList);
        }
    }

    public static int CreateFence(IntPtr device, ulong initialValue, uint flags, in Guid riid, out IntPtr ppFence)
    {
        fixed (Guid* pRiid = &riid)
        fixed (IntPtr* ppF = &ppFence)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, ulong, uint, void*, void*, int>)
                (*(void***)device)[36])(device, initialValue, flags, pRiid, ppF); // ID3D12Device::CreateFence
        }
    }

    public static int GetDeviceRemovedReason(IntPtr device)
    {
        return ((delegate* unmanaged[Stdcall]<IntPtr, int>)(*(void***)device)[37])(device); // ID3D12Device::GetDeviceRemovedReason
    }

    // ID3D12CommandQueue slots:
    public static void ExecuteCommandLists(IntPtr commandQueue, uint numCommandLists, IntPtr* commandLists)
    {
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, void*, void>)
            (*(void***)commandQueue)[10])(commandQueue, numCommandLists, commandLists); // ID3D12CommandQueue::ExecuteCommandLists
    }

    public static int Signal(IntPtr commandQueue, IntPtr fence, ulong value)
    {
        return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, ulong, int>)
            (*(void***)commandQueue)[14])(commandQueue, fence, value); // ID3D12CommandQueue::Signal
    }

    // ID3D12Fence slots:
    public static ulong GetCompletedValue(IntPtr fence)
    {
        return ((delegate* unmanaged[Stdcall]<IntPtr, ulong>)(*(void***)fence)[8])(fence); // ID3D12Fence::GetCompletedValue
    }

    public static int SetEventOnCompletion(IntPtr fence, ulong value, IntPtr hEvent)
    {
        return ((delegate* unmanaged[Stdcall]<IntPtr, ulong, IntPtr, int>)
            (*(void***)fence)[9])(fence, value, hEvent); // ID3D12Fence::SetEventOnCompletion
    }

    // ID3D12CommandAllocator slots:
    public static int ResetCommandAllocator(IntPtr commandAllocator)
    {
        return ((delegate* unmanaged[Stdcall]<IntPtr, int>)(*(void***)commandAllocator)[8])(commandAllocator); // ID3D12CommandAllocator::Reset
    }

    // ID3D12GraphicsCommandList slots:
    public static int CloseCommandList(IntPtr commandList)
    {
        return ((delegate* unmanaged[Stdcall]<IntPtr, int>)(*(void***)commandList)[9])(commandList); // ID3D12GraphicsCommandList::Close
    }

    public static int ResetCommandList(IntPtr commandList, IntPtr commandAllocator, IntPtr initialState)
    {
        return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, int>)
            (*(void***)commandList)[10])(commandList, commandAllocator, initialState); // ID3D12GraphicsCommandList::Reset
    }

    public static void ResourceBarrier(IntPtr commandList, uint numBarriers, in D3D12_RESOURCE_BARRIER barrier)
    {
        fixed (D3D12_RESOURCE_BARRIER* pBarrier = &barrier)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint, void*, void>)
                (*(void***)commandList)[26])(commandList, numBarriers, pBarrier); // ID3D12GraphicsCommandList::ResourceBarrier
        }
    }

    // IDXGISwapChain3::GetCurrentBackBufferIndex — call only on an IDXGISwapChain3 pointer (QI first).
    public static uint GetCurrentBackBufferIndex(IntPtr swapChain3)
    {
        return ((delegate* unmanaged[Stdcall]<IntPtr, uint>)(*(void***)swapChain3)[36])(swapChain3); // IDXGISwapChain3::GetCurrentBackBufferIndex
    }

    public const uint D3D12_HEAP_TYPE_DEFAULT = 1;
    public const uint D3D12_HEAP_FLAG_NONE = 0;
    public const uint D3D12_RESOURCE_DIMENSION_TEXTURE2D = 3;
    public const uint D3D12_TEXTURE_LAYOUT_UNKNOWN = 0;
    public const uint D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET = 0x1;

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D12_HEAP_PROPERTIES
    {
        public uint Type;
        public uint CPUPageProperty;
        public uint MemoryPoolPreference;
        public uint CreationNodeMask;
        public uint VisibleNodeMask;

        public static D3D12_HEAP_PROPERTIES Create(uint type) => new()
        {
            Type = type,
            CPUPageProperty = 0,
            MemoryPoolPreference = 0,
            CreationNodeMask = 0,
            VisibleNodeMask = 0
        };
    }

    // MSVC sizeof(D3D12_RESOURCE_DESC) == 56 on x86 and x64 (UINT64-aligned; no pointer fields).
    [StructLayout(LayoutKind.Explicit, Size = 56)]
    public struct D3D12_RESOURCE_DESC
    {
        [FieldOffset(0)] public uint Dimension;
        [FieldOffset(8)] public ulong Alignment;
        [FieldOffset(16)] public ulong Width;
        [FieldOffset(24)] public uint Height;
        [FieldOffset(28)] public ushort DepthOrArraySize;
        [FieldOffset(30)] public ushort MipLevels;
        [FieldOffset(32)] public uint Format;
        [FieldOffset(36)] public uint SampleCount;
        [FieldOffset(40)] public uint SampleQuality;
        [FieldOffset(44)] public uint Layout;
        [FieldOffset(48)] public uint Flags;

        public static D3D12_RESOURCE_DESC Texture2D(uint format, uint width, uint height, uint flags) => new()
        {
            Dimension = D3D12_RESOURCE_DIMENSION_TEXTURE2D,
            Alignment = 0,
            Width = width,
            Height = height,
            DepthOrArraySize = 1,
            MipLevels = 1,
            Format = format,
            SampleCount = 1,
            SampleQuality = 0,
            Layout = D3D12_TEXTURE_LAYOUT_UNKNOWN,
            Flags = flags
        };
    }

    public static int CreateCommittedResource(
        IntPtr device,
        in D3D12_HEAP_PROPERTIES heapProperties,
        uint heapFlags,
        in D3D12_RESOURCE_DESC desc,
        uint initialResourceState,
        IntPtr pOptimizedClearValue,
        in Guid riid,
        out IntPtr ppvResource)
    {
        fixed (D3D12_HEAP_PROPERTIES* pHeap = &heapProperties)
        fixed (D3D12_RESOURCE_DESC* pDesc = &desc)
        fixed (Guid* pRiid = &riid)
        fixed (IntPtr* ppResource = &ppvResource)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, void*, uint, void*, uint, IntPtr, void*, void*, int>)
                (*(void***)device)[27])(device, pHeap, heapFlags, pDesc, initialResourceState, pOptimizedClearValue, pRiid, ppResource); // ID3D12Device::CreateCommittedResource
        }
    }

    public static D3D12_RESOURCE_DESC GetResourceDescription(IntPtr resource)
    {
        // COM methods returning structs use an explicit result buffer on both x86 and x64.
        D3D12_RESOURCE_DESC description;
        ((delegate* unmanaged[Stdcall]<IntPtr, D3D12_RESOURCE_DESC*, void>)(*(void***)resource)[10])(
            resource, &description); // ID3D12Resource::GetDesc
        return description;
    }

    // --- D3D11On12 (composition drawing-surface interop) ---
    // IIDs match Windows SDK d3d11on12.h / d3d11.h.

    public static readonly Guid IID_ID3D11On12Device = Guid.Parse("85611e73-70a9-490e-9614-a9e302777904");
    public static readonly Guid IID_ID3D11On12Device2 = Guid.Parse("dc90f331-4740-43fa-866e-67f12cb58223");
    public static readonly Guid IID_ID3D11Texture2D = Guid.Parse("6f15aaf2-d208-4e89-9ab4-489535d34f9c");

    public const uint D3D11_CREATE_DEVICE_BGRA_SUPPORT = 0x20;

    [DllImport("d3d11.dll", ExactSpelling = true)]
    public static extern int D3D11On12CreateDevice(
        IntPtr pDevice,
        uint Flags,
        D3D_FEATURE_LEVEL* pFeatureLevels,
        uint FeatureLevels,
        IntPtr* ppCommandQueues,
        uint NumQueues,
        uint NodeMask,
        out IntPtr ppDevice,
        out IntPtr ppImmediateContext,
        D3D_FEATURE_LEVEL* pChosenFeatureLevel);

    // ID3D11On12Device slots (after IUnknown):
    // 3 CreateWrappedResource, 4 ReleaseWrappedResources, 5 AcquireWrappedResources
    // ID3D11On12Device2 adds UnwrapUnderlyingResource=7, ReturnUnderlyingResource=8.

    public static int UnwrapUnderlyingResource(IntPtr device, IntPtr resource11, IntPtr queue, out IntPtr resource12)
    {
        fixed (Guid* iid = &IID_ID3D12Resource)
        fixed (IntPtr* result = &resource12)
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, IntPtr, Guid*, IntPtr*, int>)(*(void***)device)[7])(
                device, resource11, queue, iid, result); // ID3D11On12Device2::UnwrapUnderlyingResource
    }

    public static int ReturnUnderlyingResource(IntPtr device, IntPtr resource11, uint count, ulong* values, IntPtr* fences) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, ulong*, IntPtr*, int>)(*(void***)device)[8])(
            device, resource11, count, values, fences); // ID3D11On12Device2::ReturnUnderlyingResource

    public const uint D3D11_BIND_RENDER_TARGET = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_RESOURCE_FLAGS
    {
        public uint BindFlags;
        public uint MiscFlags;
        public uint CPUAccessFlags;
        public uint StructureByteStride;
    }

    public static int CreateWrappedResource(
        IntPtr d3d11On12Device,
        IntPtr pResource12,
        in D3D11_RESOURCE_FLAGS flags11,
        uint inState,
        uint outState,
        in Guid riid,
        out IntPtr ppResource11)
    {
        fixed (D3D11_RESOURCE_FLAGS* pFlags = &flags11)
        fixed (Guid* pRiid = &riid)
        fixed (IntPtr* ppResource = &ppResource11)
        {
            return ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void*, uint, uint, void*, void*, int>)
                (*(void***)d3d11On12Device)[3])( // ID3D11On12Device::CreateWrappedResource
                d3d11On12Device, pResource12, pFlags, inState, outState, pRiid, ppResource);
        }
    }

    public static void ReleaseWrappedResources(IntPtr d3d11On12Device, IntPtr* ppResources, uint numResources)
    {
        ((delegate* unmanaged[Stdcall]<IntPtr, void*, uint, void>)
            (*(void***)d3d11On12Device)[4])(d3d11On12Device, ppResources, numResources); // ID3D11On12Device::ReleaseWrappedResources
    }

    public static void AcquireWrappedResources(IntPtr d3d11On12Device, IntPtr* ppResources, uint numResources)
    {
        ((delegate* unmanaged[Stdcall]<IntPtr, void*, uint, void>)
            (*(void***)d3d11On12Device)[5])(d3d11On12Device, ppResources, numResources); // ID3D11On12Device::AcquireWrappedResources
    }

    // ID3D11DeviceContext slots (after ID3D11DeviceChild 0-6): CopySubresourceRegion=46, Flush=111
    public static void CopySubresourceRegion(
        IntPtr d3d11Context,
        IntPtr pDstResource,
        uint dstSubresource,
        uint dstX,
        uint dstY,
        uint dstZ,
        IntPtr pSrcResource,
        uint srcSubresource,
        IntPtr pSrcBox)
    {
        ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, uint, uint, uint, IntPtr, uint, IntPtr, void>)
            (*(void***)d3d11Context)[46])( // ID3D11DeviceContext::CopySubresourceRegion
            d3d11Context, pDstResource, dstSubresource, dstX, dstY, dstZ,
            pSrcResource, srcSubresource, pSrcBox);
    }

    public static void Flush(IntPtr d3d11Context)
    {
        ((delegate* unmanaged[Stdcall]<IntPtr, void>)(*(void***)d3d11Context)[111])(d3d11Context); // ID3D11DeviceContext::Flush
    }
}

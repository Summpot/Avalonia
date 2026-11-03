using System;
using System.Runtime.InteropServices;
using Avalonia.Direct3D12;
using Avalonia.Win32.DirectX;
using MicroCom.Runtime;

namespace Avalonia.Win32.Direct3D12;

internal static unsafe class D3D12Interop
{
    public static readonly Guid IID_ID3D12Device = MicroComRuntime.GetGuidFor(typeof(ID3D12Device));
    public static readonly Guid IID_ID3D12CommandQueue = MicroComRuntime.GetGuidFor(typeof(ID3D12CommandQueue));
    public static readonly Guid IID_ID3D12CommandAllocator = MicroComRuntime.GetGuidFor(typeof(ID3D12CommandAllocator));
    public static readonly Guid IID_ID3D12GraphicsCommandList = MicroComRuntime.GetGuidFor(typeof(ID3D12GraphicsCommandList));
    public static readonly Guid IID_ID3D12Fence = MicroComRuntime.GetGuidFor(typeof(ID3D12Fence));
    public static readonly Guid IID_ID3D12Resource = MicroComRuntime.GetGuidFor(typeof(ID3D12Resource));

    [DllImport("d3d12.dll", ExactSpelling = true)]
    public static extern int D3D12CreateDevice(
        IntPtr pAdapter,
        D3D_FEATURE_LEVEL minimumFeatureLevel,
        in Guid riid,
        out IntPtr ppDevice);

    public const uint D3D12_COMMAND_LIST_TYPE_DIRECT = 0;
    public const uint D3D12_COMMAND_QUEUE_FLAG_NONE = 0;
    public const uint D3D12_FENCE_FLAG_NONE = 0;

    public const uint D3D12_RESOURCE_STATE_COMMON = D3D12ResourceStates.Common;
    public const uint D3D12_RESOURCE_STATE_PRESENT = D3D12ResourceStates.Present;
    public const uint D3D12_RESOURCE_STATE_RENDER_TARGET = D3D12ResourceStates.RenderTarget;
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

    // Natural pointer alignment makes the native barrier 32 bytes on x64 and 24 on x86.
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
        return Marshal.Release(pUnk);
    }

    public static int CreateCommandQueue(IntPtr device, in D3D12_COMMAND_QUEUE_DESC desc, in Guid riid, out IntPtr ppCommandQueue)
    {
        using var proxy = MicroComRuntime.CreateProxyFor<ID3D12Device>(device, false);
        fixed (D3D12_COMMAND_QUEUE_DESC* pDesc = &desc)
        fixed (Guid* pRiid = &riid)
        fixed (IntPtr* ppQueue = &ppCommandQueue)
        {
            return proxy.CreateCommandQueue(pDesc, pRiid, (void**)ppQueue);
        }
    }

    public static int CreateCommandAllocator(IntPtr device, uint type, in Guid riid, out IntPtr ppCommandAllocator)
    {
        using var proxy = MicroComRuntime.CreateProxyFor<ID3D12Device>(device, false);
        fixed (Guid* pRiid = &riid)
        fixed (IntPtr* ppAlloc = &ppCommandAllocator)
        {
            return proxy.CreateCommandAllocator(type, pRiid, (void**)ppAlloc);
        }
    }

    public static int CreateCommandList(IntPtr device, uint nodeMask, uint type, IntPtr pCommandAllocator, IntPtr pInitialState, in Guid riid, out IntPtr ppCommandList)
    {
        using var proxy = MicroComRuntime.CreateProxyFor<ID3D12Device>(device, false);
        fixed (Guid* pRiid = &riid)
        fixed (IntPtr* ppList = &ppCommandList)
        {
            return proxy.CreateCommandList(nodeMask, type, (void*)pCommandAllocator, (void*)pInitialState, pRiid, (void**)ppList);
        }
    }

    public static int CreateFence(IntPtr device, ulong initialValue, uint flags, in Guid riid, out IntPtr ppFence)
    {
        using var proxy = MicroComRuntime.CreateProxyFor<ID3D12Device>(device, false);
        fixed (Guid* pRiid = &riid)
        fixed (IntPtr* ppF = &ppFence)
        {
            return proxy.CreateFence(initialValue, flags, pRiid, (void**)ppF);
        }
    }

    public const uint D3D12_HEAP_TYPE_DEFAULT = 1;
    public const uint D3D12_HEAP_FLAG_NONE = 0;
    public const uint D3D12_RESOURCE_DIMENSION_TEXTURE2D = 3;
    public const uint D3D12_TEXTURE_LAYOUT_UNKNOWN = 0;
    public const uint D3D12_RESOURCE_FLAG_ALLOW_RENDER_TARGET = 0x1;
    public const uint D3D12_RESOURCE_FLAG_ALLOW_SIMULTANEOUS_ACCESS = 0x20;

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
        using var proxy = MicroComRuntime.CreateProxyFor<ID3D12Device>(device, false);
        fixed (D3D12_HEAP_PROPERTIES* pHeap = &heapProperties)
        fixed (D3D12_RESOURCE_DESC* pDesc = &desc)
        fixed (Guid* pRiid = &riid)
        fixed (IntPtr* ppResource = &ppvResource)
        {
            return proxy.CreateCommittedResource(pHeap, heapFlags, pDesc, initialResourceState,
                (void*)pOptimizedClearValue, pRiid, (void**)ppResource);
        }
    }

    public static int CreateSharedHandle(IntPtr device, IntPtr pObject, IntPtr pAttributes, uint access, string? name, out IntPtr pHandle)
    {
        using var proxy = MicroComRuntime.CreateProxyFor<ID3D12Device>(device, false);
        fixed (char* pName = name)
        fixed (IntPtr* ppHandle = &pHandle)
        {
            return proxy.CreateSharedHandle((void*)pObject, (void*)pAttributes, access, (ushort*)pName, ppHandle);
        }
    }

    public static int OpenSharedHandle(IntPtr device, IntPtr ntHandle, in Guid riid, out IntPtr ppvObj)
    {
        using var proxy = MicroComRuntime.CreateProxyFor<ID3D12Device>(device, false);
        fixed (Guid* pRiid = &riid)
        fixed (IntPtr* ppObj = &ppvObj)
        {
            return proxy.OpenSharedHandle(ntHandle, pRiid, (void**)ppObj);
        }
    }

    public static D3D12_RESOURCE_DESC GetResourceDescription(IntPtr resource)
    {
        using var proxy = MicroComRuntime.CreateProxyFor<ID3D12Resource>(resource, false);
        // COM methods returning structs use an explicit result buffer on both x86 and x64.
        D3D12_RESOURCE_DESC description;
        proxy.GetDesc(&description);
        return description;
    }

    public static readonly Guid IID_ID3D11Texture2D = MicroComRuntime.GetGuidFor(typeof(ID3D11Texture2D));

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

    public const uint D3D11_BIND_RENDER_TARGET = 0x20;

    [StructLayout(LayoutKind.Sequential)]
    public struct D3D11_RESOURCE_FLAGS
    {
        public uint BindFlags;
        public uint MiscFlags;
        public uint CPUAccessFlags;
        public uint StructureByteStride;
    }

}

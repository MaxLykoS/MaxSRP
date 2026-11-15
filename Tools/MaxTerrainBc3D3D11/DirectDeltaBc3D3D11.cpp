#include <d3d11.h>
#include <wrl/client.h>
#include <cstdio>
#include <memory>
#include <new>
#include "IUnityGraphics.h"
#include "IUnityGraphicsD3D11.h"

using Microsoft::WRL::ComPtr;

namespace
{
    IUnityInterfaces* unityInterfaces = nullptr;
    thread_local char lastError[512]{};

    struct TextureCopy
    {
        ComPtr<ID3D11Texture2D> blocks;
        ComPtr<ID3D11Texture2D> pool;
        UINT subresource = 0;
    };

    struct PageCopy
    {
        TextureCopy albedo;
        TextureCopy data;
        ComPtr<ID3D11Device> device;
    };

    bool RetainCopy(TextureCopy& copy, ID3D11Resource* blocks, ID3D11Resource* pool,
        int slot, DXGI_FORMAT format, ComPtr<ID3D11Device>& device, const char* channel)
    {
        if (!blocks || !pool || slot < 0 ||
            FAILED(blocks->QueryInterface(IID_PPV_ARGS(copy.blocks.GetAddressOf()))) ||
            FAILED(pool->QueryInterface(IID_PPV_ARGS(copy.pool.GetAddressOf()))))
        {
            std::snprintf(lastError, sizeof lastError, "%s: slot=%d or source/target is not a D3D11 Texture2D",
                channel, slot);
            return false;
        }

        D3D11_TEXTURE2D_DESC source{}, destination{};
        copy.blocks->GetDesc(&source);
        copy.pool->GetDesc(&destination);
        // Unity D3D11 用 TYPELESS 资源承载 UINT render-target view。
        // 两种资源都保存相同的 128 位块，Copy 按位写入 BC3 切片。
        if ((source.Format != DXGI_FORMAT_R32G32B32A32_UINT &&
             source.Format != DXGI_FORMAT_R32G32B32A32_TYPELESS) ||
            (destination.Format != format && destination.Format != DXGI_FORMAT_BC3_TYPELESS) ||
            source.Width * 4 != destination.Width || source.Height * 4 != destination.Height ||
            source.SampleDesc.Count != 1 || destination.SampleDesc.Count != 1 ||
            destination.Usage != D3D11_USAGE_DEFAULT || static_cast<UINT>(slot) >= destination.ArraySize)
        {
            std::snprintf(lastError, sizeof lastError,
                "%s: incompatible source/target for slot %d, expected 128-bit UINT/TYPELESS blocks to DXGI %u; "
                "source DXGI=%u %ux%u usage=%u samples=%u slices=%u mips=%u; "
                "target DXGI=%u %ux%u usage=%u samples=%u slices=%u mips=%u",
                channel, slot, format,
                source.Format, source.Width, source.Height, source.Usage, source.SampleDesc.Count,
                source.ArraySize, source.MipLevels, destination.Format, destination.Width, destination.Height,
                destination.Usage, destination.SampleDesc.Count, destination.ArraySize, destination.MipLevels);
            return false;
        }

        ComPtr<ID3D11Device> sourceDevice, destinationDevice;
        copy.blocks->GetDevice(sourceDevice.GetAddressOf());
        copy.pool->GetDevice(destinationDevice.GetAddressOf());
        if (sourceDevice.Get() != destinationDevice.Get() || (device && sourceDevice.Get() != device.Get()))
        {
            std::snprintf(lastError, sizeof lastError, "%s: copy resources belong to different D3D11 devices", channel);
            return false;
        }
        device = sourceDevice;
        copy.subresource = D3D11CalcSubresource(0, static_cast<UINT>(slot), destination.MipLevels);
        return true;
    }

    void UNITY_INTERFACE_API CopyPage(int, void* data)
    {
        // 渲染线程接管提交数据。四张纹理的 COM 引用保持到两次 Copy 均提交。
        std::unique_ptr<PageCopy> page(static_cast<PageCopy*>(data));
        if (!page) return;
        if (unityInterfaces)
        {
            const auto* d3d11 = unityInterfaces->Get<IUnityGraphicsD3D11>();
            if (!d3d11 || d3d11->GetDevice() != page->device.Get()) return;
        }

        ComPtr<ID3D11DeviceContext> context;
        page->device->GetImmediateContext(context.GetAddressOf());
        context->CopySubresourceRegion(page->albedo.pool.Get(), page->albedo.subresource, 0, 0, 0,
            page->albedo.blocks.Get(), 0, nullptr);
        context->CopySubresourceRegion(page->data.pool.Get(), page->data.subresource, 0, 0, 0,
            page->data.blocks.Get(), 0, nullptr);
    }
}

extern "C" UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API UnityPluginLoad(IUnityInterfaces* interfaces)
{
    unityInterfaces = interfaces;
}

extern "C" UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API UnityPluginUnload()
{
    unityInterfaces = nullptr;
}

extern "C" UNITY_INTERFACE_EXPORT void* UNITY_INTERFACE_API PrepareBc3Copy(ID3D11Resource* colorBlocks,
    ID3D11Resource* dataBlocks, ID3D11Resource* colorPool, ID3D11Resource* dataPool, int slot)
{
    lastError[0] = '\0';
    std::unique_ptr<PageCopy> page(new (std::nothrow) PageCopy());
    if (!page)
    {
        std::snprintf(lastError, sizeof lastError, "Could not allocate a native BC3 copy request");
        return nullptr;
    }
    if (!RetainCopy(page->albedo, colorBlocks, colorPool, slot, DXGI_FORMAT_BC3_UNORM_SRGB, page->device, "albedo") ||
        !RetainCopy(page->data, dataBlocks, dataPool, slot, DXGI_FORMAT_BC3_UNORM, page->device, "data"))
        return nullptr;
    return page.release();
}

extern "C" UNITY_INTERFACE_EXPORT const char* UNITY_INTERFACE_API GetBc3CopyError()
{
    return lastError;
}

extern "C" UNITY_INTERFACE_EXPORT void UNITY_INTERFACE_API CancelBc3Copy(void* request)
{
    // 只在托管端还没提交渲染事件时调用。提交后由 CopyPage 释放。
    delete static_cast<PageCopy*>(request);
}

extern "C" UNITY_INTERFACE_EXPORT UnityRenderingEventAndData UNITY_INTERFACE_API GetBc3CopyRenderEvent()
{
    return CopyPage;
}

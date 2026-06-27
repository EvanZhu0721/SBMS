#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <swdevice.h>

#include <cstdio>

static const wchar_t* StopEventName = L"Local\\SBMSDeviceHostStop";

static void WINAPI CreationCallback(
    _In_ HSWDEVICE,
    _In_ HRESULT hrCreateResult,
    _In_opt_ PVOID pContext,
    _In_opt_ PCWSTR)
{
    auto eventHandle = static_cast<HANDLE>(pContext);
    if (FAILED(hrCreateResult)) {
        std::printf("device_create_result=0x%lx\n", hrCreateResult);
    }
    SetEvent(eventHandle);
}

int wmain()
{
    HANDLE createdEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!createdEvent) {
        std::printf("error=CreateEvent created\n");
        return 1;
    }

    HANDLE stopEvent = CreateEventW(nullptr, TRUE, FALSE, StopEventName);
    if (!stopEvent) {
        std::printf("error=CreateEvent stop\n");
        CloseHandle(createdEvent);
        return 1;
    }
    ResetEvent(stopEvent);

    SW_DEVICE_CREATE_INFO createInfo{};
    createInfo.cbSize = sizeof(createInfo);
    createInfo.pszzHardwareIds = L"IddSampleDriver\0\0";
    createInfo.pszzCompatibleIds = L"IddSampleDriver\0\0";
    createInfo.pszInstanceId = L"IddSampleDriver";
    createInfo.pszDeviceDescription = L"SBMS Virtual Display";
    createInfo.CapabilityFlags = SWDeviceCapabilitiesRemovable |
                                 SWDeviceCapabilitiesSilentInstall |
                                 SWDeviceCapabilitiesDriverRequired;

    HSWDEVICE device = nullptr;
    HRESULT hr = SwDeviceCreate(
        L"IddSampleDriver",
        L"HTREE\\ROOT\\0",
        &createInfo,
        0,
        nullptr,
        CreationCallback,
        createdEvent,
        &device);
    if (FAILED(hr)) {
        std::printf("error=SwDeviceCreate hr=0x%lx\n", hr);
        CloseHandle(stopEvent);
        CloseHandle(createdEvent);
        return 1;
    }

    DWORD createWait = WaitForSingleObject(createdEvent, 30000);
    if (createWait != WAIT_OBJECT_0) {
        std::printf("error=device_create_timeout\n");
        if (device) {
            SwDeviceClose(device);
        }
        CloseHandle(stopEvent);
        CloseHandle(createdEvent);
        return 1;
    }

    std::printf("device_host=ready\n");
    std::fflush(stdout);
    WaitForSingleObject(stopEvent, INFINITE);
    std::printf("device_host=stopping\n");
    std::fflush(stdout);

    if (device) {
        SwDeviceClose(device);
    }
    CloseHandle(stopEvent);
    CloseHandle(createdEvent);
    std::printf("device_host=stopped\n");
    return 0;
}

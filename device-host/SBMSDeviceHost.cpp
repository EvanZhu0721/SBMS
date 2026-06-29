#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <swdevice.h>

#include <algorithm>
#include <cstdio>
#include <string>
#include <vector>

static const wchar_t* StopEventName = L"Local\\SBMSDeviceHostStop";
static constexpr int MaxDeviceCount = 3;

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

static int ParseDeviceCount(int argc, wchar_t** argv)
{
    int count = 1;
    for (int i = 1; i < argc; ++i) {
        std::wstring arg = argv[i];
        if (arg == L"--count" && i + 1 < argc) {
            count = _wtoi(argv[++i]);
        } else {
            std::printf("error=unknown_argument\n");
            return -1;
        }
    }
    return std::max(1, std::min(count, MaxDeviceCount));
}

int wmain(int argc, wchar_t** argv)
{
    int requestedCount = ParseDeviceCount(argc, argv);
    if (requestedCount < 0) {
        return 2;
    }

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

    std::vector<HSWDEVICE> devices;
    devices.reserve(static_cast<size_t>(requestedCount));
    for (int i = 0; i < requestedCount; ++i) {
        ResetEvent(createdEvent);

        std::wstring instanceId = (i == 0)
            ? L"IddSampleDriver"
            : (L"IddSampleDriver" + std::to_wstring(i + 1));

        SW_DEVICE_CREATE_INFO createInfo{};
        createInfo.cbSize = sizeof(createInfo);
        createInfo.pszzHardwareIds = L"IddSampleDriver\0\0";
        createInfo.pszzCompatibleIds = L"IddSampleDriver\0\0";
        createInfo.pszInstanceId = instanceId.c_str();
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
            std::printf("error=SwDeviceCreate index=%d hr=0x%lx\n", i + 1, hr);
            for (auto existing : devices) {
                SwDeviceClose(existing);
            }
            CloseHandle(stopEvent);
            CloseHandle(createdEvent);
            return 1;
        }

        DWORD createWait = WaitForSingleObject(createdEvent, 30000);
        if (createWait != WAIT_OBJECT_0) {
            std::printf("error=device_create_timeout index=%d\n", i + 1);
            if (device) {
                SwDeviceClose(device);
            }
            for (auto existing : devices) {
                SwDeviceClose(existing);
            }
            CloseHandle(stopEvent);
            CloseHandle(createdEvent);
            return 1;
        }

        devices.push_back(device);
        std::printf("device_host=created index=%d instance=%ls\n", i + 1, instanceId.c_str());
    }

    std::printf("device_host=ready count=%d\n", requestedCount);
    std::fflush(stdout);
    WaitForSingleObject(stopEvent, INFINITE);
    std::printf("device_host=stopping\n");
    std::fflush(stdout);

    for (auto it = devices.rbegin(); it != devices.rend(); ++it) {
        SwDeviceClose(*it);
    }
    CloseHandle(stopEvent);
    CloseHandle(createdEvent);
    std::printf("device_host=stopped\n");
    return 0;
}

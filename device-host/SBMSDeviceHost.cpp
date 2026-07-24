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

struct HostArgs
{
    int count = 1;
    std::wstring startGate;
};

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

static bool ParseArgs(int argc, wchar_t** argv, HostArgs& args)
{
    for (int i = 1; i < argc; ++i) {
        std::wstring arg = argv[i];
        if (arg == L"--count" && i + 1 < argc) {
            args.count = _wtoi(argv[++i]);
        } else if (arg == L"--start-gate" && i + 1 < argc) {
            args.startGate = argv[++i];
        } else {
            std::printf("error=unknown_argument\n");
            return false;
        }
    }
    args.count = std::max(1, std::min(args.count, MaxDeviceCount));
    return true;
}

static bool WaitForStartGate(const std::wstring& name)
{
    if (name.empty()) {
        return true;
    }

    HANDLE gate = OpenEventW(SYNCHRONIZE, FALSE, name.c_str());
    if (!gate) {
        std::printf("error=start_gate_open win32=%lu\n", GetLastError());
        return false;
    }

    std::printf("start_gate=waiting\n");
    std::fflush(stdout);
    DWORD waitResult = WaitForSingleObject(gate, INFINITE);
    CloseHandle(gate);
    if (waitResult != WAIT_OBJECT_0) {
        std::printf("error=start_gate_wait result=%lu\n", waitResult);
        return false;
    }
    std::printf("start_gate=released\n");
    std::fflush(stdout);
    return true;
}

int wmain(int argc, wchar_t** argv)
{
    HostArgs args;
    if (!ParseArgs(argc, argv, args)) {
        return 2;
    }
    if (!WaitForStartGate(args.startGate)) {
        return 1;
    }
    int requestedCount = args.count;

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

        wchar_t instanceIdBuffer[32]{};
        swprintf_s(instanceIdBuffer, L"VirtualDisplay-%02d", i + 1);
        std::wstring instanceId = instanceIdBuffer;

        SW_DEVICE_CREATE_INFO createInfo{};
        createInfo.cbSize = sizeof(createInfo);
        createInfo.pszzHardwareIds = L"SBMS\\IndirectDisplay\0\0";
        createInfo.pszzCompatibleIds = L"SBMS\\IndirectDisplay\0\0";
        createInfo.pszInstanceId = instanceId.c_str();
        createInfo.pszDeviceDescription = L"SBMS Virtual Display Adapter";
        createInfo.CapabilityFlags = SWDeviceCapabilitiesRemovable |
                                     SWDeviceCapabilitiesSilentInstall |
                                     SWDeviceCapabilitiesDriverRequired;

        HSWDEVICE device = nullptr;
        HRESULT hr = SwDeviceCreate(
            L"SBMS",
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

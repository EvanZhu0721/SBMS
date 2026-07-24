#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <swdevice.h>

#include <algorithm>
#include <cstdio>
#include <string>
#include <vector>

static constexpr int MaxDeviceCount = 3;

struct CreationContext
{
    HANDLE eventHandle = nullptr;
    HRESULT result = E_PENDING;
    std::wstring instanceId;
};

static void WINAPI CreationCallback(
    _In_ HSWDEVICE,
    _In_ HRESULT hrCreateResult,
    _In_opt_ PVOID pContext,
    _In_opt_ PCWSTR deviceInstanceId)
{
    auto context = static_cast<CreationContext*>(pContext);
    if (!context) {
        return;
    }
    context->result = hrCreateResult;
    if (deviceInstanceId) {
        context->instanceId = deviceInstanceId;
    }
    SetEvent(context->eventHandle);
}

struct HostOptions
{
    int count = 1;
    std::wstring runId;
    std::wstring stopEventName = L"Local\\SBMSDeviceHostStop";
};

static bool IsRunId(const std::wstring& value)
{
    if (value.size() != 36) {
        return false;
    }
    for (size_t i = 0; i < value.size(); ++i) {
        if (i == 8 || i == 13 || i == 18 || i == 23) {
            if (value[i] != L'-') {
                return false;
            }
        } else if (!((value[i] >= L'0' && value[i] <= L'9') ||
                     (value[i] >= L'a' && value[i] <= L'f') ||
                     (value[i] >= L'A' && value[i] <= L'F'))) {
            return false;
        }
    }
    return true;
}

static bool ParseOptions(int argc, wchar_t** argv, HostOptions& options)
{
    for (int i = 1; i < argc; ++i) {
        std::wstring arg = argv[i];
        if (arg == L"--count" && i + 1 < argc) {
            const std::wstring value = argv[++i];
            wchar_t* end = nullptr;
            long parsed = wcstol(value.c_str(), &end, 10);
            if (!end || *end != L'\0' || parsed < 1 || parsed > MaxDeviceCount) {
                std::printf("error=invalid_count\n");
                return false;
            }
            options.count = static_cast<int>(parsed);
        } else if (arg == L"--run-id" && i + 1 < argc) {
            options.runId = argv[++i];
            if (!IsRunId(options.runId)) {
                std::printf("error=invalid_run_id\n");
                return false;
            }
        } else if (arg == L"--stop-event" && i + 1 < argc) {
            options.stopEventName = argv[++i];
            if (options.stopEventName.rfind(L"Global\\SBMSDeviceHostStop-", 0) != 0 ||
                options.stopEventName.size() <= wcslen(L"Global\\SBMSDeviceHostStop-")) {
                std::printf("error=invalid_stop_event\n");
                return false;
            }
        } else {
            std::printf("error=unknown_argument\n");
            return false;
        }
    }
    if ((!options.runId.empty()) !=
        (options.stopEventName.rfind(L"Global\\SBMSDeviceHostStop-", 0) == 0)) {
        std::printf("error=run_identity_incomplete\n");
        return false;
    }
    if (!options.runId.empty() &&
        options.stopEventName != (L"Global\\SBMSDeviceHostStop-" + options.runId)) {
        std::printf("error=run_identity_mismatch\n");
        return false;
    }
    return true;
}

int wmain(int argc, wchar_t** argv)
{
    HostOptions options;
    if (!ParseOptions(argc, argv, options)) {
        return 2;
    }

    HANDLE createdEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!createdEvent) {
        std::printf("error=CreateEvent created\n");
        return 1;
    }

    HANDLE stopEvent = CreateEventW(nullptr, TRUE, FALSE, options.stopEventName.c_str());
    if (!stopEvent) {
        std::printf("error=CreateEvent stop\n");
        CloseHandle(createdEvent);
        return 1;
    }
    ResetEvent(stopEvent);

    std::vector<HSWDEVICE> devices;
    devices.reserve(static_cast<size_t>(options.count));
    for (int i = 0; i < options.count; ++i) {
        ResetEvent(createdEvent);
        CreationContext creationContext{ createdEvent, E_PENDING, L"" };

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
            &creationContext,
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
        if (FAILED(creationContext.result) || creationContext.instanceId.empty()) {
            std::printf("error=device_create_result index=%d hr=0x%lx\n", i + 1, creationContext.result);
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
        std::printf("device_host=created index=%d instance=%ls\n", i + 1, creationContext.instanceId.c_str());
    }

    std::printf("device_host=ready count=%d run_id=%ls stop_event=%ls\n",
        options.count,
        options.runId.empty() ? L"gui" : options.runId.c_str(),
        options.stopEventName.c_str());
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

#define NOMINMAX

#include <windows.h>
#include <wdf.h>
#include <iddcx.h>
#include <dxgi1_5.h>
#include <d3d11.h>
#include <new>

EVT_WDF_DRIVER_DEVICE_ADD DeviceAdd;
EVT_WDF_DEVICE_D0_ENTRY DeviceD0Entry;
EVT_WDF_OBJECT_CONTEXT_CLEANUP MonitorCleanup;
EVT_IDD_CX_ADAPTER_INIT_FINISHED AdapterInitFinished;
EVT_IDD_CX_ADAPTER_COMMIT_MODES AdapterCommitModes;
EVT_IDD_CX_PARSE_MONITOR_DESCRIPTION ParseMonitorDescription;
EVT_IDD_CX_MONITOR_GET_DEFAULT_DESCRIPTION_MODES GetDefaultMonitorModes;
EVT_IDD_CX_MONITOR_QUERY_TARGET_MODES QueryTargetModes;
EVT_IDD_CX_MONITOR_ASSIGN_SWAPCHAIN AssignSwapChain;
EVT_IDD_CX_MONITOR_UNASSIGN_SWAPCHAIN UnassignSwapChain;

namespace
{
constexpr UINT kMaximumDimension = 16384;
constexpr UINT kMaximumRefreshRate = 1000;
constexpr wchar_t kSessionGate[] = L"Global\\SBMSSession-v4";
constexpr wchar_t kFramePrefix[] = L"Global\\SBMSFrame-v4-";
constexpr wchar_t kEventPrefix[] = L"Global\\SBMSFrameReady-v4-";
constexpr UINT kGateMagic = 0x53424734;
constexpr UINT kProtocolVersion = 4;

struct GateHeader
{
    UINT magic;
    UINT version;
    UINT width;
    UINT height;
    UINT stride;
    UINT refreshNumerator;
    UINT refreshDenominator;
    UINT flags;
    BYTE nonce[16];
};

struct FrameHeader
{
    UINT magic;
    UINT width;
    UINT height;
    UINT stride;
    volatile LONG publishedSlot;
    volatile LONG readerSlot;
};

constexpr UINT kFrameMagic = 0x53424d53;
constexpr UINT kFrameError = 0x45525221;
static_assert(sizeof(FrameHeader) == 24);
static_assert(sizeof(GateHeader) == 48);

struct ModeConfig
{
    UINT width;
    UINT height;
    UINT refreshNumerator;
    UINT refreshDenominator;
    SIZE_T stride;
    SIZE_T framePixels;
    SIZE_T frameBytes;
};

// One permanent monitor identity. Changing either value makes Windows treat the
// virtual monitor as new hardware and loses its saved desktop placement.
constexpr GUID kMonitorContainerId =
    {0x67453031, 0x7ba9, 0x4d45, {0x8f, 0x0b, 0x72, 0x1d, 0xb4, 0x61, 0x62, 0x42}};

struct Drain
{
    IDDCX_SWAPCHAIN swapChain{};
    ID3D11Device* d3dDevice{};
    ID3D11DeviceContext* d3dContext{};
    ID3D11Texture2D* staging{};
    HANDLE frameAvailable{};
    HANDLE frameMapping{};
    HANDLE frameEvent{};
    FrameHeader* frame{};
    HANDLE stop{};
    HANDLE thread{};
    volatile LONG ownership{};
    ModeConfig mode{};
};

void PublishError(Drain* drain, UINT stage, HRESULT result, UINT detail = 0)
{
    drain->frame->width = stage;
    drain->frame->height = static_cast<UINT>(result);
    drain->frame->stride = detail;
    MemoryBarrier();
    drain->frame->magic = kFrameError;
    SetEvent(drain->frameEvent);
}

bool PublishFrame(Drain* drain, IDXGIResource* surface)
{
    ID3D11Texture2D* source = nullptr;
    HRESULT result = surface->QueryInterface(IID_PPV_ARGS(&source));
    if (FAILED(result))
    {
        PublishError(drain, 1, result);
        return false;
    }

    if (drain->staging == nullptr)
    {
        D3D11_TEXTURE2D_DESC description{};
        source->GetDesc(&description);
        if (description.Width != drain->mode.width ||
            description.Height != drain->mode.height ||
            description.Format != DXGI_FORMAT_B8G8R8A8_UNORM)
        {
            PublishError(
                drain,
                2,
                E_INVALIDARG,
                static_cast<UINT>(description.Format));
            source->Release();
            return false;
        }
        description.Usage = D3D11_USAGE_STAGING;
        description.BindFlags = 0;
        description.CPUAccessFlags = D3D11_CPU_ACCESS_READ;
        description.MiscFlags = 0;
        result = drain->d3dDevice->CreateTexture2D(&description, nullptr, &drain->staging);
        if (FAILED(result))
        {
            PublishError(drain, 3, result);
        }
    }
    if (SUCCEEDED(result))
    {
        const LONG published =
            InterlockedCompareExchange(&drain->frame->publishedSlot, 0, 0);
        const LONG destinationSlot = published == 0 ? 1 : 0;
        if (InterlockedCompareExchange(&drain->frame->readerSlot, 0, 0) ==
            destinationSlot)
        {
            source->Release();
            return true;
        }

        drain->d3dContext->CopyResource(drain->staging, source);
        D3D11_MAPPED_SUBRESOURCE mapped{};
        result = drain->d3dContext->Map(drain->staging, 0, D3D11_MAP_READ, 0, &mapped);
        if (SUCCEEDED(result))
        {
            BYTE* destination =
                reinterpret_cast<BYTE*>(drain->frame + 1) +
                static_cast<SIZE_T>(destinationSlot) * drain->mode.framePixels;
            const BYTE* sourceRow = static_cast<const BYTE*>(mapped.pData);
            for (UINT row = 0; row < drain->mode.height; ++row)
            {
                memcpy(
                    destination + static_cast<SIZE_T>(row) * drain->mode.stride,
                    sourceRow,
                    drain->mode.stride);
                sourceRow += mapped.RowPitch;
            }
            MemoryBarrier();
            InterlockedExchange(&drain->frame->publishedSlot, destinationSlot);
            SetEvent(drain->frameEvent);
            drain->d3dContext->Unmap(drain->staging, 0);
        }
        else
        {
            PublishError(drain, 4, result);
        }
    }
    source->Release();
    return SUCCEEDED(result);
}

void ChannelName(
    const wchar_t* prefix,
    const BYTE (&nonce)[16],
    wchar_t (&output)[96])
{
    constexpr wchar_t hex[] = L"0123456789abcdef";
    size_t index = 0;
    while (prefix[index] != L'\0')
    {
        output[index] = prefix[index];
        ++index;
    }
    for (BYTE value : nonce)
    {
        output[index++] = hex[value >> 4];
        output[index++] = hex[value & 0x0f];
    }
    output[index] = L'\0';
}

struct DeviceState
{
    WDFDEVICE device{};
    IDDCX_ADAPTER adapter{};
};

struct MonitorState
{
    Drain* drain{};
    ModeConfig mode{};
    BYTE nonce[16]{};
};

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DeviceState, GetDeviceState);
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(MonitorState, GetMonitorState);

bool ValidateMode(
    UINT width,
    UINT height,
    UINT refreshNumerator,
    UINT refreshDenominator,
    ModeConfig& mode)
{
    if (width == 0 || height == 0 ||
        width > kMaximumDimension || height > kMaximumDimension ||
        refreshNumerator == 0 || refreshDenominator == 0)
    {
        return false;
    }
    const UINT64 maximumNumerator =
        static_cast<UINT64>(refreshDenominator) * kMaximumRefreshRate;
    if (refreshNumerator < refreshDenominator ||
        refreshNumerator > maximumNumerator ||
        static_cast<UINT64>(height) * refreshNumerator > MAXUINT32)
    {
        return false;
    }

    const SIZE_T stride = static_cast<SIZE_T>(width) * 4;
    if (height > MAXSIZE_T / stride)
    {
        return false;
    }
    const SIZE_T framePixels = stride * height;
    if (framePixels > (MAXSIZE_T - sizeof(FrameHeader)) / 2)
    {
        return false;
    }

    mode.width = width;
    mode.height = height;
    mode.refreshNumerator = refreshNumerator;
    mode.refreshDenominator = refreshDenominator;
    mode.stride = stride;
    mode.framePixels = framePixels;
    mode.frameBytes = sizeof(FrameHeader) + 2 * framePixels;
    return true;
}

bool ReadSessionConfig(ModeConfig& mode, BYTE (&nonce)[16])
{
    HANDLE gate = OpenFileMappingW(FILE_MAP_READ, FALSE, kSessionGate);
    if (gate == nullptr)
    {
        return false;
    }
    const auto* header = static_cast<const GateHeader*>(
        MapViewOfFile(gate, FILE_MAP_READ, 0, 0, sizeof(GateHeader)));
    bool valid = false;
    if (header != nullptr &&
        header->magic == kGateMagic &&
        header->version == kProtocolVersion &&
        header->flags == 0 &&
        header->stride == static_cast<UINT64>(header->width) * 4 &&
        ValidateMode(
            header->width,
            header->height,
            header->refreshNumerator,
            header->refreshDenominator,
            mode))
    {
        memcpy(nonce, header->nonce, sizeof(header->nonce));
        valid = true;
    }
    if (header != nullptr) UnmapViewOfFile(header);
    CloseHandle(gate);
    return valid;
}

void FillSignal(
    DISPLAYCONFIG_VIDEO_SIGNAL_INFO& signal,
    const ModeConfig& config,
    bool monitorMode)
{
    signal.activeSize.cx = config.width;
    signal.activeSize.cy = config.height;
    signal.totalSize.cx = config.width;
    signal.totalSize.cy = config.height;
    signal.vSyncFreq.Numerator = config.refreshNumerator;
    signal.vSyncFreq.Denominator = config.refreshDenominator;
    signal.hSyncFreq.Numerator = config.height * config.refreshNumerator;
    signal.hSyncFreq.Denominator = config.refreshDenominator;
    signal.pixelRate =
        static_cast<UINT64>(config.width) *
        config.height *
        config.refreshNumerator /
        config.refreshDenominator;
    signal.scanLineOrdering = DISPLAYCONFIG_SCANLINE_ORDERING_PROGRESSIVE;
    signal.AdditionalSignalInfo.videoStandard = 255;
    signal.AdditionalSignalInfo.vSyncFreqDivider = monitorMode ? 0 : 1;
}

IDDCX_MONITOR_MODE MonitorMode(
    const ModeConfig& config,
    IDDCX_MONITOR_MODE_ORIGIN origin)
{
    IDDCX_MONITOR_MODE mode{};
    mode.Size = sizeof(mode);
    mode.Origin = origin;
    FillSignal(mode.MonitorVideoSignalInfo, config, true);
    return mode;
}

IDDCX_TARGET_MODE TargetMode(const ModeConfig& config)
{
    IDDCX_TARGET_MODE mode{};
    mode.Size = sizeof(mode);
    FillSignal(mode.TargetVideoSignalInfo.targetVideoSignalInfo, config, false);
    return mode;
}

void DestroyDrain(Drain* drain)
{
    if (drain->thread != nullptr) CloseHandle(drain->thread);
    if (drain->stop != nullptr) CloseHandle(drain->stop);
    if (drain->frame != nullptr) UnmapViewOfFile(drain->frame);
    if (drain->frameEvent != nullptr) CloseHandle(drain->frameEvent);
    if (drain->frameMapping != nullptr) CloseHandle(drain->frameMapping);
    if (drain->staging != nullptr) drain->staging->Release();
    if (drain->d3dContext != nullptr) drain->d3dContext->Release();
    if (drain->d3dDevice != nullptr) drain->d3dDevice->Release();
    delete drain;
}

DWORD WINAPI DrainFrames(void* argument)
{
    auto* drain = static_cast<Drain*>(argument);

    for (;;)
    {
        if (WaitForSingleObject(drain->stop, 0) == WAIT_OBJECT_0)
        {
            break;
        }

        IDARG_OUT_RELEASEANDACQUIREBUFFER buffer{};
        const HRESULT result =
            IddCxSwapChainReleaseAndAcquireBuffer(drain->swapChain, &buffer);

        if (result == E_PENDING)
        {
            const HANDLE handles[] = {drain->frameAvailable, drain->stop};
            const DWORD wait = WaitForMultipleObjects(ARRAYSIZE(handles), handles, FALSE, INFINITE);
            if (wait == WAIT_OBJECT_0)
            {
                continue;
            }
            break;
        }

        if (FAILED(result))
        {
            break;
        }

        if (buffer.MetaData.pSurface != nullptr)
        {
            PublishFrame(drain, buffer.MetaData.pSurface);
            buffer.MetaData.pSurface->Release();
        }

        if (FAILED(IddCxSwapChainFinishedProcessingFrame(drain->swapChain)))
        {
            break;
        }
    }

    WdfObjectDelete(drain->swapChain);
    drain->swapChain = nullptr;
    if (InterlockedCompareExchange(&drain->ownership, 2, 0) == 1)
    {
        DestroyDrain(drain);
    }
    return 0;
}

void StopDrain(MonitorState* monitor)
{
    if (monitor == nullptr || monitor->drain == nullptr)
    {
        return;
    }

    Drain* drain = monitor->drain;
    monitor->drain = nullptr;
    SetEvent(drain->stop);
    if (drain->thread != nullptr)
    {
        if (WaitForSingleObject(drain->thread, 5000) != WAIT_OBJECT_0)
        {
            // Either the worker observes state 1 and destroys itself, or it
            // already published state 2 and cleanup remains here.
            if (InterlockedCompareExchange(&drain->ownership, 1, 0) != 2)
            {
                return;
            }
        }
    }
    DestroyDrain(drain);
}

bool StartDrain(
    MonitorState* monitor,
    IDDCX_SWAPCHAIN swapChain,
    LUID renderAdapter,
    HANDLE frameAvailable)
{
    IDXGIFactory5* factory = nullptr;
    IDXGIAdapter1* adapter = nullptr;
    IDXGIDevice* dxgiDevice = nullptr;
    ID3D11Device* d3dDevice = nullptr;
    ID3D11DeviceContext* d3dContext = nullptr;

    HRESULT result = CreateDXGIFactory2(0, IID_PPV_ARGS(&factory));
    if (SUCCEEDED(result))
    {
        result = factory->EnumAdapterByLuid(renderAdapter, IID_PPV_ARGS(&adapter));
    }
    if (SUCCEEDED(result))
    {
        result = D3D11CreateDevice(
            adapter,
            D3D_DRIVER_TYPE_UNKNOWN,
            nullptr,
            D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            nullptr,
            0,
            D3D11_SDK_VERSION,
            &d3dDevice,
            nullptr,
            &d3dContext);
    }
    if (SUCCEEDED(result))
    {
        result = d3dDevice->QueryInterface(IID_PPV_ARGS(&dxgiDevice));
    }
    if (SUCCEEDED(result))
    {
        IDARG_IN_SWAPCHAINSETDEVICE setDevice{};
        setDevice.pDevice = dxgiDevice;
        result = IddCxSwapChainSetDevice(swapChain, &setDevice);
    }

    if (dxgiDevice != nullptr) dxgiDevice->Release();
    if (adapter != nullptr) adapter->Release();
    if (factory != nullptr) factory->Release();

    if (FAILED(result))
    {
        if (d3dContext != nullptr) d3dContext->Release();
        if (d3dDevice != nullptr) d3dDevice->Release();
        return false;
    }

    auto* drain = new (std::nothrow) Drain{};
    if (drain == nullptr)
    {
        d3dDevice->Release();
        return false;
    }

    drain->swapChain = swapChain;
    drain->d3dDevice = d3dDevice;
    drain->d3dContext = d3dContext;
    drain->frameAvailable = frameAvailable;
    drain->mode = monitor->mode;
    wchar_t frameName[96]{};
    wchar_t eventName[96]{};
    ChannelName(kFramePrefix, monitor->nonce, frameName);
    ChannelName(kEventPrefix, monitor->nonce, eventName);

    if (frameName[0] != L'\0')
    {
        drain->frameMapping = OpenFileMappingW(FILE_MAP_WRITE, FALSE, frameName);
    }
    if (drain->frameMapping != nullptr)
    {
        drain->frame = static_cast<FrameHeader*>(
            MapViewOfFile(
                drain->frameMapping,
                FILE_MAP_WRITE,
                0,
                0,
                drain->mode.frameBytes));
    }
    if (eventName[0] != L'\0')
    {
        drain->frameEvent = OpenEventW(EVENT_MODIFY_STATE, FALSE, eventName);
    }
    drain->stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (drain->frame != nullptr && drain->frameEvent != nullptr && drain->stop != nullptr)
    {
        drain->frame->magic = kFrameMagic;
        drain->frame->width = drain->mode.width;
        drain->frame->height = drain->mode.height;
        drain->frame->stride = static_cast<UINT>(drain->mode.stride);
        drain->frame->publishedSlot = -1;
        drain->frame->readerSlot = -1;
        drain->thread = CreateThread(nullptr, 0, DrainFrames, drain, 0, nullptr);
    }
    if (drain->thread == nullptr)
    {
        if (drain->stop != nullptr) CloseHandle(drain->stop);
        if (drain->frame != nullptr) UnmapViewOfFile(drain->frame);
        if (drain->frameEvent != nullptr) CloseHandle(drain->frameEvent);
        if (drain->frameMapping != nullptr) CloseHandle(drain->frameMapping);
        d3dContext->Release();
        d3dDevice->Release();
        delete drain;
        return false;
    }

    monitor->drain = drain;
    return true;
}

NTSTATUS ReportMonitor(DeviceState* state)
{
    ModeConfig mode{};
    BYTE nonce[16]{};
    if (!ReadSessionConfig(mode, nonce))
    {
        return STATUS_INVALID_PARAMETER;
    }

    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, MonitorState);
    attributes.EvtCleanupCallback = MonitorCleanup;

    IDDCX_MONITOR_INFO info{};
    info.Size = sizeof(info);
    info.MonitorType = DISPLAYCONFIG_OUTPUT_TECHNOLOGY_OTHER;
    info.ConnectorIndex = 0;
    info.MonitorContainerId = kMonitorContainerId;
    info.MonitorDescription.Size = sizeof(info.MonitorDescription);
    info.MonitorDescription.Type = IDDCX_MONITOR_DESCRIPTION_TYPE_EDID;
    info.MonitorDescription.DataSize = 0;
    info.MonitorDescription.pData = nullptr;

    IDARG_IN_MONITORCREATE input{};
    input.ObjectAttributes = &attributes;
    input.pMonitorInfo = &info;
    IDARG_OUT_MONITORCREATE output{};
    const NTSTATUS createStatus =
        IddCxMonitorCreate(state->adapter, &input, &output);
    if (!NT_SUCCESS(createStatus))
    {
        return createStatus;
    }

    MonitorState* monitorState = GetMonitorState(output.MonitorObject);
    monitorState->mode = mode;
    memcpy(monitorState->nonce, nonce, sizeof(nonce));

    IDARG_OUT_MONITORARRIVAL arrival{};
    const NTSTATUS arrivalStatus =
        IddCxMonitorArrival(output.MonitorObject, &arrival);
    if (!NT_SUCCESS(arrivalStatus))
    {
        WdfObjectDelete(output.MonitorObject);
        return arrivalStatus;
    }

    return STATUS_SUCCESS;
}

void InitAdapter(DeviceState* state)
{
    if (state->adapter != nullptr)
    {
        return;
    }

    IDDCX_ENDPOINT_VERSION version{};
    version.Size = sizeof(version);
    version.MajorVer = 1;

    IDDCX_ADAPTER_CAPS caps{};
    caps.Size = sizeof(caps);
    caps.Flags = IDDCX_ADAPTER_FLAGS_USE_SMALLEST_MODE;
    caps.MaxMonitorsSupported = 1;
    caps.EndPointDiagnostics.Size = sizeof(caps.EndPointDiagnostics);
    caps.EndPointDiagnostics.GammaSupport = IDDCX_FEATURE_IMPLEMENTATION_NONE;
    caps.EndPointDiagnostics.TransmissionType = IDDCX_TRANSMISSION_TYPE_WIRED_OTHER;
    caps.EndPointDiagnostics.pEndPointFriendlyName = L"SBMS Display";
    caps.EndPointDiagnostics.pEndPointManufacturerName = L"SBMS";
    caps.EndPointDiagnostics.pEndPointModelName = L"SBMS IDD";
    caps.EndPointDiagnostics.pFirmwareVersion = &version;
    caps.EndPointDiagnostics.pHardwareVersion = &version;

    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DeviceState);

    IDARG_IN_ADAPTER_INIT input{};
    input.WdfDevice = state->device;
    input.pCaps = &caps;
    input.ObjectAttributes = &attributes;
    IDARG_OUT_ADAPTER_INIT output{};
    if (NT_SUCCESS(IddCxAdapterInitAsync(&input, &output)))
    {
        state->adapter = output.AdapterObject;
        DeviceState* adapterState = GetDeviceState(output.AdapterObject);
        adapterState->device = state->device;
        adapterState->adapter = output.AdapterObject;
    }
}
}

extern "C" DRIVER_INITIALIZE DriverEntry;

extern "C" BOOL WINAPI DllMain(HINSTANCE, DWORD, void*)
{
    return TRUE;
}

extern "C" NTSTATUS DriverEntry(
    PDRIVER_OBJECT driverObject,
    PUNICODE_STRING registryPath)
{
    WDF_DRIVER_CONFIG config;
    WDF_DRIVER_CONFIG_INIT(&config, DeviceAdd);
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&attributes);
    return WdfDriverCreate(
        driverObject, registryPath, &attributes, &config, WDF_NO_HANDLE);
}

NTSTATUS DeviceAdd(WDFDRIVER driver, PWDFDEVICE_INIT deviceInit)
{
    UNREFERENCED_PARAMETER(driver);

    WDF_PNPPOWER_EVENT_CALLBACKS power;
    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&power);
    power.EvtDeviceD0Entry = DeviceD0Entry;
    WdfDeviceInitSetPnpPowerEventCallbacks(deviceInit, &power);

    IDD_CX_CLIENT_CONFIG idd;
    IDD_CX_CLIENT_CONFIG_INIT(&idd);
    idd.EvtIddCxAdapterInitFinished = AdapterInitFinished;
    idd.EvtIddCxAdapterCommitModes = AdapterCommitModes;
    idd.EvtIddCxParseMonitorDescription = ParseMonitorDescription;
    idd.EvtIddCxMonitorGetDefaultDescriptionModes = GetDefaultMonitorModes;
    idd.EvtIddCxMonitorQueryTargetModes = QueryTargetModes;
    idd.EvtIddCxMonitorAssignSwapChain = AssignSwapChain;
    idd.EvtIddCxMonitorUnassignSwapChain = UnassignSwapChain;

    NTSTATUS status = IddCxDeviceInitConfig(deviceInit, &idd);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DeviceState);
    WDFDEVICE device = nullptr;
    status = WdfDeviceCreate(&deviceInit, &attributes, &device);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    DeviceState* state = GetDeviceState(device);
    state->device = device;
    status = IddCxDeviceInitialize(device);
    return status;
}

NTSTATUS DeviceD0Entry(WDFDEVICE device, WDF_POWER_DEVICE_STATE previousState)
{
    UNREFERENCED_PARAMETER(previousState);
    InitAdapter(GetDeviceState(device));
    return STATUS_SUCCESS;
}

void MonitorCleanup(WDFOBJECT object)
{
    StopDrain(GetMonitorState(object));
}

NTSTATUS AdapterInitFinished(
    IDDCX_ADAPTER adapter,
    const IDARG_IN_ADAPTER_INIT_FINISHED* input)
{
    if (NT_SUCCESS(input->AdapterInitStatus))
    {
        return ReportMonitor(GetDeviceState(adapter));
    }
    return input->AdapterInitStatus;
}

NTSTATUS AdapterCommitModes(
    IDDCX_ADAPTER adapter,
    const IDARG_IN_COMMITMODES* input)
{
    UNREFERENCED_PARAMETER(adapter);
    UNREFERENCED_PARAMETER(input);
    return STATUS_SUCCESS;
}

NTSTATUS ParseMonitorDescription(
    const IDARG_IN_PARSEMONITORDESCRIPTION* input,
    IDARG_OUT_PARSEMONITORDESCRIPTION* output)
{
    UNREFERENCED_PARAMETER(input);
    UNREFERENCED_PARAMETER(output);
    return STATUS_INVALID_PARAMETER;
}

NTSTATUS GetDefaultMonitorModes(
    IDDCX_MONITOR monitor,
    const IDARG_IN_GETDEFAULTDESCRIPTIONMODES* input,
    IDARG_OUT_GETDEFAULTDESCRIPTIONMODES* output)
{
    UNREFERENCED_PARAMETER(monitor);
    output->DefaultMonitorModeBufferOutputCount = 1;
    if (input->DefaultMonitorModeBufferInputCount == 0)
    {
        return STATUS_SUCCESS;
    }
    if (input->DefaultMonitorModeBufferInputCount < 1)
    {
        return STATUS_BUFFER_TOO_SMALL;
    }

    input->pDefaultMonitorModes[0] =
        MonitorMode(GetMonitorState(monitor)->mode, IDDCX_MONITOR_MODE_ORIGIN_DRIVER);
    output->PreferredMonitorModeIdx = 0;
    return STATUS_SUCCESS;
}

NTSTATUS QueryTargetModes(
    IDDCX_MONITOR monitor,
    const IDARG_IN_QUERYTARGETMODES* input,
    IDARG_OUT_QUERYTARGETMODES* output)
{
    UNREFERENCED_PARAMETER(monitor);
    output->TargetModeBufferOutputCount = 1;
    if (input->TargetModeBufferInputCount >= 1)
    {
        input->pTargetModes[0] = TargetMode(GetMonitorState(monitor)->mode);
    }
    return STATUS_SUCCESS;
}

NTSTATUS AssignSwapChain(
    IDDCX_MONITOR monitor,
    const IDARG_IN_SETSWAPCHAIN* input)
{
    MonitorState* state = GetMonitorState(monitor);
    StopDrain(state);
    if (!StartDrain(
            state,
            input->hSwapChain,
            input->RenderAdapterLuid,
            input->hNextSurfaceAvailable))
    {
        WdfObjectDelete(input->hSwapChain);
    }
    return STATUS_SUCCESS;
}

NTSTATUS UnassignSwapChain(IDDCX_MONITOR monitor)
{
    StopDrain(GetMonitorState(monitor));
    return STATUS_SUCCESS;
}

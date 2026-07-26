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
constexpr UINT kWidth = 1920;
constexpr UINT kHeight = 1080;
constexpr UINT kRefreshRate = 60;

// One permanent monitor identity. Changing either value makes Windows treat the
// virtual monitor as new hardware and loses its saved desktop placement.
constexpr GUID kMonitorContainerId =
    {0x67453031, 0x7ba9, 0x4d45, {0x8f, 0x0b, 0x72, 0x1d, 0xb4, 0x61, 0x62, 0x42}};

// EDID 1.4: SBMS Display, serial SBMS00000001, one 1920x1080@60 detailed mode.
constexpr BYTE kEdid[128] = {
    0x00,0xff,0xff,0xff,0xff,0xff,0xff,0x00,0x4c,0x4d,0x01,0x00,0x01,0x00,0x00,0x00,
    0x01,0x24,0x01,0x04,0xa5,0x34,0x1d,0x78,0x0a,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
    0x00,0x00,0x00,0x00,0x00,0x00,0x01,0x01,0x01,0x01,0x01,0x01,0x01,0x01,0x01,0x01,
    0x01,0x01,0x01,0x01,0x01,0x01,0x02,0x3a,0x80,0x18,0x71,0x38,0x2d,0x40,0x58,0x2c,
    0x45,0x00,0xfd,0x1e,0x11,0x00,0x00,0x1a,0x00,0x00,0x00,0xff,0x00,0x53,0x42,0x4d,
    0x53,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x31,0x0a,0x00,0x00,0x00,0xfc,0x00,0x53,
    0x42,0x4d,0x53,0x20,0x44,0x69,0x73,0x70,0x6c,0x61,0x79,0x0a,0x00,0x00,0x00,0xfe,
    0x00,0x53,0x42,0x4d,0x53,0x20,0x49,0x44,0x44,0x0a,0x20,0x20,0x20,0x20,0x00,0x22
};

struct Drain
{
    IDDCX_SWAPCHAIN swapChain{};
    ID3D11Device* d3dDevice{};
    HANDLE frameAvailable{};
    HANDLE stop{};
    HANDLE thread{};
};

struct DeviceState
{
    WDFDEVICE device{};
    IDDCX_ADAPTER adapter{};
};

struct MonitorState
{
    Drain* drain{};
};

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DeviceState, GetDeviceState);
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(MonitorState, GetMonitorState);

void FillSignal(DISPLAYCONFIG_VIDEO_SIGNAL_INFO& signal, bool monitorMode)
{
    signal.activeSize.cx = kWidth;
    signal.activeSize.cy = kHeight;
    signal.totalSize.cx = 2200;
    signal.totalSize.cy = 1125;
    signal.vSyncFreq.Numerator = kRefreshRate;
    signal.vSyncFreq.Denominator = 1;
    signal.hSyncFreq.Numerator = 67500;
    signal.hSyncFreq.Denominator = 1;
    signal.pixelRate = 148500000;
    signal.scanLineOrdering = DISPLAYCONFIG_SCANLINE_ORDERING_PROGRESSIVE;
    signal.AdditionalSignalInfo.videoStandard = 255;
    signal.AdditionalSignalInfo.vSyncFreqDivider = monitorMode ? 0 : 1;
}

IDDCX_MONITOR_MODE MonitorMode(IDDCX_MONITOR_MODE_ORIGIN origin)
{
    IDDCX_MONITOR_MODE mode{};
    mode.Size = sizeof(mode);
    mode.Origin = origin;
    FillSignal(mode.MonitorVideoSignalInfo, true);
    return mode;
}

IDDCX_TARGET_MODE TargetMode()
{
    IDDCX_TARGET_MODE mode{};
    mode.Size = sizeof(mode);
    FillSignal(mode.TargetVideoSignalInfo.targetVideoSignalInfo, false);
    return mode;
}

DWORD WINAPI DrainFrames(void* argument)
{
    auto* drain = static_cast<Drain*>(argument);

    for (;;)
    {
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

        // This first driver deliberately transports no pixels. Acquiring and
        // releasing every surface is still mandatory or DWM will stall.
        if (buffer.MetaData.pSurface != nullptr)
        {
            buffer.MetaData.pSurface->Release();
        }

        if (FAILED(IddCxSwapChainFinishedProcessingFrame(drain->swapChain)))
        {
            break;
        }
    }

    WdfObjectDelete(drain->swapChain);
    drain->swapChain = nullptr;
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
        WaitForSingleObject(drain->thread, INFINITE);
        CloseHandle(drain->thread);
    }
    if (drain->stop != nullptr)
    {
        CloseHandle(drain->stop);
    }
    if (drain->d3dDevice != nullptr)
    {
        drain->d3dDevice->Release();
    }
    delete drain;
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

    if (d3dContext != nullptr) d3dContext->Release();
    if (dxgiDevice != nullptr) dxgiDevice->Release();
    if (adapter != nullptr) adapter->Release();
    if (factory != nullptr) factory->Release();

    if (FAILED(result))
    {
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
    drain->frameAvailable = frameAvailable;
    drain->stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (drain->stop != nullptr)
    {
        drain->thread = CreateThread(nullptr, 0, DrainFrames, drain, 0, nullptr);
    }
    if (drain->stop == nullptr || drain->thread == nullptr)
    {
        if (drain->stop != nullptr) CloseHandle(drain->stop);
        d3dDevice->Release();
        delete drain;
        return false;
    }

    monitor->drain = drain;
    return true;
}

void ReportMonitor(DeviceState* state)
{
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
    info.MonitorDescription.DataSize = sizeof(kEdid);
    info.MonitorDescription.pData = const_cast<BYTE*>(kEdid);

    IDARG_IN_MONITORCREATE input{};
    input.ObjectAttributes = &attributes;
    input.pMonitorInfo = &info;
    IDARG_OUT_MONITORCREATE output{};
    if (!NT_SUCCESS(IddCxMonitorCreate(state->adapter, &input, &output)))
    {
        return;
    }

    IDARG_OUT_MONITORARRIVAL arrival{};
    if (!NT_SUCCESS(IddCxMonitorArrival(output.MonitorObject, &arrival)))
    {
        WdfObjectDelete(output.MonitorObject);
    }
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
        ReportMonitor(GetDeviceState(adapter));
    }
    return STATUS_SUCCESS;
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
    if (input->MonitorDescription.Type != IDDCX_MONITOR_DESCRIPTION_TYPE_EDID ||
        input->MonitorDescription.DataSize != sizeof(kEdid) ||
        memcmp(input->MonitorDescription.pData, kEdid, sizeof(kEdid)) != 0)
    {
        return STATUS_INVALID_PARAMETER;
    }

    output->MonitorModeBufferOutputCount = 1;
    if (input->MonitorModeBufferInputCount == 0)
    {
        return STATUS_SUCCESS;
    }
    if (input->MonitorModeBufferInputCount < 1)
    {
        return STATUS_BUFFER_TOO_SMALL;
    }

    input->pMonitorModes[0] =
        MonitorMode(IDDCX_MONITOR_MODE_ORIGIN_MONITORDESCRIPTOR);
    output->PreferredMonitorModeIdx = 0;
    return STATUS_SUCCESS;
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

    input->pDefaultMonitorModes[0] = MonitorMode(IDDCX_MONITOR_MODE_ORIGIN_DRIVER);
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
        input->pTargetModes[0] = TargetMode();
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

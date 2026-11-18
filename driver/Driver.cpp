#define NOMINMAX

#include <windows.h>
#include <wdf.h>
#include <iddcx.h>
#include <dxgi1_5.h>
#include <d3d11.h>
#include <new>

#ifdef SBMS_CAPTURE_DIAGNOSTICS
#include "CaptureDiagnostics.h"
EVT_WDF_OBJECT_CONTEXT_CLEANUP DeviceCleanup;
#endif

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
constexpr UINT kMaximumMonitors = 16;
#ifdef SBMS_CAPTURE_DIAGNOSTICS
constexpr wchar_t kSessionGate[] = L"Global\\SBMSCaptureSession-v8";
using CaptureDiagnostics::Kind;
using CaptureDiagnostics::Stage;
constexpr UINT kGateMagic = 0x53424738;
constexpr UINT kProtocolVersion = 8;
#else
constexpr wchar_t kSessionGate[] = L"Global\\SBMSSession-v9";
constexpr UINT kGateMagic = 0x53424739;
constexpr UINT kProtocolVersion = 9;
#endif

struct GateHeader
{
    UINT magic;
    UINT version;
    UINT count;
    UINT reserved;
};

struct GateEntry
{
    UINT connectorIndex;
    UINT width;
    UINT height;
    UINT refreshNumerator;
    UINT refreshDenominator;
};

struct GateIdentity
{
    volatile LONG sequence;
    ULONG adapterLow;
    LONG adapterHigh;
    UINT targetId;
};

#ifndef SBMS_CAPTURE_DIAGNOSTICS
struct GateRenderAdapter
{
    volatile LONG sequence;
    ULONG adapterLow;
    LONG adapterHigh;
    UINT active;
};
#endif

struct GateConfig
{
    GateHeader header;
    GateEntry entries[kMaximumMonitors];
    GateIdentity identities[kMaximumMonitors];
#ifndef SBMS_CAPTURE_DIAGNOSTICS
    LUID preferredAdapter;
    GateRenderAdapter renderAdapters[kMaximumMonitors];
#endif
};

static_assert(sizeof(GateHeader) == 16);
static_assert(sizeof(GateEntry) == 20);
static_assert(sizeof(GateIdentity) == 16);
#ifdef SBMS_CAPTURE_DIAGNOSTICS
static_assert(sizeof(GateConfig) == 592);
#else
static_assert(sizeof(GateRenderAdapter) == 16);
static_assert(sizeof(GateConfig) == 856);
#endif

ULONG BeginPublication(volatile LONG& sequence)
{
    const ULONG previousSequence = static_cast<ULONG>(
        InterlockedCompareExchange(&sequence, 0, 0));
    const ULONG writingSequence = (previousSequence | 1u) + 2u;
    InterlockedExchange(&sequence, static_cast<LONG>(writingSequence));
    return writingSequence;
}

struct ModeConfig
{
    UINT connectorIndex;
    UINT width;
    UINT height;
    UINT refreshNumerator;
    UINT refreshDenominator;
};

// One permanent monitor identity. Changing either value makes Windows treat the
// virtual monitor as new hardware and loses its saved desktop placement.
constexpr GUID kMonitorContainerId =
#ifdef SBMS_CAPTURE_DIAGNOSTICS
    {0x8c971f73, 0x59c5, 0x42a4, {0xb8, 0xa5, 0x13, 0xd3, 0x51, 0x50, 0x0b, 0x46}};
#else
    {0x67453031, 0x7ba9, 0x4d45, {0x8f, 0x0b, 0x72, 0x1d, 0xb4, 0x61, 0x62, 0x42}};
#endif

GUID MonitorContainerId(UINT connectorIndex)
{
    GUID id = kMonitorContainerId;
    id.Data1 ^= connectorIndex;
    return id;
}

struct Drain
{
    IDDCX_SWAPCHAIN swapChain{};
    ID3D11Device* d3dDevice{};
    HANDLE frameAvailable{};
    HANDLE stop{};
    HANDLE thread{};
    volatile LONG ownership{};
#ifdef SBMS_CAPTURE_DIAGNOSTICS
    CaptureDiagnostics::Session* diagnostics{};
    UINT connector{};
    UINT64 generation{};
#endif
};

struct DeviceState
{
    WDFDEVICE device{};
    IDDCX_ADAPTER adapter{};
#ifdef SBMS_CAPTURE_DIAGNOSTICS
    CaptureDiagnostics::Session* diagnostics{};
    bool ownsDiagnostics{};
#endif
};

struct MonitorState
{
    Drain* drain{};
    ModeConfig mode{};
#ifdef SBMS_CAPTURE_DIAGNOSTICS
    CaptureDiagnostics::Session* diagnostics{};
    UINT64 generation{};
#else
    // Keep the original session mapped; late callbacks must never reopen a newer session.
    GateConfig* gate{};
#endif
};

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DeviceState, GetDeviceState);
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(MonitorState, GetMonitorState);

#ifndef SBMS_CAPTURE_DIAGNOSTICS
void PublishRenderAdapter(MonitorState* monitor, const LUID* adapter)
{
    if (monitor->gate == nullptr) return;
    auto& receipt = monitor->gate->renderAdapters[monitor->mode.connectorIndex];
    const ULONG writingSequence = BeginPublication(receipt.sequence);
    receipt.adapterLow = adapter != nullptr ? adapter->LowPart : 0;
    receipt.adapterHigh = adapter != nullptr ? adapter->HighPart : 0;
    receipt.active = adapter != nullptr ? 1u : 0u;
    InterlockedExchange(&receipt.sequence, static_cast<LONG>(writingSequence + 1u));
}
#endif

bool ValidateMode(
    UINT connectorIndex,
    UINT width,
    UINT height,
    UINT refreshNumerator,
    UINT refreshDenominator,
    ModeConfig& mode)
{
    if (connectorIndex >= kMaximumMonitors ||
        width == 0 || height == 0 ||
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

    mode.connectorIndex = connectorIndex;
    mode.width = width;
    mode.height = height;
    mode.refreshNumerator = refreshNumerator;
    mode.refreshDenominator = refreshDenominator;
    return true;
}

bool ReadSessionConfig(
    const GateConfig& config,
    ModeConfig (&modes)[kMaximumMonitors],
    UINT& count)
{
    bool valid = false;
    if (config.header.magic == kGateMagic &&
        config.header.version == kProtocolVersion &&
        config.header.count > 0 &&
        config.header.count <= kMaximumMonitors &&
        config.header.reserved <= 1)
    {
        valid = true;
        UINT connectorMask = 0;
        for (UINT index = 0; index < config.header.count; ++index)
        {
            const GateEntry& entry = config.entries[index];
            if (entry.connectorIndex >= kMaximumMonitors)
            {
                valid = false;
                break;
            }
            const UINT connectorBit = 1u << entry.connectorIndex;
            if ((connectorMask & connectorBit) != 0 ||
                !ValidateMode(
                    entry.connectorIndex,
                    entry.width,
                    entry.height,
                    entry.refreshNumerator,
                    entry.refreshDenominator,
                    modes[index]))
            {
                valid = false;
                break;
            }
            connectorMask |= connectorBit;
        }
        if (valid)
        {
            count = config.header.count;
        }
    }
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
    if (drain->d3dDevice != nullptr) drain->d3dDevice->Release();
#ifdef SBMS_CAPTURE_DIAGNOSTICS
    drain->diagnostics->Release();
#endif
    delete drain;
}

DWORD WINAPI DrainFrames(void* argument)
{
    auto* drain = static_cast<Drain*>(argument);
#ifdef SBMS_CAPTURE_DIAGNOSTICS
    auto* diagnostics = drain->diagnostics;
    diagnostics->Record(Kind::DrainStart, drain->connector, drain->generation);
    bool firstFrame = true;
#endif

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
#ifdef SBMS_CAPTURE_DIAGNOSTICS
            if (wait != WAIT_OBJECT_0 + 1)
            {
                diagnostics->Record(Kind::Error, drain->connector, drain->generation,
                    HRESULT_FROM_WIN32(GetLastError()), Stage::WaitFrame, {}, wait);
            }
#endif
            break;
        }

        if (FAILED(result))
        {
#ifdef SBMS_CAPTURE_DIAGNOSTICS
            diagnostics->Record(Kind::Error, drain->connector, drain->generation,
                result, Stage::Acquire);
#endif
            break;
        }

        if (buffer.MetaData.pSurface != nullptr)
        {
            buffer.MetaData.pSurface->Release();
        }

        const HRESULT finishResult = IddCxSwapChainFinishedProcessingFrame(drain->swapChain);
        if (FAILED(finishResult))
        {
#ifdef SBMS_CAPTURE_DIAGNOSTICS
            diagnostics->Record(Kind::Error, drain->connector, drain->generation,
                finishResult, Stage::FinishFrame);
#endif
            break;
        }
#ifdef SBMS_CAPTURE_DIAGNOSTICS
        if (firstFrame)
        {
            diagnostics->Record(Kind::FirstFrame, drain->connector, drain->generation);
            firstFrame = false;
        }
#endif
    }

    WdfObjectDelete(drain->swapChain);
    drain->swapChain = nullptr;
#ifdef SBMS_CAPTURE_DIAGNOSTICS
    diagnostics->Record(Kind::DrainStopped, drain->connector, drain->generation);
#endif
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
#ifdef SBMS_CAPTURE_DIAGNOSTICS
    drain->diagnostics->Record(Kind::StopRequested, drain->connector, drain->generation);
#endif
    SetEvent(drain->stop);
    if (drain->thread != nullptr)
    {
        const DWORD wait = WaitForSingleObject(drain->thread, 5000);
        if (wait != WAIT_OBJECT_0)
        {
#ifdef SBMS_CAPTURE_DIAGNOSTICS
            const LONG result = HRESULT_FROM_WIN32(wait == WAIT_TIMEOUT ? WAIT_TIMEOUT : GetLastError());
            drain->diagnostics->Record(wait == WAIT_TIMEOUT ? Kind::StopTimeout : Kind::Error,
                drain->connector, drain->generation, result, Stage::Thread, {}, wait);
#endif
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
#ifndef SBMS_CAPTURE_DIAGNOSTICS
    LUID actualRenderAdapter{};
    bool actualAdapterKnown = false;
#endif
#ifdef SBMS_CAPTURE_DIAGNOSTICS
    auto* diagnostics = monitor->diagnostics;
    const UINT connector = monitor->mode.connectorIndex;
    const UINT64 generation = monitor->generation;
    Stage stage = Stage::Factory;
#endif

    HRESULT result = CreateDXGIFactory2(0, IID_PPV_ARGS(&factory));
    if (SUCCEEDED(result))
    {
#ifdef SBMS_CAPTURE_DIAGNOSTICS
        stage = Stage::EnumAdapter;
#endif
        result = factory->EnumAdapterByLuid(renderAdapter, IID_PPV_ARGS(&adapter));
    }
    if (SUCCEEDED(result))
    {
#ifdef SBMS_CAPTURE_DIAGNOSTICS
        stage = Stage::D3DCreate;
#endif
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
            nullptr);
#ifdef SBMS_CAPTURE_DIAGNOSTICS
        diagnostics->Record(Kind::D3DDevice, connector, generation, result, stage);
#endif
    }
    if (SUCCEEDED(result))
    {
#ifdef SBMS_CAPTURE_DIAGNOSTICS
        stage = Stage::QueryDevice;
#endif
        result = d3dDevice->QueryInterface(IID_PPV_ARGS(&dxgiDevice));
    }
    if (SUCCEEDED(result))
    {
        IDXGIAdapter* actualAdapter = nullptr;
        DXGI_ADAPTER_DESC description{};
        HRESULT actualResult = dxgiDevice->GetAdapter(&actualAdapter);
        if (SUCCEEDED(actualResult)) actualResult = actualAdapter->GetDesc(&description);
#ifdef SBMS_CAPTURE_DIAGNOSTICS
        diagnostics->Record(Kind::ActualAdapter, connector, generation, actualResult,
            Stage::ActualAdapter, description.AdapterLuid);
        stage = Stage::SetDevice;
#else
        actualAdapterKnown = SUCCEEDED(actualResult);
        actualRenderAdapter = description.AdapterLuid;
#endif
        if (actualAdapter != nullptr) actualAdapter->Release();
        IDARG_IN_SWAPCHAINSETDEVICE setDevice{};
        setDevice.pDevice = dxgiDevice;
        result = IddCxSwapChainSetDevice(swapChain, &setDevice);
#ifdef SBMS_CAPTURE_DIAGNOSTICS
        diagnostics->Record(Kind::SetDevice, connector, generation, result, stage);
#endif
    }

    if (dxgiDevice != nullptr) dxgiDevice->Release();
    if (adapter != nullptr) adapter->Release();
    if (factory != nullptr) factory->Release();

    if (FAILED(result))
    {
#ifdef SBMS_CAPTURE_DIAGNOSTICS
        diagnostics->Record(Kind::Error, connector, generation, result, stage);
#endif
        if (d3dDevice != nullptr) d3dDevice->Release();
        return false;
    }

    auto* drain = new (std::nothrow) Drain{};
    if (drain == nullptr)
    {
#ifdef SBMS_CAPTURE_DIAGNOSTICS
        diagnostics->Record(Kind::Error, connector, generation, E_OUTOFMEMORY, Stage::Allocate);
#endif
        d3dDevice->Release();
        return false;
    }

    drain->swapChain = swapChain;
    drain->d3dDevice = d3dDevice;
    drain->frameAvailable = frameAvailable;
#ifdef SBMS_CAPTURE_DIAGNOSTICS
    drain->diagnostics = diagnostics;
    diagnostics->AddRef();
    drain->connector = connector;
    drain->generation = generation;
#endif
    drain->stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (drain->stop != nullptr)
    {
        drain->thread = CreateThread(nullptr, 0, DrainFrames, drain, 0, nullptr);
    }
    if (drain->thread == nullptr)
    {
#ifdef SBMS_CAPTURE_DIAGNOSTICS
        diagnostics->Record(Kind::Error, connector, generation, HRESULT_FROM_WIN32(GetLastError()),
            drain->stop != nullptr ? Stage::Thread : Stage::StopEvent);
        diagnostics->Release();
#endif
        if (drain->stop != nullptr) CloseHandle(drain->stop);
        d3dDevice->Release();
        delete drain;
        return false;
    }

    monitor->drain = drain;
#ifndef SBMS_CAPTURE_DIAGNOSTICS
    PublishRenderAdapter(monitor, actualAdapterKnown ? &actualRenderAdapter : nullptr);
#endif
    return true;
}

NTSTATUS ReportMonitor(DeviceState* state, const ModeConfig& mode, GateConfig& config, HANDLE gate)
{
#ifdef SBMS_CAPTURE_DIAGNOSTICS
    UNREFERENCED_PARAMETER(gate);
#endif
    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, MonitorState);
    attributes.EvtCleanupCallback = MonitorCleanup;

    IDDCX_MONITOR_INFO info{};
    info.Size = sizeof(info);
    info.MonitorType = DISPLAYCONFIG_OUTPUT_TECHNOLOGY_OTHER;
    info.ConnectorIndex = mode.connectorIndex;
    info.MonitorContainerId = MonitorContainerId(mode.connectorIndex);
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
#ifdef SBMS_CAPTURE_DIAGNOSTICS
    state->diagnostics->Record(Kind::MonitorCreate, mode.connectorIndex, 0,
        createStatus, Stage::MonitorCreate);
#endif
    if (!NT_SUCCESS(createStatus))
    {
        return createStatus;
    }

    MonitorState* monitorState = GetMonitorState(output.MonitorObject);
    monitorState->mode = mode;
#ifdef SBMS_CAPTURE_DIAGNOSTICS
    monitorState->diagnostics = state->diagnostics;
    state->diagnostics->AddRef();
#else
    monitorState->gate = static_cast<GateConfig*>(
        MapViewOfFile(gate, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(GateConfig)));
    if (monitorState->gate == nullptr)
    {
        WdfObjectDelete(output.MonitorObject);
        return STATUS_INSUFFICIENT_RESOURCES;
    }
#endif

    IDARG_OUT_MONITORARRIVAL arrival{};
    const NTSTATUS arrivalStatus =
        IddCxMonitorArrival(output.MonitorObject, &arrival);
#ifdef SBMS_CAPTURE_DIAGNOSTICS
    state->diagnostics->Record(Kind::MonitorArrival, mode.connectorIndex, 0,
        arrivalStatus, Stage::MonitorArrival, arrival.OsAdapterLuid, arrival.OsTargetId);
#endif
    if (!NT_SUCCESS(arrivalStatus))
    {
        WdfObjectDelete(output.MonitorObject);
        return arrivalStatus;
    }

    // An odd sequence marks a write; matching nonzero even reads form one
    // snapshot. Advance past an interrupted write when restarting the driver.
    GateIdentity& identity = config.identities[mode.connectorIndex];
    const ULONG writingSequence = BeginPublication(identity.sequence);
    identity.adapterLow = arrival.OsAdapterLuid.LowPart;
    identity.adapterHigh = arrival.OsAdapterLuid.HighPart;
    identity.targetId = arrival.OsTargetId;
    InterlockedExchange(&identity.sequence, static_cast<LONG>(writingSequence + 1u));

    return STATUS_SUCCESS;
}

NTSTATUS ReportMonitors(DeviceState* state)
{
    HANDLE gate = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, kSessionGate);
    if (gate == nullptr)
    {
#ifdef SBMS_CAPTURE_DIAGNOSTICS
        state->diagnostics->Record(Kind::Error, CaptureDiagnostics::kNoConnector, 0,
            HRESULT_FROM_WIN32(GetLastError()), Stage::SessionConfig);
#endif
        return STATUS_INVALID_PARAMETER;
    }
    auto* config = static_cast<GateConfig*>(
        MapViewOfFile(gate, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(GateConfig)));
    if (config == nullptr)
    {
#ifdef SBMS_CAPTURE_DIAGNOSTICS
        state->diagnostics->Record(Kind::Error, CaptureDiagnostics::kNoConnector, 0,
            HRESULT_FROM_WIN32(GetLastError()), Stage::SessionConfig);
#endif
        CloseHandle(gate);
        return STATUS_INVALID_PARAMETER;
    }

    // Keep this exact session mapping alive through all arrivals. Reopening by
    // name per monitor could publish an older adapter's identities into a new session.
    ModeConfig modes[kMaximumMonitors]{};
    UINT count = 0;
    NTSTATUS result = STATUS_INVALID_PARAMETER;
    if (ReadSessionConfig(*config, modes, count))
    {
#ifndef SBMS_CAPTURE_DIAGNOSTICS
        if (config->header.reserved == 1)
        {
            IDARG_IN_ADAPTERSETRENDERADAPTER preference{};
            preference.PreferredRenderAdapter = config->preferredAdapter;
            IddCxAdapterSetRenderAdapter(state->adapter, &preference);
        }
#endif
        // A restarted adapter may reuse OS target IDs. Invalidate every old
        // receipt before any new monitor can arrive and be mistaken for it.
        for (UINT index = 0; index < count; ++index)
        {
            const UINT connector = modes[index].connectorIndex;
            BeginPublication(config->identities[connector].sequence);
#ifndef SBMS_CAPTURE_DIAGNOSTICS
            BeginPublication(config->renderAdapters[connector].sequence);
#endif
        }
        result = STATUS_SUCCESS;
        for (UINT index = 0; index < count; ++index)
        {
            result = ReportMonitor(state, modes[index], *config, gate);
            if (!NT_SUCCESS(result))
            {
                break;
            }
        }
    }
    UnmapViewOfFile(config);
    CloseHandle(gate);
#ifdef SBMS_CAPTURE_DIAGNOSTICS
    if (!NT_SUCCESS(result))
        state->diagnostics->Record(Kind::Error, CaptureDiagnostics::kNoConnector, 0,
            result, Stage::SessionConfig);
#endif
    return result;
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
    caps.MaxMonitorsSupported = kMaximumMonitors;
    caps.EndPointDiagnostics.Size = sizeof(caps.EndPointDiagnostics);
    caps.EndPointDiagnostics.GammaSupport = IDDCX_FEATURE_IMPLEMENTATION_NONE;
    caps.EndPointDiagnostics.TransmissionType = IDDCX_TRANSMISSION_TYPE_WIRED_OTHER;
#ifdef SBMS_CAPTURE_DIAGNOSTICS
    caps.EndPointDiagnostics.pEndPointFriendlyName = L"SBMS Capture Diagnostics";
#else
    caps.EndPointDiagnostics.pEndPointFriendlyName = L"SBMS Display";
#endif
    caps.EndPointDiagnostics.pEndPointManufacturerName = L"SBMS";
    caps.EndPointDiagnostics.pEndPointModelName = L"SBMS IDD";
    caps.EndPointDiagnostics.pFirmwareVersion = &version;
    caps.EndPointDiagnostics.pHardwareVersion = &version;

    WDF_OBJECT_ATTRIBUTES attributes;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&attributes, DeviceState);
#ifdef SBMS_CAPTURE_DIAGNOSTICS
    attributes.EvtCleanupCallback = DeviceCleanup;
#endif

    IDARG_IN_ADAPTER_INIT input{};
    input.WdfDevice = state->device;
    input.pCaps = &caps;
    input.ObjectAttributes = &attributes;
    IDARG_OUT_ADAPTER_INIT output{};
    const NTSTATUS status = IddCxAdapterInitAsync(&input, &output);
#ifdef SBMS_CAPTURE_DIAGNOSTICS
    state->diagnostics->Record(Kind::AdapterInit, CaptureDiagnostics::kNoConnector, 0,
        status, Stage::AdapterInit);
#endif
    if (NT_SUCCESS(status))
    {
        state->adapter = output.AdapterObject;
        DeviceState* adapterState = GetDeviceState(output.AdapterObject);
        adapterState->device = state->device;
        adapterState->adapter = output.AdapterObject;
#ifdef SBMS_CAPTURE_DIAGNOSTICS
        adapterState->diagnostics = state->diagnostics;
        state->diagnostics->AddRef();
#endif
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
#ifdef SBMS_CAPTURE_DIAGNOSTICS
    attributes.EvtCleanupCallback = DeviceCleanup;
#endif
    WDFDEVICE device = nullptr;
    status = WdfDeviceCreate(&deviceInit, &attributes, &device);
    if (!NT_SUCCESS(status))
    {
        return status;
    }

    DeviceState* state = GetDeviceState(device);
    state->device = device;
#ifdef SBMS_CAPTURE_DIAGNOSTICS
    state->diagnostics = CaptureDiagnostics::Session::Open();
    if (state->diagnostics == nullptr) return STATUS_INVALID_PARAMETER;
    state->ownsDiagnostics = true;
#endif
    status = IddCxDeviceInitialize(device);
#ifdef SBMS_CAPTURE_DIAGNOSTICS
    if (!NT_SUCCESS(status))
        state->diagnostics->Record(Kind::Error, CaptureDiagnostics::kNoConnector, 0,
            status, Stage::DeviceInitialize);
#endif
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
    auto* state = GetMonitorState(object);
#ifdef SBMS_CAPTURE_DIAGNOSTICS
    if (state->diagnostics != nullptr)
        state->diagnostics->Record(Kind::MonitorCleanup, state->mode.connectorIndex, state->generation);
#else
    PublishRenderAdapter(state, nullptr);
#endif
    StopDrain(state);
#ifdef SBMS_CAPTURE_DIAGNOSTICS
    if (state->diagnostics != nullptr) state->diagnostics->Release();
#else
    if (state->gate != nullptr) UnmapViewOfFile(state->gate);
#endif
}

#ifdef SBMS_CAPTURE_DIAGNOSTICS
void DeviceCleanup(WDFOBJECT object)
{
    auto* state = GetDeviceState(object);
    if (state->diagnostics == nullptr) return;
    if (state->ownsDiagnostics) state->diagnostics->Record(Kind::DeviceDetach);
    state->diagnostics->Release();
}
#endif

NTSTATUS AdapterInitFinished(
    IDDCX_ADAPTER adapter,
    const IDARG_IN_ADAPTER_INIT_FINISHED* input)
{
#ifdef SBMS_CAPTURE_DIAGNOSTICS
    auto* state = GetDeviceState(adapter);
    state->diagnostics->Record(Kind::AdapterInit, CaptureDiagnostics::kNoConnector, 0,
        input->AdapterInitStatus, Stage::AdapterInit, {}, 1);
#endif
    if (NT_SUCCESS(input->AdapterInitStatus))
    {
#ifdef SBMS_CAPTURE_DIAGNOSTICS
        LUID requested{};
        const bool explicitAdapter = state->diagnostics->RequestedAdapter(requested);
        if (explicitAdapter)
        {
            IDARG_IN_ADAPTERSETRENDERADAPTER preference{};
            preference.PreferredRenderAdapter = requested;
            IddCxAdapterSetRenderAdapter(adapter, &preference);
        }
        // This API is void and expresses a preference. AssignSwapChain records
        // what the OS actually selected, separately from this requested LUID.
        state->diagnostics->Record(Kind::RenderPreference, CaptureDiagnostics::kNoConnector,
            0, 0, Stage::None, requested, explicitAdapter ? 1 : 0);
#endif
        return ReportMonitors(GetDeviceState(adapter));
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
#ifndef SBMS_CAPTURE_DIAGNOSTICS
    PublishRenderAdapter(state, nullptr);
#endif
    StopDrain(state);
#ifdef SBMS_CAPTURE_DIAGNOSTICS
    state->generation = state->diagnostics->Record(Kind::Assign,
        state->mode.connectorIndex, 0, 0, Stage::None, input->RenderAdapterLuid);
#endif
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
    auto* state = GetMonitorState(monitor);
#ifdef SBMS_CAPTURE_DIAGNOSTICS
    state->diagnostics->Record(Kind::Unassign, state->mode.connectorIndex, state->generation);
#else
    PublishRenderAdapter(state, nullptr);
#endif
    StopDrain(state);
    return STATUS_SUCCESS;
}

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
constexpr UINT kMaximumMonitors = 16;
constexpr wchar_t kSessionGate[] = L"Global\\SBMSSession-v8";
constexpr UINT kGateMagic = 0x53424738;
constexpr UINT kProtocolVersion = 8;

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

struct GateConfig
{
    GateHeader header;
    GateEntry entries[kMaximumMonitors];
    GateIdentity identities[kMaximumMonitors];
};

static_assert(sizeof(GateHeader) == 16);
static_assert(sizeof(GateEntry) == 20);
static_assert(sizeof(GateIdentity) == 16);
static_assert(sizeof(GateConfig) == 592);

// Optional, bounded lifecycle evidence. The existing v8 gate ABI is unchanged.
constexpr wchar_t kDriverDiagnostics[] = L"Global\\SBMSDriverDiagnostics-v1";
constexpr UINT kDiagnosticMagic = 0x53424431;
constexpr UINT kDiagnosticCapacity = 64;

enum class DiagnosticStage : UINT
{
    MonitorReady = 1, Assign, Unassign, CreateFactory, EnumAdapter,
    CreateDevice, QueryDevice, SetDevice, AllocateDrain, CreateStop,
    CreateWorker, DrainStarted, FirstFrame, DrainExit, StopWait, DeviceRemovedReason,
    SignalStop
};

struct DiagnosticRecord
{
    volatile LONG publication;
    ULONG sequence;
    ULONG tickLow;
    ULONG tickHigh;
    ULONG driverPid;
    UINT connectorIndex;
    ULONG assignment;
    UINT stage;
    ULONG adapterLow;
    LONG adapterHigh;
    HRESULT result;
    UINT detail;
};

struct DiagnosticBuffer
{
    UINT magic;
    UINT version;
    UINT capacity;
    ULONG ownerPid;
    ULONG sessionTickLow;
    ULONG sessionTickHigh;
    volatile LONG writerLock;
    volatile LONG nextSequence;
    volatile LONG dropped;
    DiagnosticRecord records[kDiagnosticCapacity];
};

static_assert(sizeof(DiagnosticRecord) == 48);
static_assert(sizeof(DiagnosticBuffer) == 3108);

struct DriverDiagnostics
{
    HANDLE mapping{};
    DiagnosticBuffer* buffer{};
    volatile LONG references{1};
};

void RetainDiagnostics(DriverDiagnostics* diagnostics)
{
    if (diagnostics != nullptr) InterlockedIncrement(&diagnostics->references);
}

void ReleaseDiagnostics(DriverDiagnostics* diagnostics)
{
    if (diagnostics != nullptr && InterlockedDecrement(&diagnostics->references) == 0)
    {
        UnmapViewOfFile(diagnostics->buffer);
        CloseHandle(diagnostics->mapping);
        delete diagnostics;
    }
}

DriverDiagnostics* OpenDriverDiagnostics()
{
    HANDLE mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, kDriverDiagnostics);
    if (mapping == nullptr) return nullptr;
    auto* buffer = static_cast<DiagnosticBuffer*>(
        MapViewOfFile(mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(DiagnosticBuffer)));
    if (buffer == nullptr || buffer->magic != kDiagnosticMagic ||
        buffer->version != 1 || buffer->capacity != kDiagnosticCapacity)
    {
        if (buffer != nullptr) UnmapViewOfFile(buffer);
        CloseHandle(mapping);
        return nullptr;
    }
    auto* diagnostics = new (std::nothrow) DriverDiagnostics{};
    if (diagnostics == nullptr)
    {
        UnmapViewOfFile(buffer);
        CloseHandle(mapping);
        return nullptr;
    }
    diagnostics->mapping = mapping;
    diagnostics->buffer = buffer;
    return diagnostics;
}

void RecordDiagnostic(
    DriverDiagnostics* diagnostics, UINT connector, ULONG assignment,
    DiagnosticStage stage, LUID adapter, HRESULT result = S_OK, UINT detail = 0)
{
    if (diagnostics == nullptr) return;
    DiagnosticBuffer* buffer = diagnostics->buffer;
    // Never wait on diagnostics in an IddCx callback or frame worker. Report
    // contention explicitly; the host also reports overwritten ring records.
    if (InterlockedCompareExchange(&buffer->writerLock, 1, 0) != 0)
    {
        InterlockedIncrement(&buffer->dropped);
        return;
    }
    const ULONG sequence = static_cast<ULONG>(InterlockedIncrement(&buffer->nextSequence));
    DiagnosticRecord& record = buffer->records[(sequence - 1u) % kDiagnosticCapacity];
    InterlockedExchange(&record.publication, static_cast<LONG>(sequence * 2u - 1u));
    const ULONGLONG tick = GetTickCount64();
    record.sequence = sequence;
    record.tickLow = static_cast<ULONG>(tick);
    record.tickHigh = static_cast<ULONG>(tick >> 32);
    record.driverPid = GetCurrentProcessId();
    record.connectorIndex = connector;
    record.assignment = assignment;
    record.stage = static_cast<UINT>(stage);
    record.adapterLow = adapter.LowPart;
    record.adapterHigh = adapter.HighPart;
    record.result = result;
    record.detail = detail;
    InterlockedExchange(&record.publication, static_cast<LONG>(sequence * 2u));
    InterlockedExchange(&buffer->writerLock, 0);
}

ULONG BeginIdentityPublication(GateIdentity& identity)
{
    const ULONG previousSequence = static_cast<ULONG>(
        InterlockedCompareExchange(&identity.sequence, 0, 0));
    const ULONG writingSequence = (previousSequence | 1u) + 2u;
    InterlockedExchange(&identity.sequence, static_cast<LONG>(writingSequence));
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
    {0x67453031, 0x7ba9, 0x4d45, {0x8f, 0x0b, 0x72, 0x1d, 0xb4, 0x61, 0x62, 0x42}};

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
    DriverDiagnostics* diagnostics{};
    UINT connectorIndex{};
    ULONG assignment{};
    LUID renderAdapter{};
};

struct DeviceState
{
    WDFDEVICE device{};
    IDDCX_ADAPTER adapter{};
};

struct MonitorState
{
    Drain* drain{};
    ModeConfig mode{};
    DriverDiagnostics* diagnostics{};
    ULONG assignment{};
    LUID renderAdapter{};
};

WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(DeviceState, GetDeviceState);
WDF_DECLARE_CONTEXT_TYPE_WITH_NAME(MonitorState, GetMonitorState);

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
        config.header.count <= kMaximumMonitors)
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
    ReleaseDiagnostics(drain->diagnostics);
    delete drain;
}

DWORD WINAPI DrainFrames(void* argument)
{
    auto* drain = static_cast<Drain*>(argument);
    RecordDiagnostic(drain->diagnostics, drain->connectorIndex, drain->assignment,
        DiagnosticStage::DrainStarted, drain->renderAdapter);
    bool firstFrame = true;
    HRESULT exitResult = S_OK;
    UINT exitReason = 1; // Explicit stop before acquisition.

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
            exitReason = wait == WAIT_OBJECT_0 + 1 ? 2 : 3;
            exitResult = wait == WAIT_FAILED ? HRESULT_FROM_WIN32(GetLastError()) : S_OK;
            break;
        }

        if (FAILED(result))
        {
            exitResult = result;
            exitReason = 4; // ReleaseAndAcquireBuffer.
            break;
        }

        if (buffer.MetaData.pSurface != nullptr)
        {
            buffer.MetaData.pSurface->Release();
        }

        const HRESULT finished = IddCxSwapChainFinishedProcessingFrame(drain->swapChain);
        if (FAILED(finished))
        {
            exitResult = finished;
            exitReason = 5; // FinishedProcessingFrame.
            break;
        }
        if (firstFrame)
        {
            firstFrame = false;
            RecordDiagnostic(drain->diagnostics, drain->connectorIndex, drain->assignment,
                DiagnosticStage::FirstFrame, drain->renderAdapter);
        }
    }

    RecordDiagnostic(drain->diagnostics, drain->connectorIndex, drain->assignment,
        DiagnosticStage::DrainExit, drain->renderAdapter, exitResult, exitReason);
    if (FAILED(exitResult))
    {
        RecordDiagnostic(drain->diagnostics, drain->connectorIndex, drain->assignment,
            DiagnosticStage::DeviceRemovedReason, drain->renderAdapter,
            drain->d3dDevice->GetDeviceRemovedReason());
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
    if (!SetEvent(drain->stop))
    {
        const HRESULT result = HRESULT_FROM_WIN32(GetLastError());
        RecordDiagnostic(drain->diagnostics, drain->connectorIndex, drain->assignment,
            DiagnosticStage::SignalStop, drain->renderAdapter, result);
    }
    if (drain->thread != nullptr)
    {
        const DWORD wait = WaitForSingleObject(drain->thread, 5000);
        if (wait != WAIT_OBJECT_0)
        {
            const HRESULT result = wait == WAIT_FAILED ? HRESULT_FROM_WIN32(GetLastError()) : S_OK;
            RecordDiagnostic(drain->diagnostics, drain->connectorIndex, drain->assignment,
                DiagnosticStage::StopWait, drain->renderAdapter, result, wait);
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

    DiagnosticStage stage = DiagnosticStage::CreateFactory;
    HRESULT result = CreateDXGIFactory2(0, IID_PPV_ARGS(&factory));
    if (SUCCEEDED(result))
    {
        stage = DiagnosticStage::EnumAdapter;
        result = factory->EnumAdapterByLuid(renderAdapter, IID_PPV_ARGS(&adapter));
    }
    if (SUCCEEDED(result))
    {
        stage = DiagnosticStage::CreateDevice;
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
    }
    if (SUCCEEDED(result))
    {
        stage = DiagnosticStage::QueryDevice;
        result = d3dDevice->QueryInterface(IID_PPV_ARGS(&dxgiDevice));
    }
    if (SUCCEEDED(result))
    {
        stage = DiagnosticStage::SetDevice;
        IDARG_IN_SWAPCHAINSETDEVICE setDevice{};
        setDevice.pDevice = dxgiDevice;
        result = IddCxSwapChainSetDevice(swapChain, &setDevice);
    }

    if (dxgiDevice != nullptr) dxgiDevice->Release();
    if (adapter != nullptr) adapter->Release();
    if (factory != nullptr) factory->Release();

    if (FAILED(result))
    {
        RecordDiagnostic(monitor->diagnostics, monitor->mode.connectorIndex, monitor->assignment,
            stage, renderAdapter, result);
        if (d3dDevice != nullptr)
        {
            RecordDiagnostic(monitor->diagnostics, monitor->mode.connectorIndex, monitor->assignment,
                DiagnosticStage::DeviceRemovedReason, renderAdapter, d3dDevice->GetDeviceRemovedReason());
        }
        if (d3dDevice != nullptr) d3dDevice->Release();
        return false;
    }

    auto* drain = new (std::nothrow) Drain{};
    if (drain == nullptr)
    {
        RecordDiagnostic(monitor->diagnostics, monitor->mode.connectorIndex, monitor->assignment,
            DiagnosticStage::AllocateDrain, renderAdapter, E_OUTOFMEMORY);
        d3dDevice->Release();
        return false;
    }

    drain->swapChain = swapChain;
    drain->d3dDevice = d3dDevice;
    drain->frameAvailable = frameAvailable;
    drain->diagnostics = monitor->diagnostics;
    RetainDiagnostics(drain->diagnostics);
    drain->connectorIndex = monitor->mode.connectorIndex;
    drain->assignment = monitor->assignment;
    drain->renderAdapter = renderAdapter;
    drain->stop = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (drain->stop != nullptr)
    {
        drain->thread = CreateThread(nullptr, 0, DrainFrames, drain, 0, nullptr);
    }
    if (drain->thread == nullptr)
    {
        const HRESULT error = HRESULT_FROM_WIN32(GetLastError());
        RecordDiagnostic(monitor->diagnostics, monitor->mode.connectorIndex, monitor->assignment,
            drain->stop == nullptr ? DiagnosticStage::CreateStop : DiagnosticStage::CreateWorker,
            renderAdapter, error);
        DestroyDrain(drain);
        return false;
    }

    monitor->drain = drain;
    return true;
}

NTSTATUS ReportMonitor(DeviceState* state, const ModeConfig& mode, GateConfig& config,
    DriverDiagnostics* diagnostics)
{
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
    if (!NT_SUCCESS(createStatus))
    {
        return createStatus;
    }

    MonitorState* monitorState = GetMonitorState(output.MonitorObject);
    monitorState->mode = mode;
    monitorState->diagnostics = diagnostics;
    RetainDiagnostics(diagnostics);

    IDARG_OUT_MONITORARRIVAL arrival{};
    const NTSTATUS arrivalStatus =
        IddCxMonitorArrival(output.MonitorObject, &arrival);
    if (!NT_SUCCESS(arrivalStatus))
    {
        WdfObjectDelete(output.MonitorObject);
        return arrivalStatus;
    }

    // An odd sequence marks a write; matching nonzero even reads form one
    // snapshot. Advance past an interrupted write when restarting the driver.
    GateIdentity& identity = config.identities[mode.connectorIndex];
    const ULONG writingSequence = BeginIdentityPublication(identity);
    identity.adapterLow = arrival.OsAdapterLuid.LowPart;
    identity.adapterHigh = arrival.OsAdapterLuid.HighPart;
    identity.targetId = arrival.OsTargetId;
    InterlockedExchange(&identity.sequence, static_cast<LONG>(writingSequence + 1u));
    RecordDiagnostic(diagnostics, mode.connectorIndex, 0, DiagnosticStage::MonitorReady,
        arrival.OsAdapterLuid, S_OK, arrival.OsTargetId);

    return STATUS_SUCCESS;
}

NTSTATUS ReportMonitors(DeviceState* state)
{
    HANDLE gate = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, kSessionGate);
    if (gate == nullptr)
    {
        return STATUS_INVALID_PARAMETER;
    }
    auto* config = static_cast<GateConfig*>(
        MapViewOfFile(gate, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(GateConfig)));
    if (config == nullptr)
    {
        CloseHandle(gate);
        return STATUS_INVALID_PARAMETER;
    }

    // Keep this exact session mapping alive through all arrivals. Reopening by
    // name per monitor could publish an older adapter's identities into a new session.
    ModeConfig modes[kMaximumMonitors]{};
    DriverDiagnostics* diagnostics = OpenDriverDiagnostics();
    UINT count = 0;
    NTSTATUS result = STATUS_INVALID_PARAMETER;
    if (ReadSessionConfig(*config, modes, count))
    {
        // A restarted adapter may reuse OS target IDs. Invalidate every old
        // receipt before any new monitor can arrive and be mistaken for it.
        for (UINT index = 0; index < count; ++index)
        {
            BeginIdentityPublication(config->identities[modes[index].connectorIndex]);
        }
        result = STATUS_SUCCESS;
        for (UINT index = 0; index < count; ++index)
        {
            result = ReportMonitor(state, modes[index], *config, diagnostics);
            if (!NT_SUCCESS(result))
            {
                break;
            }
        }
    }
    ReleaseDiagnostics(diagnostics);
    UnmapViewOfFile(config);
    CloseHandle(gate);
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
    MonitorState* monitor = GetMonitorState(object);
    StopDrain(monitor);
    ReleaseDiagnostics(monitor->diagnostics);
    monitor->diagnostics = nullptr;
}

NTSTATUS AdapterInitFinished(
    IDDCX_ADAPTER adapter,
    const IDARG_IN_ADAPTER_INIT_FINISHED* input)
{
    if (NT_SUCCESS(input->AdapterInitStatus))
    {
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
    StopDrain(state);
    ++state->assignment;
    state->renderAdapter = input->RenderAdapterLuid;
    RecordDiagnostic(state->diagnostics, state->mode.connectorIndex, state->assignment,
        DiagnosticStage::Assign, input->RenderAdapterLuid);
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
    MonitorState* state = GetMonitorState(monitor);
    RecordDiagnostic(state->diagnostics, state->mode.connectorIndex, state->assignment,
        DiagnosticStage::Unassign, state->renderAdapter);
    StopDrain(state);
    return STATUS_SUCCESS;
}

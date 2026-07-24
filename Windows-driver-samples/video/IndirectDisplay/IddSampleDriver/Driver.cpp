/*++

Copyright (c) Microsoft Corporation

Abstract:

    This module contains a sample implementation of an indirect display driver. See the included README.md file and the
    various TODO blocks throughout this file and all accompanying files for information on building a production driver.

    MSDN documentation on indirect displays can be found at https://msdn.microsoft.com/en-us/library/windows/hardware/mt761968(v=vs.85).aspx.

Environment:

    User Mode, UMDF

--*/

#include <initguid.h>
#include "Driver.h"
#include "Driver.tmh"

using namespace std;
using namespace Microsoft::IndirectDisp;
using namespace Microsoft::WRL;

#pragma region SampleMonitors

/*
 * Issue #1: keep one host-created software device mapped to one virtual
 * monitor so a normal launch cannot light up extra desktops.
 *
 * SBMS owns multi-screen scaling at the host/process layer: SBMSDeviceHost
 * creates one software device for each requested virtual desktop, and each
 * software device must expose exactly one IddCx monitor here. Raising this
 * constant makes Windows attach multiple monitors to a single host-created
 * device, so the normal one-screen workflow unexpectedly lights up extra
 * virtual displays before the GUI has asked for them.
 */
static constexpr DWORD IDD_SAMPLE_MONITOR_COUNT = 1;
static constexpr DWORD SBMS_PREFERRED_MODE_INDEX = 7;

/*
 * Issue #1: do not advertise oversized legacy modes that Windows may prefer
 * before the GUI applies the requested virtual display mode.
 *
 * These are the only modes the indirect monitor advertises to Windows. Keep
 * the table bounded to resolutions SBMS can actually select and mirror. Older
 * test builds advertised 7680-series modes; Windows preferred one of those on
 * first arrival, which created a huge virtual desktop and made the GUI appear
 * to start in an unintended layout.
 */
#define SBMS_SUPPORTED_MODES \
    { 4552, 2560, 240 }, \
    { 4552, 2560,  60 }, \
    { 2560, 4552, 240 }, \
    { 2560, 4552,  60 }, \
    { 4550, 2560, 240 }, \
    { 4550, 2560,  60 }, \
    { 1920, 1080, 240 }, \
    { 1920, 1080,  60 }, \
    { 1080, 1920, 240 }, \
    { 1080, 1920,  60 }, \
    { 1920, 1200, 240 }, \
    { 1920, 1200,  60 }, \
    { 1200, 1920, 240 }, \
    { 1200, 1920,  60 }, \
    { 1920, 1440, 240 }, \
    { 1920, 1440,  60 }, \
    { 1440, 1920, 240 }, \
    { 1440, 1920,  60 }, \
    { 2560, 1440, 240 }, \
    { 2560, 1440,  60 }, \
    { 1440, 2560, 240 }, \
    { 1440, 2560,  60 }, \
    { 2560, 1600, 240 }, \
    { 2560, 1600,  60 }, \
    { 1600, 2560, 240 }, \
    { 1600, 2560,  60 }, \
    { 2560, 1920, 240 }, \
    { 2560, 1920,  60 }, \
    { 1920, 2560, 240 }, \
    { 1920, 2560,  60 }, \
    { 3840, 2160, 240 }, \
    { 3840, 2160,  60 }, \
    { 2160, 3840, 240 }, \
    { 2160, 3840,  60 }, \
    { 3840, 2400, 240 }, \
    { 3840, 2400,  60 }, \
    { 2400, 3840, 240 }, \
    { 2400, 3840,  60 }, \
    { 3840, 2880, 240 }, \
    { 3840, 2880,  60 }, \
    { 2880, 3840, 240 }, \
    { 2880, 3840,  60 }, \
    { 5120, 2880, 240 }, \
    { 5120, 2880,  60 }, \
    { 2880, 5120, 240 }, \
    { 2880, 5120,  60 }, \
    { 5120, 3200, 240 }, \
    { 5120, 3200,  60 }, \
    { 3200, 5120, 240 }, \
    { 3200, 5120,  60 }, \
    { 5120, 3840, 240 }, \
    { 5120, 3840,  60 }, \
    { 3840, 5120, 240 }, \
    { 3840, 5120,  60 }

// Default modes reported for edid-less monitors. The first mode is set as preferred
static const struct IndirectSampleMonitor::SampleMonitorMode s_SampleDefaultModes[] = 
{
    SBMS_SUPPORTED_MODES
};

// FOR SAMPLE PURPOSES ONLY, Static info about monitors that will be reported to OS
static const struct IndirectSampleMonitor s_SampleMonitors[] =
{
    // SBMS-owned EDID. The device-instance-specific serial and checksum are
    // filled immediately before monitor arrival.
    {
        {
            0x00,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0x00,0x4C,0x4D,0x01,0x00,0x00,0x00,0x00,0x00,
            0x01,0x24,0x01,0x04,0xA5,0x34,0x1D,0x78,0x0A,0x00,0x00,0x00,0x00,0x00,0x00,0x00,
            0x00,0x00,0x00,0x00,0x00,0x00,0x01,0x01,0x01,0x01,0x01,0x01,0x01,0x01,0x01,0x01,
            0x01,0x01,0x01,0x01,0x01,0x01,0x02,0x3A,0x80,0x18,0x71,0x38,0x2D,0x40,0x58,0x2C,
            0x45,0x00,0xFD,0x1E,0x11,0x00,0x00,0x1A,0x00,0x00,0x00,0xFF,0x00,0x53,0x42,0x4D,
            0x53,0x30,0x30,0x30,0x30,0x30,0x30,0x30,0x31,0x0A,0x00,0x00,0x00,0xFC,0x00,0x53,
            0x42,0x4D,0x53,0x20,0x44,0x69,0x73,0x70,0x6C,0x61,0x79,0x0A,0x00,0x00,0x00,0xFE,
            0x00,0x53,0x42,0x4D,0x53,0x20,0x49,0x44,0x44,0x0A,0x20,0x20,0x20,0x20,0x00,0x23
        },
        s_SampleDefaultModes,
        ARRAYSIZE(s_SampleDefaultModes),
        SBMS_PREFERRED_MODE_INDEX
    }
};

#pragma endregion

#pragma region helpers

static ULONGLONG HashSbmsMonitorIdentity(PCWSTR InstanceId, ULONGLONG Seed, UINT ConnectorIndex)
{
    constexpr ULONGLONG FnvPrime = 1099511628211ULL;
    ULONGLONG Hash = Seed;
    for (auto Character = InstanceId; Character && *Character; ++Character)
    {
        WCHAR Value = *Character;
        if (Value >= L'a' && Value <= L'z')
        {
            Value -= (L'a' - L'A');
        }
        Hash ^= static_cast<BYTE>(Value & 0xff);
        Hash *= FnvPrime;
        Hash ^= static_cast<BYTE>((Value >> 8) & 0xff);
        Hash *= FnvPrime;
    }
    for (UINT Shift = 0; Shift < 32; Shift += 8)
    {
        Hash ^= static_cast<BYTE>((ConnectorIndex >> Shift) & 0xff);
        Hash *= FnvPrime;
    }
    return Hash;
}

static bool IsSbmsEdid(const BYTE* Edid, size_t EdidSize)
{
    static const BYTE Header[] = { 0x00,0xFF,0xFF,0xFF,0xFF,0xFF,0xFF,0x00 };
    if (!Edid || EdidSize != IndirectSampleMonitor::szEdidBlock ||
        memcmp(Edid, Header, ARRAYSIZE(Header)) != 0 ||
        Edid[8] != 0x4C || Edid[9] != 0x4D ||
        Edid[10] != 0x01 || Edid[11] != 0x00)
    {
        return false;
    }

    BYTE Checksum = 0;
    for (size_t Index = 0; Index < EdidSize; ++Index)
    {
        Checksum = static_cast<BYTE>(Checksum + Edid[Index]);
    }
    return Checksum == 0;
}

static inline void FillSignalInfo(DISPLAYCONFIG_VIDEO_SIGNAL_INFO& Mode, DWORD Width, DWORD Height, DWORD VSync, bool bMonitorMode)
{
    Mode.totalSize.cx = Mode.activeSize.cx = Width;
    Mode.totalSize.cy = Mode.activeSize.cy = Height;

    // See https://docs.microsoft.com/en-us/windows/win32/api/wingdi/ns-wingdi-displayconfig_video_signal_info
    Mode.AdditionalSignalInfo.vSyncFreqDivider = bMonitorMode ? 0 : 1;
    Mode.AdditionalSignalInfo.videoStandard = 255;

    Mode.vSyncFreq.Numerator = VSync;
    Mode.vSyncFreq.Denominator = 1;
    Mode.hSyncFreq.Numerator = VSync * Height;
    Mode.hSyncFreq.Denominator = 1;

    Mode.scanLineOrdering = DISPLAYCONFIG_SCANLINE_ORDERING_PROGRESSIVE;

    Mode.pixelRate = ((UINT64) VSync) * ((UINT64) Width) * ((UINT64) Height);
}

static IDDCX_MONITOR_MODE CreateIddCxMonitorMode(DWORD Width, DWORD Height, DWORD VSync, IDDCX_MONITOR_MODE_ORIGIN Origin = IDDCX_MONITOR_MODE_ORIGIN_DRIVER)
{
    IDDCX_MONITOR_MODE Mode = {};

    Mode.Size = sizeof(Mode);
    Mode.Origin = Origin;
    FillSignalInfo(Mode.MonitorVideoSignalInfo, Width, Height, VSync, true);

    return Mode;
}

static IDDCX_TARGET_MODE CreateIddCxTargetMode(DWORD Width, DWORD Height, DWORD VSync)
{
    IDDCX_TARGET_MODE Mode = {};

    Mode.Size = sizeof(Mode);
    FillSignalInfo(Mode.TargetVideoSignalInfo.targetVideoSignalInfo, Width, Height, VSync, false);

    return Mode;
}

#pragma endregion

extern "C" DRIVER_INITIALIZE DriverEntry;

EVT_WDF_DRIVER_DEVICE_ADD IddSampleDeviceAdd;
EVT_WDF_DEVICE_D0_ENTRY IddSampleDeviceD0Entry;

EVT_IDD_CX_ADAPTER_INIT_FINISHED IddSampleAdapterInitFinished;
EVT_IDD_CX_ADAPTER_COMMIT_MODES IddSampleAdapterCommitModes;

EVT_IDD_CX_PARSE_MONITOR_DESCRIPTION IddSampleParseMonitorDescription;
EVT_IDD_CX_MONITOR_GET_DEFAULT_DESCRIPTION_MODES IddSampleMonitorGetDefaultModes;
EVT_IDD_CX_MONITOR_QUERY_TARGET_MODES IddSampleMonitorQueryModes;

EVT_IDD_CX_MONITOR_ASSIGN_SWAPCHAIN IddSampleMonitorAssignSwapChain;
EVT_IDD_CX_MONITOR_UNASSIGN_SWAPCHAIN IddSampleMonitorUnassignSwapChain;

struct IndirectDeviceContextWrapper
{
    IndirectDeviceContext* pContext;

    void Cleanup()
    {
        delete pContext;
        pContext = nullptr;
    }
};

struct IndirectMonitorContextWrapper
{
    IndirectMonitorContext* pContext;

    void Cleanup()
    {
        delete pContext;
        pContext = nullptr;
    }
};

// This macro creates the methods for accessing an IndirectDeviceContextWrapper as a context for a WDF object
WDF_DECLARE_CONTEXT_TYPE(IndirectDeviceContextWrapper);

WDF_DECLARE_CONTEXT_TYPE(IndirectMonitorContextWrapper);

extern "C" BOOL WINAPI DllMain(
    _In_ HINSTANCE hInstance,
    _In_ UINT dwReason,
    _In_opt_ LPVOID lpReserved)
{
    UNREFERENCED_PARAMETER(hInstance);
    UNREFERENCED_PARAMETER(lpReserved);
    UNREFERENCED_PARAMETER(dwReason);

    return TRUE;
}

_Use_decl_annotations_
extern "C" NTSTATUS DriverEntry(
    PDRIVER_OBJECT  pDriverObject,
    PUNICODE_STRING pRegistryPath
)
{
    WDF_DRIVER_CONFIG Config;
    NTSTATUS Status;

    WDF_OBJECT_ATTRIBUTES Attributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&Attributes);

    WDF_DRIVER_CONFIG_INIT(&Config,
        IddSampleDeviceAdd
    );

    Status = WdfDriverCreate(pDriverObject, pRegistryPath, &Attributes, &Config, WDF_NO_HANDLE);
    if (!NT_SUCCESS(Status))
    {
        return Status;
    }

    return Status;
}

_Use_decl_annotations_
NTSTATUS IddSampleDeviceAdd(WDFDRIVER Driver, PWDFDEVICE_INIT pDeviceInit)
{
    NTSTATUS Status = STATUS_SUCCESS;
    WDF_PNPPOWER_EVENT_CALLBACKS PnpPowerCallbacks;

    UNREFERENCED_PARAMETER(Driver);

    // Register for power callbacks - in this sample only power-on is needed
    WDF_PNPPOWER_EVENT_CALLBACKS_INIT(&PnpPowerCallbacks);
    PnpPowerCallbacks.EvtDeviceD0Entry = IddSampleDeviceD0Entry;
    WdfDeviceInitSetPnpPowerEventCallbacks(pDeviceInit, &PnpPowerCallbacks);

    IDD_CX_CLIENT_CONFIG IddConfig;
    IDD_CX_CLIENT_CONFIG_INIT(&IddConfig);

    // If the driver wishes to handle custom IoDeviceControl requests, it's necessary to use this callback since IddCx
    // redirects IoDeviceControl requests to an internal queue. This sample does not need this.
    // IddConfig.EvtIddCxDeviceIoControl = IddSampleIoDeviceControl;

    IddConfig.EvtIddCxAdapterInitFinished = IddSampleAdapterInitFinished;

    IddConfig.EvtIddCxParseMonitorDescription = IddSampleParseMonitorDescription;
    IddConfig.EvtIddCxMonitorGetDefaultDescriptionModes = IddSampleMonitorGetDefaultModes;
    IddConfig.EvtIddCxMonitorQueryTargetModes = IddSampleMonitorQueryModes;
    IddConfig.EvtIddCxAdapterCommitModes = IddSampleAdapterCommitModes;
    IddConfig.EvtIddCxMonitorAssignSwapChain = IddSampleMonitorAssignSwapChain;
    IddConfig.EvtIddCxMonitorUnassignSwapChain = IddSampleMonitorUnassignSwapChain;

    Status = IddCxDeviceInitConfig(pDeviceInit, &IddConfig);
    if (!NT_SUCCESS(Status))
    {
        return Status;
    }

    WDF_OBJECT_ATTRIBUTES Attr;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&Attr, IndirectDeviceContextWrapper);
    Attr.EvtCleanupCallback = [](WDFOBJECT Object)
    {
        // Automatically cleanup the context when the WDF object is about to be deleted
        auto* pContext = WdfObjectGet_IndirectDeviceContextWrapper(Object);
        if (pContext)
        {
            pContext->Cleanup();
        }
    };

    WDFDEVICE Device = nullptr;
    Status = WdfDeviceCreate(&pDeviceInit, &Attr, &Device);
    if (!NT_SUCCESS(Status))
    {
        return Status;
    }

    Status = IddCxDeviceInitialize(Device);

    // Create a new device context object and attach it to the WDF device object
    auto* pContext = WdfObjectGet_IndirectDeviceContextWrapper(Device);
    pContext->pContext = new IndirectDeviceContext(Device);

    return Status;
}

_Use_decl_annotations_
NTSTATUS IddSampleDeviceD0Entry(WDFDEVICE Device, WDF_POWER_DEVICE_STATE PreviousState)
{
    UNREFERENCED_PARAMETER(PreviousState);

    // This function is called by WDF to start the device in the fully-on power state.

    auto* pContext = WdfObjectGet_IndirectDeviceContextWrapper(Device);
    pContext->pContext->InitAdapter();

    return STATUS_SUCCESS;
}

#pragma region Direct3DDevice

Direct3DDevice::Direct3DDevice(LUID AdapterLuid) : AdapterLuid(AdapterLuid)
{

}

Direct3DDevice::Direct3DDevice()
{
    AdapterLuid = LUID{};
}

HRESULT Direct3DDevice::Init()
{
    // The DXGI factory could be cached, but if a new render adapter appears on the system, a new factory needs to be
    // created. If caching is desired, check DxgiFactory->IsCurrent() each time and recreate the factory if !IsCurrent.
    HRESULT hr = CreateDXGIFactory2(0, IID_PPV_ARGS(&DxgiFactory));
    if (FAILED(hr))
    {
        return hr;
    }

    // Find the specified render adapter
    hr = DxgiFactory->EnumAdapterByLuid(AdapterLuid, IID_PPV_ARGS(&Adapter));
    if (FAILED(hr))
    {
        return hr;
    }

    // Create a D3D device using the render adapter. BGRA support is required by the WHQL test suite.
    hr = D3D11CreateDevice(Adapter.Get(), D3D_DRIVER_TYPE_UNKNOWN, nullptr, D3D11_CREATE_DEVICE_BGRA_SUPPORT, nullptr, 0, D3D11_SDK_VERSION, &Device, nullptr, &DeviceContext);
    if (FAILED(hr))
    {
        // If creating the D3D device failed, it's possible the render GPU was lost (e.g. detachable GPU) or else the
        // system is in a transient state.
        return hr;
    }

    return S_OK;
}

#pragma endregion

#pragma region SwapChainProcessor

SwapChainProcessor::SwapChainProcessor(IDDCX_SWAPCHAIN hSwapChain, shared_ptr<Direct3DDevice> Device, HANDLE NewFrameEvent)
    : m_hSwapChain(hSwapChain), m_Device(Device), m_hAvailableBufferEvent(NewFrameEvent)
{
    m_hTerminateEvent.Attach(CreateEvent(nullptr, FALSE, FALSE, nullptr));

    // Immediately create and run the swap-chain processing thread, passing 'this' as the thread parameter
    m_hThread.Attach(CreateThread(nullptr, 0, RunThread, this, 0, nullptr));
}

SwapChainProcessor::~SwapChainProcessor()
{
    // Alert the swap-chain processing thread to terminate
    SetEvent(m_hTerminateEvent.Get());

    if (m_hThread.Get())
    {
        // Wait for the thread to terminate
        WaitForSingleObject(m_hThread.Get(), INFINITE);
    }
}

DWORD CALLBACK SwapChainProcessor::RunThread(LPVOID Argument)
{
    reinterpret_cast<SwapChainProcessor*>(Argument)->Run();
    return 0;
}

void SwapChainProcessor::Run()
{
    // For improved performance, make use of the Multimedia Class Scheduler Service, which will intelligently
    // prioritize this thread for improved throughput in high CPU-load scenarios.
    DWORD AvTask = 0;
    HANDLE AvTaskHandle = AvSetMmThreadCharacteristicsW(L"Distribution", &AvTask);

    RunCore();

    // Always delete the swap-chain object when swap-chain processing loop terminates in order to kick the system to
    // provide a new swap-chain if necessary.
    WdfObjectDelete((WDFOBJECT)m_hSwapChain);
    m_hSwapChain = nullptr;

    // MMCSS registration can fail when the service is unavailable or the thread
    // cannot be promoted. Only revert when registration returned a real handle;
    // calling AvRevertMmThreadCharacteristics(nullptr) is invalid and WDK
    // static analysis treats it as a driver-load risk.
    if (AvTaskHandle != nullptr)
    {
        AvRevertMmThreadCharacteristics(AvTaskHandle);
    }
}

void SwapChainProcessor::RunCore()
{
    // Get the DXGI device interface
    ComPtr<IDXGIDevice> DxgiDevice;
    HRESULT hr = m_Device->Device.As(&DxgiDevice);
    if (FAILED(hr))
    {
        return;
    }

    IDARG_IN_SWAPCHAINSETDEVICE SetDevice = {};
    SetDevice.pDevice = DxgiDevice.Get();

    hr = IddCxSwapChainSetDevice(m_hSwapChain, &SetDevice);
    if (FAILED(hr))
    {
        return;
    }

    // Acquire and release buffers in a loop
    for (;;)
    {
        ComPtr<IDXGIResource> AcquiredBuffer;

        // Ask for the next buffer from the producer
        IDARG_OUT_RELEASEANDACQUIREBUFFER Buffer = {};
        hr = IddCxSwapChainReleaseAndAcquireBuffer(m_hSwapChain, &Buffer);

        // AcquireBuffer immediately returns STATUS_PENDING if no buffer is yet available
        if (hr == E_PENDING)
        {
            // We must wait for a new buffer
            HANDLE WaitHandles [] =
            {
                m_hAvailableBufferEvent,
                m_hTerminateEvent.Get()
            };
            DWORD WaitResult = WaitForMultipleObjects(ARRAYSIZE(WaitHandles), WaitHandles, FALSE, 16);
            if (WaitResult == WAIT_OBJECT_0 || WaitResult == WAIT_TIMEOUT)
            {
                // We have a new buffer, so try the AcquireBuffer again
                continue;
            }
            else if (WaitResult == WAIT_OBJECT_0 + 1)
            {
                // We need to terminate
                break;
            }
            else
            {
                // The wait was cancelled or something unexpected happened
                hr = HRESULT_FROM_WIN32(WaitResult);
                break;
            }
        }
        else if (SUCCEEDED(hr))
        {
            // We have new frame to process, the surface has a reference on it that the driver has to release
            AcquiredBuffer.Attach(Buffer.MetaData.pSurface);

            // ==============================
            // TODO: Process the frame here
            //
            // This is the most performance-critical section of code in an IddCx driver. It's important that whatever
            // is done with the acquired surface be finished as quickly as possible. This operation could be:
            //  * a GPU copy to another buffer surface for later processing (such as a staging surface for mapping to CPU memory)
            //  * a GPU encode operation
            //  * a GPU VPBlt to another surface
            //  * a GPU custom compute shader encode operation
            // ==============================

            // We have finished processing this frame hence we release the reference on it.
            // If the driver forgets to release the reference to the surface, it will be leaked which results in the
            // surfaces being left around after swapchain is destroyed.
            // NOTE: Although in this sample we release reference to the surface here; the driver still
            // owns the Buffer.MetaData.pSurface surface until IddCxSwapChainReleaseAndAcquireBuffer returns
            // S_OK and gives us a new frame, a driver may want to use the surface in future to re-encode the desktop 
            // for better quality if there is no new frame for a while
            AcquiredBuffer.Reset();
            
            // Indicate to OS that we have finished inital processing of the frame, it is a hint that
            // OS could start preparing another frame
            hr = IddCxSwapChainFinishedProcessingFrame(m_hSwapChain);
            if (FAILED(hr))
            {
                break;
            }

            // ==============================
            // TODO: Report frame statistics once the asynchronous encode/send work is completed
            //
            // Drivers should report information about sub-frame timings, like encode time, send time, etc.
            // ==============================
            // IddCxSwapChainReportFrameStatistics(m_hSwapChain, ...);
        }
        else
        {
            // The swap-chain was likely abandoned (e.g. DXGI_ERROR_ACCESS_LOST), so exit the processing loop
            break;
        }
    }
}

#pragma endregion

#pragma region IndirectDeviceContext

IndirectDeviceContext::IndirectDeviceContext(_In_ WDFDEVICE WdfDevice) :
    m_WdfDevice(WdfDevice),
    m_Adapter{},
    m_MonitorEdid{},
    m_MonitorContainerId{},
    m_MonitorIdentityInitialized(false)
{
}

IndirectDeviceContext::~IndirectDeviceContext()
{
}

bool IndirectDeviceContext::InitializeMonitorIdentity(UINT ConnectorIndex)
{
    if (m_MonitorIdentityInitialized)
    {
        return true;
    }

    WDF_OBJECT_ATTRIBUTES Attributes;
    WDF_OBJECT_ATTRIBUTES_INIT(&Attributes);
    Attributes.ParentObject = m_WdfDevice;

    WDF_DEVICE_PROPERTY_DATA PropertyData;
    WDF_DEVICE_PROPERTY_DATA_INIT(&PropertyData, &DEVPKEY_Device_InstanceId);
    WDFMEMORY InstanceIdMemory = nullptr;
    DEVPROPTYPE PropertyType = 0;
    NTSTATUS Status = WdfDeviceAllocAndQueryPropertyEx(
        m_WdfDevice,
        &PropertyData,
        PagedPool,
        &Attributes,
        &InstanceIdMemory,
        &PropertyType);
    if (!NT_SUCCESS(Status))
    {
        return false;
    }
    if (PropertyType != DEVPROP_TYPE_STRING)
    {
        WdfObjectDelete(InstanceIdMemory);
        return false;
    }

    size_t InstanceIdBytes = 0;
    auto InstanceId = static_cast<PCWSTR>(WdfMemoryGetBuffer(InstanceIdMemory, &InstanceIdBytes));
    if (!InstanceId || InstanceIdBytes < sizeof(WCHAR) || InstanceId[0] == L'\0')
    {
        WdfObjectDelete(InstanceIdMemory);
        return false;
    }

    const ULONGLONG FirstHash = HashSbmsMonitorIdentity(
        InstanceId,
        14695981039346656037ULL,
        ConnectorIndex);
    const ULONGLONG SecondHash = HashSbmsMonitorIdentity(
        InstanceId,
        1099511628211ULL,
        ConnectorIndex);
    WdfObjectDelete(InstanceIdMemory);

    m_MonitorContainerId.Data1 = static_cast<ULONG>(FirstHash);
    m_MonitorContainerId.Data2 = static_cast<USHORT>(FirstHash >> 32);
    m_MonitorContainerId.Data3 =
        static_cast<USHORT>(((FirstHash >> 48) & 0x0fff) | 0x8000);
    for (UINT Index = 0; Index < ARRAYSIZE(m_MonitorContainerId.Data4); ++Index)
    {
        m_MonitorContainerId.Data4[Index] = static_cast<BYTE>(SecondHash >> (Index * 8));
    }
    // UUIDv8 reserves the payload for application-defined deterministic data.
    // Keep its version and RFC variant bits explicit so Windows receives a
    // standards-shaped GUID without adding random identity churn.
    m_MonitorContainerId.Data4[0] =
        static_cast<BYTE>((m_MonitorContainerId.Data4[0] & 0x3f) | 0x80);

    memcpy(
        m_MonitorEdid.data(),
        s_SampleMonitors[0].pEdidBlock,
        IndirectSampleMonitor::szEdidBlock);

    ULONG Serial = static_cast<ULONG>(FirstHash ^ (FirstHash >> 32) ^ SecondHash);
    if (Serial == 0)
    {
        Serial = 1;
    }
    for (UINT Index = 0; Index < sizeof(Serial); ++Index)
    {
        m_MonitorEdid[12 + Index] = static_cast<BYTE>(Serial >> (Index * 8));
    }

    static const char HexDigits[] = "0123456789ABCDEF";
    m_MonitorEdid[77] = 'S';
    m_MonitorEdid[78] = 'B';
    m_MonitorEdid[79] = 'M';
    m_MonitorEdid[80] = 'S';
    for (UINT Index = 0; Index < 8; ++Index)
    {
        const UINT Shift = (7 - Index) * 4;
        m_MonitorEdid[81 + Index] = static_cast<BYTE>(HexDigits[(Serial >> Shift) & 0x0f]);
    }
    m_MonitorEdid[89] = 0x0a;

    m_MonitorEdid[127] = 0;
    BYTE Checksum = 0;
    for (size_t Index = 0; Index < m_MonitorEdid.size() - 1; ++Index)
    {
        Checksum = static_cast<BYTE>(Checksum + m_MonitorEdid[Index]);
    }
    m_MonitorEdid[127] = static_cast<BYTE>(0 - Checksum);
    m_MonitorIdentityInitialized = true;
    return true;
}

void IndirectDeviceContext::InitAdapter()
{
    // ==============================
    // TODO: Update the below diagnostic information in accordance with the target hardware. The strings and version
    // numbers are used for telemetry and may be displayed to the user in some situations.
    //
    // This is also where static per-adapter capabilities are determined.
    // ==============================

    IDDCX_ADAPTER_CAPS AdapterCaps = {};
    AdapterCaps.Size = sizeof(AdapterCaps);

    // Declare basic feature support for the adapter (required)
    AdapterCaps.MaxMonitorsSupported = IDD_SAMPLE_MONITOR_COUNT;
    AdapterCaps.EndPointDiagnostics.Size = sizeof(AdapterCaps.EndPointDiagnostics);
    AdapterCaps.EndPointDiagnostics.GammaSupport = IDDCX_FEATURE_IMPLEMENTATION_NONE;
    AdapterCaps.EndPointDiagnostics.TransmissionType = IDDCX_TRANSMISSION_TYPE_WIRED_OTHER;

    // Declare your device strings for telemetry (required)
    AdapterCaps.EndPointDiagnostics.pEndPointFriendlyName = L"SBMS Virtual Display";
    AdapterCaps.EndPointDiagnostics.pEndPointManufacturerName = L"SBMS";
    AdapterCaps.EndPointDiagnostics.pEndPointModelName = L"SBMS Indirect Display";

    // Declare your hardware and firmware versions (required)
    IDDCX_ENDPOINT_VERSION Version = {};
    Version.Size = sizeof(Version);
    Version.MajorVer = 1;
    AdapterCaps.EndPointDiagnostics.pFirmwareVersion = &Version;
    AdapterCaps.EndPointDiagnostics.pHardwareVersion = &Version;

    // Initialize a WDF context that can store a pointer to the device context object
    WDF_OBJECT_ATTRIBUTES Attr;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&Attr, IndirectDeviceContextWrapper);

    IDARG_IN_ADAPTER_INIT AdapterInit = {};
    AdapterInit.WdfDevice = m_WdfDevice;
    AdapterInit.pCaps = &AdapterCaps;
    AdapterInit.ObjectAttributes = &Attr;

    // Start the initialization of the adapter, which will trigger the AdapterFinishInit callback later
    IDARG_OUT_ADAPTER_INIT AdapterInitOut;
    NTSTATUS Status = IddCxAdapterInitAsync(&AdapterInit, &AdapterInitOut);

    if (NT_SUCCESS(Status))
    {
        // Store a reference to the WDF adapter handle
        m_Adapter = AdapterInitOut.AdapterObject;

        // Store the device context object into the WDF object context
        auto* pContext = WdfObjectGet_IndirectDeviceContextWrapper(AdapterInitOut.AdapterObject);
        pContext->pContext = this;
    }
}

void IndirectDeviceContext::FinishInit(UINT ConnectorIndex)
{
    // ==============================
    // TODO: In a real driver, the EDID should be retrieved dynamically from a connected physical monitor. The EDIDs
    // provided here are purely for demonstration.
    // Monitor manufacturers are required to correctly fill in physical monitor attributes in order to allow the OS
    // to optimize settings like viewing distance and scale factor. Manufacturers should also use a unique serial
    // number every single device to ensure the OS can tell the monitors apart.
    // ==============================

    if (ConnectorIndex >= IDD_SAMPLE_MONITOR_COUNT ||
        !InitializeMonitorIdentity(ConnectorIndex))
    {
        return;
    }

    WDF_OBJECT_ATTRIBUTES Attr;
    WDF_OBJECT_ATTRIBUTES_INIT_CONTEXT_TYPE(&Attr, IndirectMonitorContextWrapper);
    Attr.EvtCleanupCallback = [](WDFOBJECT Object)
    {
        // The monitor context owns the swap-chain processing thread. Tying the
        // C++ object to the WDF monitor object's cleanup path prevents a stale
        // processing thread if monitor creation or arrival fails midway.
        auto* pContext = WdfObjectGet_IndirectMonitorContextWrapper(Object);
        if (pContext)
        {
            pContext->Cleanup();
        }
    };

    // In the sample driver, we report a monitor right away but a real driver would do this when a monitor connection event occurs
    IDDCX_MONITOR_INFO MonitorInfo = {};
    MonitorInfo.Size = sizeof(MonitorInfo);
    MonitorInfo.MonitorType = DISPLAYCONFIG_OUTPUT_TECHNOLOGY_HDMI;
    MonitorInfo.ConnectorIndex = ConnectorIndex;

    MonitorInfo.MonitorDescription.Size = sizeof(MonitorInfo.MonitorDescription);
    MonitorInfo.MonitorDescription.Type = IDDCX_MONITOR_DESCRIPTION_TYPE_EDID;
    MonitorInfo.MonitorDescription.DataSize = IndirectSampleMonitor::szEdidBlock;
    MonitorInfo.MonitorDescription.pData = m_MonitorEdid.data();

    // ==============================
    // TODO: The monitor's container ID should be distinct from "this" device's container ID if the monitor is not
    // permanently attached to the display adapter device object. The container ID is typically made unique for each
    // monitor and can be used to associate the monitor with other devices, like audio or input devices. In this
    // sample we generate a random container ID GUID, but it's best practice to choose a stable container ID for a
    // unique monitor or to use "this" device's container ID for a permanent/integrated monitor.
    // ==============================

    MonitorInfo.MonitorContainerId = m_MonitorContainerId;

    IDARG_IN_MONITORCREATE MonitorCreate = {};
    MonitorCreate.ObjectAttributes = &Attr;
    MonitorCreate.pMonitorInfo = &MonitorInfo;

    // Create a monitor object with the specified monitor descriptor
    IDARG_OUT_MONITORCREATE MonitorCreateOut;
    NTSTATUS Status = IddCxMonitorCreate(m_Adapter, &MonitorCreate, &MonitorCreateOut);
    if (NT_SUCCESS(Status))
    {
        // Create a new monitor context object and attach it to the Idd monitor object
        auto* pMonitorContextWrapper = WdfObjectGet_IndirectMonitorContextWrapper(MonitorCreateOut.MonitorObject);
        pMonitorContextWrapper->pContext = new IndirectMonitorContext(MonitorCreateOut.MonitorObject);

        // Tell the OS that the monitor has been plugged in. A created-but-not-
        // arrived monitor is unusable, so delete the WDF object immediately and
        // let the device host retry on the next adapter initialization.
        IDARG_OUT_MONITORARRIVAL ArrivalOut;
        Status = IddCxMonitorArrival(MonitorCreateOut.MonitorObject, &ArrivalOut);
        if (!NT_SUCCESS(Status))
        {
            WdfObjectDelete(MonitorCreateOut.MonitorObject);
            return;
        }
    }
}

IndirectMonitorContext::IndirectMonitorContext(_In_ IDDCX_MONITOR Monitor) :
    m_Monitor(Monitor)
{
}

IndirectMonitorContext::~IndirectMonitorContext()
{
    m_ProcessingThread.reset();
}

void IndirectMonitorContext::AssignSwapChain(IDDCX_SWAPCHAIN SwapChain, LUID RenderAdapter, HANDLE NewFrameEvent)
{
    m_ProcessingThread.reset();

    auto Device = make_shared<Direct3DDevice>(RenderAdapter);
    if (FAILED(Device->Init()))
    {
        // It's important to delete the swap-chain if D3D initialization fails, so that the OS knows to generate a new
        // swap-chain and try again.
        WdfObjectDelete(SwapChain);
    }
    else
    {
        // Create a new swap-chain processing thread
        m_ProcessingThread.reset(new SwapChainProcessor(SwapChain, Device, NewFrameEvent));
    }
}

void IndirectMonitorContext::UnassignSwapChain()
{
    // Stop processing the last swap-chain
    m_ProcessingThread.reset();
}

#pragma endregion

#pragma region DDI Callbacks

_Use_decl_annotations_
NTSTATUS IddSampleAdapterInitFinished(IDDCX_ADAPTER AdapterObject, const IDARG_IN_ADAPTER_INIT_FINISHED* pInArgs)
{
    // This is called when the OS has finished setting up the adapter for use by the IddCx driver. It's now possible
    // to report attached monitors.

    auto* pDeviceContextWrapper = WdfObjectGet_IndirectDeviceContextWrapper(AdapterObject);
    if (NT_SUCCESS(pInArgs->AdapterInitStatus))
    {
        for (DWORD i = 0; i < IDD_SAMPLE_MONITOR_COUNT; i++)
        {
            pDeviceContextWrapper->pContext->FinishInit(i);
        }
    }

    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS IddSampleAdapterCommitModes(IDDCX_ADAPTER AdapterObject, const IDARG_IN_COMMITMODES* pInArgs)
{
    UNREFERENCED_PARAMETER(AdapterObject);
    UNREFERENCED_PARAMETER(pInArgs);

    // For the sample, do nothing when modes are picked - the swap-chain is taken care of by IddCx

    // ==============================
    // TODO: In a real driver, this function would be used to reconfigure the device to commit the new modes. Loop
    // through pInArgs->pPaths and look for IDDCX_PATH_FLAGS_ACTIVE. Any path not active is inactive (e.g. the monitor
    // should be turned off).
    // ==============================

    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS IddSampleParseMonitorDescription(const IDARG_IN_PARSEMONITORDESCRIPTION* pInArgs, IDARG_OUT_PARSEMONITORDESCRIPTION* pOutArgs)
{
    // ==============================
    // TODO: In a real driver, this function would be called to generate monitor modes for an EDID by parsing it. In
    // this sample driver, we hard-code the EDID, so this function can generate known modes.
    // ==============================

    if (!IsSbmsEdid(
            static_cast<const BYTE*>(pInArgs->MonitorDescription.pData),
            pInArgs->MonitorDescription.DataSize))
    {
        return STATUS_INVALID_PARAMETER;
    }

    const auto& Monitor = s_SampleMonitors[0];

    pOutArgs->MonitorModeBufferOutputCount = Monitor.ModeCount;

    if (pInArgs->MonitorModeBufferInputCount < Monitor.ModeCount)
    {
        // Return success if there was no buffer, since the caller was only asking for a count of modes
        return (pInArgs->MonitorModeBufferInputCount > 0) ? STATUS_BUFFER_TOO_SMALL : STATUS_SUCCESS;
    }
    else
    {
        // In the sample driver, we have reported some static information about connected monitors
        // Check which of the reported monitors this call is for by comparing it to the pointer of
        // our known EDID blocks.

        // Copy the known modes to the output buffer
        for (DWORD ModeIndex = 0; ModeIndex < Monitor.ModeCount; ModeIndex++)
        {
            pInArgs->pMonitorModes[ModeIndex] = CreateIddCxMonitorMode(
                Monitor.pModeList[ModeIndex].Width,
                Monitor.pModeList[ModeIndex].Height,
                Monitor.pModeList[ModeIndex].VSync,
                IDDCX_MONITOR_MODE_ORIGIN_MONITORDESCRIPTOR
            );
        }

        // Set the preferred mode as represented in the EDID
        pOutArgs->PreferredMonitorModeIdx = Monitor.ulPreferredModeIdx;

        return STATUS_SUCCESS;
    }
}

_Use_decl_annotations_
NTSTATUS IddSampleMonitorGetDefaultModes(IDDCX_MONITOR MonitorObject, const IDARG_IN_GETDEFAULTDESCRIPTIONMODES* pInArgs, IDARG_OUT_GETDEFAULTDESCRIPTIONMODES* pOutArgs)
{
    UNREFERENCED_PARAMETER(MonitorObject);

    // ==============================
    // TODO: In a real driver, this function would be called to generate monitor modes for a monitor with no EDID.
    // Drivers should report modes that are guaranteed to be supported by the transport protocol and by nearly all
    // monitors (such 640x480, 800x600, or 1024x768). If the driver has access to monitor modes from a descriptor other
    // than an EDID, those modes would also be reported here.
    // ==============================

    if (pInArgs->DefaultMonitorModeBufferInputCount == 0)
    {
        pOutArgs->DefaultMonitorModeBufferOutputCount = ARRAYSIZE(s_SampleDefaultModes); 
    }
    else if (pInArgs->DefaultMonitorModeBufferInputCount < ARRAYSIZE(s_SampleDefaultModes))
    {
        pOutArgs->DefaultMonitorModeBufferOutputCount = ARRAYSIZE(s_SampleDefaultModes);
        return STATUS_BUFFER_TOO_SMALL;
    }
    else
    {
        for (DWORD ModeIndex = 0; ModeIndex < ARRAYSIZE(s_SampleDefaultModes); ModeIndex++)
        {
            pInArgs->pDefaultMonitorModes[ModeIndex] = CreateIddCxMonitorMode(
                s_SampleDefaultModes[ModeIndex].Width,
                s_SampleDefaultModes[ModeIndex].Height,
                s_SampleDefaultModes[ModeIndex].VSync,
                IDDCX_MONITOR_MODE_ORIGIN_DRIVER
            );
        }

        pOutArgs->DefaultMonitorModeBufferOutputCount = ARRAYSIZE(s_SampleDefaultModes); 
        pOutArgs->PreferredMonitorModeIdx = 0;
    }

    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS IddSampleMonitorQueryModes(IDDCX_MONITOR MonitorObject, const IDARG_IN_QUERYTARGETMODES* pInArgs, IDARG_OUT_QUERYTARGETMODES* pOutArgs)
{
    UNREFERENCED_PARAMETER(MonitorObject);

    // Create a set of modes supported for frame processing and scan-out. These are typically not based on the
    // monitor's descriptor and instead are based on the static processing capability of the device. The OS will
    // report the available set of modes for a given output as the intersection of monitor modes with target modes.

    const UINT TargetModeCount = ARRAYSIZE(s_SampleDefaultModes);
    pOutArgs->TargetModeBufferOutputCount = TargetModeCount;

    if (pInArgs->TargetModeBufferInputCount >= TargetModeCount)
    {
        for (UINT ModeIndex = 0; ModeIndex < TargetModeCount; ++ModeIndex)
        {
            const auto& Mode = s_SampleDefaultModes[ModeIndex];
            pInArgs->pTargetModes[ModeIndex] = CreateIddCxTargetMode(Mode.Width, Mode.Height, Mode.VSync);
        }
    }

    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS IddSampleMonitorAssignSwapChain(IDDCX_MONITOR MonitorObject, const IDARG_IN_SETSWAPCHAIN* pInArgs)
{
    auto* pMonitorContextWrapper = WdfObjectGet_IndirectMonitorContextWrapper(MonitorObject);
    pMonitorContextWrapper->pContext->AssignSwapChain(pInArgs->hSwapChain, pInArgs->RenderAdapterLuid, pInArgs->hNextSurfaceAvailable);
    return STATUS_SUCCESS;
}

_Use_decl_annotations_
NTSTATUS IddSampleMonitorUnassignSwapChain(IDDCX_MONITOR MonitorObject)
{
    auto* pMonitorContextWrapper = WdfObjectGet_IndirectMonitorContextWrapper(MonitorObject);
    pMonitorContextWrapper->pContext->UnassignSwapChain();
    return STATUS_SUCCESS;
}

#pragma endregion

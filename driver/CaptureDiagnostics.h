#pragma once

#include <stddef.h>

namespace CaptureDiagnostics
{
constexpr wchar_t kMappingName[] = L"Global\\SBMSCaptureDiagnostics-v1";
constexpr UINT kMagic = 0x53434431;
constexpr UINT kVersion = 1;
constexpr UINT kDriverBuild = 0x20261105;
constexpr UINT kMonitorCount = 16;
constexpr UINT kEventCapacity = 256;
constexpr UINT kNoConnector = MAXUINT32;

enum class Kind : UINT
{
    DeviceAttach = 1, DeviceDetach, AdapterInit, RenderPreference,
    MonitorCreate, MonitorArrival, MonitorCleanup, Assign, D3DDevice,
    ActualAdapter, SetDevice, DrainStart, FirstFrame, Unassign,
    StopRequested, DrainStopped, Error, StopTimeout
};

enum class Stage : UINT
{
    None, DeviceInitialize, AdapterInit, MonitorCreate, MonitorArrival,
    Factory, EnumAdapter, D3DCreate, QueryDevice, ActualAdapter,
    SetDevice, Allocate, StopEvent, Thread, Acquire, FinishFrame,
    WaitFrame, SessionConfig
};

enum class State : UINT
{
    None, Assigned, Draining, FirstFrame, Stopping, Stopped, Failed,
    Unassigned, Removed
};

struct Header
{
    UINT magic;
    UINT version;
    UINT size;
    UINT eventCapacity;
    UINT64 sessionId;
    UINT requestedAdapterSet;
    ULONG requestedLow;
    LONG requestedHigh;
    volatile LONG sequence;
    UINT64 writtenEvents;
    UINT64 overwrittenEvents;
    UINT64 nextGeneration;
    UINT64 deviceGeneration;
    volatile LONG driverPid;
    LONG deviceStatus;
    UINT64 lastEventTick;
    UINT deviceAttached;
    UINT droppedEvents;
};

struct MonitorSnapshot
{
    UINT64 generation;
    UINT64 assignTick;
    UINT64 firstFrameTick;
    UINT64 lastEventTick;
    UINT active;
    State state;
    ULONG assignLow;
    LONG assignHigh;
    ULONG actualLow;
    LONG actualHigh;
    LONG createDeviceResult;
    LONG setDeviceResult;
    LONG lastError;
    Stage lastStage;
    UINT assignCount;
    UINT unassignCount;
    UINT stopCount;
    UINT firstFrameCount;
    UINT flags; // 1: D3D attempted, 2: SetDevice attempted, 4: actual LUID known, 8: first frame
    UINT driverBuild;
};

struct Event
{
    UINT64 serial;
    UINT64 tick;
    UINT64 deviceGeneration;
    UINT64 generation;
    UINT connector;
    Kind kind;
    LONG result;
    Stage stage;
    ULONG adapterLow;
    LONG adapterHigh;
    UINT64 detail;
};

struct Shared
{
    Header header;
    MonitorSnapshot monitors[kMonitorCount];
    Event events[kEventCapacity];
};

static_assert(sizeof(Header) == 96);
static_assert(offsetof(Header, sequence) == 36);
static_assert(offsetof(Header, driverPid) == 72);
static_assert(sizeof(MonitorSnapshot) == 96);
static_assert(offsetof(MonitorSnapshot, flags) == 88);
static_assert(sizeof(Event) == 64);
static_assert(offsetof(Shared, events) == 1632);
static_assert(sizeof(Shared) == 18016);

// A device opens once. References held by its adapter, monitors and detached
// drain workers pin that exact mapping until the final worker has exited.
class Session
{
public:
    static Session* Open()
    {
        HANDLE mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, kMappingName);
        if (mapping == nullptr) return nullptr;
        auto* shared = static_cast<Shared*>(MapViewOfFile(
            mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(Shared)));
        if (shared == nullptr)
        {
            CloseHandle(mapping);
            return nullptr;
        }
        if (shared->header.magic != kMagic || shared->header.version != kVersion ||
            shared->header.size != sizeof(Shared) ||
            shared->header.eventCapacity != kEventCapacity ||
            shared->header.sessionId == 0 || shared->header.requestedAdapterSet > 1)
        {
            UnmapViewOfFile(shared);
            CloseHandle(mapping);
            return nullptr;
        }
        auto* session = new (std::nothrow) Session(mapping, shared);
        if (session == nullptr || InterlockedCompareExchange(
                &shared->header.driverPid, static_cast<LONG>(GetCurrentProcessId()), 0) != 0)
        {
            delete session;
            UnmapViewOfFile(shared);
            CloseHandle(mapping);
            return nullptr;
        }
        session->Record(Kind::DeviceAttach);
        return session;
    }

    void AddRef() { InterlockedIncrement(&references_); }

    void Release()
    {
        if (InterlockedDecrement(&references_) == 0)
        {
            InterlockedExchange(&shared_->header.driverPid, 0);
            UnmapViewOfFile(shared_);
            CloseHandle(mapping_);
            delete this;
        }
    }

    bool RequestedAdapter(LUID& luid) const
    {
        luid.LowPart = shared_->header.requestedLow;
        luid.HighPart = shared_->header.requestedHigh;
        return shared_->header.requestedAdapterSet != 0;
    }

    UINT64 Record(
        Kind kind, UINT connector = kNoConnector, UINT64 generation = 0,
        LONG result = 0, Stage stage = Stage::None, LUID adapter = {}, UINT64 detail = 0)
    {
        AcquireSRWLockExclusive(&lock_);
        auto& header = shared_->header;
        InterlockedIncrement(&header.sequence);
        const UINT64 tick = GetTickCount64();
        if (kind == Kind::DeviceAttach)
        {
            deviceGeneration_ = ++header.deviceGeneration;
            header.deviceAttached = 1;
            header.deviceStatus = 0;
            detail = kDriverBuild;
            for (auto& monitor : shared_->monitors)
            {
                monitor = {};
                monitor.driverBuild = kDriverBuild;
            }
        }
        if (kind == Kind::DeviceDetach)
        {
            header.deviceAttached = 0;
            for (auto& monitor : shared_->monitors)
            {
                monitor.active = 0;
                monitor.state = State::Removed;
            }
        }
        if (connector == kNoConnector && result < 0) header.deviceStatus = result;
        if (connector < kMonitorCount)
        {
            auto& current = shared_->monitors[connector];
            if (kind == Kind::Assign)
            {
                const UINT assigns = current.assignCount;
                const UINT unassigns = current.unassignCount;
                const UINT stops = current.stopCount;
                const UINT frames = current.firstFrameCount;
                current = {};
                current.driverBuild = kDriverBuild;
                current.generation = generation = ++header.nextGeneration;
                current.assignTick = tick;
                current.active = 1;
                current.state = State::Assigned;
                current.assignLow = adapter.LowPart;
                current.assignHigh = adapter.HighPart;
                current.assignCount = assigns + 1;
                current.unassignCount = unassigns;
                current.stopCount = stops;
                current.firstFrameCount = frames;
            }
            // A late event from a detached drain remains in history but cannot
            // rewrite the state of its replacement assignment.
            if (current.generation == generation)
            {
                current.lastEventTick = tick;
                if (stage != Stage::None) current.lastStage = stage;
                switch (kind)
                {
                case Kind::D3DDevice:
                    current.flags |= 1;
                    current.createDeviceResult = result;
                    break;
                case Kind::ActualAdapter:
                    if (result >= 0)
                    {
                        current.flags |= 4;
                        current.actualLow = adapter.LowPart;
                        current.actualHigh = adapter.HighPart;
                    }
                    break;
                case Kind::SetDevice:
                    current.flags |= 2;
                    current.setDeviceResult = result;
                    break;
                case Kind::DrainStart:
                    if (current.active) current.state = State::Draining;
                    break;
                case Kind::FirstFrame:
                    if (current.active)
                    {
                        current.flags |= 8;
                        current.firstFrameTick = tick;
                        ++current.firstFrameCount;
                        current.state = State::FirstFrame;
                    }
                    break;
                case Kind::Unassign:
                    ++current.unassignCount;
                    current.active = 0;
                    current.state = State::Unassigned;
                    break;
                case Kind::MonitorCleanup:
                    current.active = 0;
                    current.state = State::Removed;
                    break;
                case Kind::StopRequested:
                    ++current.stopCount;
                    if (current.active) current.state = State::Stopping;
                    current.active = 0;
                    break;
                case Kind::DrainStopped:
                    current.active = 0;
                    if (current.state != State::Unassigned && current.state != State::Removed &&
                        current.state != State::Failed) current.state = State::Stopped;
                    break;
                case Kind::Error:
                    current.lastError = result;
                    current.active = 0;
                    if (current.state != State::Unassigned && current.state != State::Removed)
                        current.state = State::Failed;
                    break;
                default:
                    break;
                }
            }
        }
        const UINT64 serial = ++header.writtenEvents;
        if (serial > kEventCapacity) ++header.overwrittenEvents;
        shared_->events[(serial - 1) % kEventCapacity] = {
            serial, tick, deviceGeneration_, generation, connector, kind,
            result, stage, adapter.LowPart, adapter.HighPart, detail};
        header.lastEventTick = tick;
        InterlockedIncrement(&header.sequence);
        ReleaseSRWLockExclusive(&lock_);
        return generation;
    }

private:
    Session(HANDLE mapping, Shared* shared) : mapping_(mapping), shared_(shared) {}
    HANDLE mapping_;
    Shared* shared_;
    SRWLOCK lock_ = SRWLOCK_INIT;
    volatile LONG references_ = 1;
    UINT64 deviceGeneration_ = 0;
};
}

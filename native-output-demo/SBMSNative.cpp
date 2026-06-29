#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#include <windows.h>
#include <windowsx.h>
#include <d3d11.h>
#include <d3dcompiler.h>
#include <dxgi1_2.h>
#include <wrl/client.h>

#include <algorithm>
#include <chrono>
#include <cstdint>
#include <iostream>
#include <sstream>
#include <stdexcept>
#include <string>
#include <vector>

using Microsoft::WRL::ComPtr;

struct DisplayInfo {
    std::wstring name;
    std::wstring text;
    RECT rect{};
    DWORD frequency = 0;
    bool primary = false;
};

struct DxOutputInfo {
    UINT adapterIndex = 0;
    UINT outputIndex = 0;
    std::wstring name;
    RECT rect{};
    ComPtr<IDXGIAdapter1> adapter;
    ComPtr<IDXGIOutput> output;
    ComPtr<IDXGIOutput1> output1;
};

struct WindowMoveRecord {
    HWND hwnd = nullptr;
    RECT originalRect{};
};

struct WindowMigrationState {
    bool enabled = true;
    bool active = false;
    RECT fromRect{};
    RECT toRect{};
    std::chrono::steady_clock::time_point lastScan{};
    std::vector<WindowMoveRecord> moved;
};

static bool g_running = true;
static WindowMigrationState g_windowMigration;

static void Check(HRESULT hr, const char* what) {
    if (FAILED(hr)) {
        std::ostringstream os;
        os << what << " failed: 0x" << std::hex << static_cast<unsigned long>(hr);
        throw std::runtime_error(os.str());
    }
}

static int Width(const RECT& r) {
    return r.right - r.left;
}

static int Height(const RECT& r) {
    return r.bottom - r.top;
}

static bool IsEmptyRect(const RECT& rect) {
    return Width(rect) <= 0 || Height(rect) <= 0;
}

static POINT RectCenter(const RECT& rect) {
    POINT point{};
    point.x = rect.left + Width(rect) / 2;
    point.y = rect.top + Height(rect) / 2;
    return point;
}

struct InputMapperState {
    bool enabled = true;
    bool capture = false;
    HWND hwnd = nullptr;
    HHOOK mouseHook = nullptr;
    HHOOK keyboardHook = nullptr;
    RECT sourceRect{};
    RECT targetRect{};
    RECT returnRect{};
    POINT targetCursor{};
    bool cursorHidden = false;
    bool cursorClipped = false;
};

static InputMapperState g_input;

static LRESULT CALLBACK LowLevelMouseProc(int code, WPARAM wparam, LPARAM lparam);
static LRESULT CALLBACK LowLevelKeyboardProc(int code, WPARAM wparam, LPARAM lparam);

static double ClampDouble(double value, double low, double high) {
    return std::max(low, std::min(value, high));
}

static bool PointInRect(const RECT& rect, POINT point) {
    return point.x >= rect.left && point.x < rect.right && point.y >= rect.top && point.y < rect.bottom;
}

static bool PointInInputZone(POINT point) {
    return PointInRect(g_input.targetRect, point) || PointInRect(g_input.sourceRect, point);
}

static void SetCursorClip(bool enable) {
    if (enable == g_input.cursorClipped) {
        return;
    }
    if (enable) {
        RECT clipRect = g_input.sourceRect;
        if (Width(clipRect) > 0 && Height(clipRect) > 0 && ClipCursor(&clipRect)) {
            g_input.cursorClipped = true;
        }
        return;
    }
    ClipCursor(nullptr);
    g_input.cursorClipped = false;
}

static bool InstallInputHooks() {
    if (!g_input.mouseHook) {
        g_input.mouseHook = SetWindowsHookExW(WH_MOUSE_LL, LowLevelMouseProc, GetModuleHandleW(nullptr), 0);
    }
    if (!g_input.keyboardHook) {
        g_input.keyboardHook = SetWindowsHookExW(WH_KEYBOARD_LL, LowLevelKeyboardProc, GetModuleHandleW(nullptr), 0);
    }
    return g_input.mouseHook && g_input.keyboardHook;
}

static void UninstallInputHooks() {
    if (g_input.mouseHook) {
        UnhookWindowsHookEx(g_input.mouseHook);
        g_input.mouseHook = nullptr;
    }
    if (g_input.keyboardHook) {
        UnhookWindowsHookEx(g_input.keyboardHook);
        g_input.keyboardHook = nullptr;
    }
}

static POINT SourcePointFromTargetCursor() {
    const double targetW = static_cast<double>(std::max(Width(g_input.targetRect), 1));
    const double targetH = static_cast<double>(std::max(Height(g_input.targetRect), 1));
    const double sourceW = static_cast<double>(std::max(Width(g_input.sourceRect), 1));
    const double sourceH = static_cast<double>(std::max(Height(g_input.sourceRect), 1));

    POINT point{};
    point.x = g_input.sourceRect.left + static_cast<LONG>((static_cast<double>(g_input.targetCursor.x) / targetW) * sourceW);
    point.y = g_input.sourceRect.top + static_cast<LONG>((static_cast<double>(g_input.targetCursor.y) / targetH) * sourceH);
    point.x = static_cast<LONG>(ClampDouble(point.x, g_input.sourceRect.left, g_input.sourceRect.right - 1.0));
    point.y = static_cast<LONG>(ClampDouble(point.y, g_input.sourceRect.top, g_input.sourceRect.bottom - 1.0));
    return point;
}

static void SendAbsoluteMouseAtSource(DWORD flags, DWORD mouseData = 0) {
    POINT sourcePoint = SourcePointFromTargetCursor();
    const int vx = GetSystemMetrics(SM_XVIRTUALSCREEN);
    const int vy = GetSystemMetrics(SM_YVIRTUALSCREEN);
    const int vw = std::max(GetSystemMetrics(SM_CXVIRTUALSCREEN), 1);
    const int vh = std::max(GetSystemMetrics(SM_CYVIRTUALSCREEN), 1);

    INPUT input{};
    input.type = INPUT_MOUSE;
    input.mi.dx = static_cast<LONG>((static_cast<double>(sourcePoint.x - vx) * 65535.0) / static_cast<double>(std::max(vw - 1, 1)));
    input.mi.dy = static_cast<LONG>((static_cast<double>(sourcePoint.y - vy) * 65535.0) / static_cast<double>(std::max(vh - 1, 1)));
    input.mi.mouseData = mouseData;
    input.mi.dwFlags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK | MOUSEEVENTF_MOVE | flags;
    SendInput(1, &input, sizeof(input));
}

static void HideMappedCursor(bool hide) {
    if (hide == g_input.cursorHidden) {
        return;
    }
    ShowCursor(hide ? FALSE : TRUE);
    g_input.cursorHidden = hide;
}

static void ReturnCursorToRealDesktop() {
    RECT rect = g_input.returnRect;
    if (Width(rect) <= 0 || Height(rect) <= 0) {
        rect = g_input.targetRect;
    }
    SetCursorPos(rect.left + Width(rect) / 2, rect.top + Height(rect) / 2);
}

static void StopInputCapture(bool returnToDesktop = true) {
    POINT preservePoint{};
    GetCursorPos(&preservePoint);
    if (!g_input.capture) {
        SetCursorClip(false);
        UninstallInputHooks();
        return;
    }
    SendAbsoluteMouseAtSource(MOUSEEVENTF_LEFTUP | MOUSEEVENTF_RIGHTUP | MOUSEEVENTF_MIDDLEUP);
    g_input.capture = false;
    SetCursorClip(false);
    UninstallInputHooks();
    HideMappedCursor(false);
    if (returnToDesktop) {
        ReturnCursorToRealDesktop();
    } else {
        SetCursorPos(preservePoint.x, preservePoint.y);
    }
    std::cout << "input_capture=off\n";
}

static void CleanupInputMapper() {
    StopInputCapture();
    UninstallInputHooks();
    SetCursorClip(false);
    HideMappedCursor(false);
}

static RECT MapRectBetweenDisplays(const RECT& rect, const RECT& fromRect, const RECT& toRect) {
    const double fromW = static_cast<double>(std::max(Width(fromRect), 1));
    const double fromH = static_cast<double>(std::max(Height(fromRect), 1));
    const double sx = static_cast<double>(std::max(Width(toRect), 1)) / fromW;
    const double sy = static_cast<double>(std::max(Height(toRect), 1)) / fromH;

    RECT mapped{};
    mapped.left = toRect.left + static_cast<LONG>((rect.left - fromRect.left) * sx);
    mapped.top = toRect.top + static_cast<LONG>((rect.top - fromRect.top) * sy);
    mapped.right = toRect.left + static_cast<LONG>((rect.right - fromRect.left) * sx);
    mapped.bottom = toRect.top + static_cast<LONG>((rect.bottom - fromRect.top) * sy);

    const LONG minW = 160;
    const LONG minH = 90;
    if (Width(mapped) < minW) {
        mapped.right = mapped.left + minW;
    }
    if (Height(mapped) < minH) {
        mapped.bottom = mapped.top + minH;
    }
    if (mapped.right > toRect.right) {
        const LONG width = Width(mapped);
        mapped.right = toRect.right;
        mapped.left = mapped.right - width;
    }
    if (mapped.bottom > toRect.bottom) {
        const LONG height = Height(mapped);
        mapped.bottom = toRect.bottom;
        mapped.top = mapped.bottom - height;
    }
    if (mapped.left < toRect.left) {
        const LONG width = Width(mapped);
        mapped.left = toRect.left;
        mapped.right = mapped.left + width;
    }
    if (mapped.top < toRect.top) {
        const LONG height = Height(mapped);
        mapped.top = toRect.top;
        mapped.bottom = mapped.top + height;
    }
    return mapped;
}

static bool IsShellOrSystemWindow(HWND hwnd) {
    wchar_t className[128]{};
    GetClassNameW(hwnd, className, ARRAYSIZE(className));
    std::wstring klass = className;
    return klass == L"Progman" ||
           klass == L"WorkerW" ||
           klass == L"Shell_TrayWnd" ||
           klass == L"Shell_SecondaryTrayWnd" ||
           klass == L"Button";
}

static std::wstring ProcessFileNameForWindow(HWND hwnd) {
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid == 0) {
        return L"";
    }
    HANDLE process = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (!process) {
        return L"";
    }
    wchar_t path[MAX_PATH]{};
    DWORD size = ARRAYSIZE(path);
    std::wstring fileName;
    if (QueryFullProcessImageNameW(process, 0, path, &size)) {
        std::wstring fullPath(path, size);
        size_t slash = fullPath.find_last_of(L"\\/");
        fileName = slash == std::wstring::npos ? fullPath : fullPath.substr(slash + 1);
    }
    CloseHandle(process);
    return fileName;
}

static bool EqualsIgnoreCase(const std::wstring& left, const wchar_t* right) {
    return CompareStringOrdinal(left.c_str(), -1, right, -1, TRUE) == CSTR_EQUAL;
}

static bool IsScreenCaptureWindow(HWND hwnd) {
    std::wstring processName = ProcessFileNameForWindow(hwnd);
    if (EqualsIgnoreCase(processName, L"ScreenClippingHost.exe") ||
        EqualsIgnoreCase(processName, L"SnippingTool.exe") ||
        EqualsIgnoreCase(processName, L"ScreenSketch.exe") ||
        EqualsIgnoreCase(processName, L"ShellExperienceHost.exe") ||
        EqualsIgnoreCase(processName, L"StartMenuExperienceHost.exe")) {
        return true;
    }
    return false;
}

static bool IsMovableTopLevelWindow(HWND hwnd) {
    if (!IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd)) {
        return false;
    }
    if (GetAncestor(hwnd, GA_ROOT) != hwnd) {
        return false;
    }
    if (GetWindow(hwnd, GW_OWNER) != nullptr) {
        return false;
    }
    LONG_PTR exStyle = GetWindowLongPtrW(hwnd, GWL_EXSTYLE);
    if ((exStyle & WS_EX_TOOLWINDOW) != 0) {
        return false;
    }
    if (IsShellOrSystemWindow(hwnd)) {
        return false;
    }
    if (IsScreenCaptureWindow(hwnd)) {
        return false;
    }
    DWORD pid = 0;
    GetWindowThreadProcessId(hwnd, &pid);
    if (pid == GetCurrentProcessId()) {
        return false;
    }
    RECT rect{};
    if (!GetWindowRect(hwnd, &rect) || IsEmptyRect(rect)) {
        return false;
    }
    return true;
}

static WindowMoveRecord* FindWindowMoveRecord(HWND hwnd) {
    auto it = std::find_if(
        g_windowMigration.moved.begin(),
        g_windowMigration.moved.end(),
        [hwnd](const WindowMoveRecord& record) {
            return record.hwnd == hwnd;
        });
    return it == g_windowMigration.moved.end() ? nullptr : &(*it);
}

static BOOL CALLBACK MoveWindowsToSourceProc(HWND hwnd, LPARAM) {
    if (!IsMovableTopLevelWindow(hwnd)) {
        return TRUE;
    }

    RECT rect{};
    GetWindowRect(hwnd, &rect);
    if (!PointInRect(g_windowMigration.fromRect, RectCenter(rect))) {
        return TRUE;
    }

    WindowMoveRecord* existingRecord = FindWindowMoveRecord(hwnd);

    RECT mapped = MapRectBetweenDisplays(rect, g_windowMigration.fromRect, g_windowMigration.toRect);
    SetWindowPos(
        hwnd,
        nullptr,
        mapped.left,
        mapped.top,
        Width(mapped),
        Height(mapped),
        SWP_NOZORDER | SWP_NOACTIVATE);
    if (!existingRecord) {
        g_windowMigration.moved.push_back({hwnd, rect});
    }
    return TRUE;
}

static size_t MoveVisibleWindowsFromRealDesktop() {
    const size_t before = g_windowMigration.moved.size();
    EnumWindows(MoveWindowsToSourceProc, 0);
    return g_windowMigration.moved.size() - before;
}

static void MoveTargetWindowsToVirtual(const RECT& targetRect, const RECT& sourceRect) {
    if (!g_windowMigration.enabled) {
        return;
    }
    g_windowMigration.active = true;
    g_windowMigration.fromRect = targetRect;
    g_windowMigration.toRect = sourceRect;
    g_windowMigration.moved.clear();
    g_windowMigration.lastScan = std::chrono::steady_clock::now();
    MoveVisibleWindowsFromRealDesktop();
    std::cout << "window_migration=moved_to_virtual count=" << g_windowMigration.moved.size() << "\n";
}

static void PumpWindowMigration() {
    if (!g_windowMigration.enabled || !g_windowMigration.active) {
        return;
    }
    auto now = std::chrono::steady_clock::now();
    if (g_windowMigration.lastScan.time_since_epoch().count() != 0) {
        auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(now - g_windowMigration.lastScan);
        if (elapsed.count() < 250) {
            return;
        }
    }
    g_windowMigration.lastScan = now;
    const size_t moved = MoveVisibleWindowsFromRealDesktop();
    if (moved > 0) {
        std::cout << "window_migration=runtime_moved count=" << moved << "\n";
    }
}

static void RestoreMigratedWindows() {
    if (!g_windowMigration.active) {
        return;
    }
    size_t restored = 0;
    for (const auto& record : g_windowMigration.moved) {
        HWND hwnd = record.hwnd;
        if (!IsWindow(hwnd) || !IsWindowVisible(hwnd) || IsIconic(hwnd)) {
            continue;
        }
        RECT rect{};
        if (!GetWindowRect(hwnd, &rect) || IsEmptyRect(rect)) {
            continue;
        }
        RECT mapped = PointInRect(g_windowMigration.toRect, RectCenter(rect))
                          ? MapRectBetweenDisplays(rect, g_windowMigration.toRect, g_windowMigration.fromRect)
                          : record.originalRect;
        SetWindowPos(
            hwnd,
            nullptr,
            mapped.left,
            mapped.top,
            Width(mapped),
            Height(mapped),
            SWP_NOZORDER | SWP_NOACTIVATE);
        ++restored;
    }
    std::cout << "window_migration=restored count=" << restored << "\n";
    g_windowMigration.moved.clear();
    g_windowMigration.active = false;
}

static void StartInputCapture(HWND hwnd, int clientX, int clientY) {
    POINT current{};
    GetCursorPos(&current);
    if (!PointInRect(g_input.targetRect, current)) {
        return;
    }
    if (!InstallInputHooks()) {
        std::cerr << "input_capture_error=SetWindowsHookExW\n";
        UninstallInputHooks();
        return;
    }
    g_input.capture = true;
    g_input.hwnd = hwnd;
    g_input.targetCursor.x = static_cast<LONG>(ClampDouble(clientX, 0.0, std::max(Width(g_input.targetRect) - 1.0, 0.0)));
    g_input.targetCursor.y = static_cast<LONG>(ClampDouble(clientY, 0.0, std::max(Height(g_input.targetRect) - 1.0, 0.0)));
    SetCursorClip(true);
    HideMappedCursor(true);
    SetForegroundWindow(hwnd);
    SendAbsoluteMouseAtSource(0);
    std::cout << "input_capture=on mapped_cursor="
              << g_input.targetCursor.x << "," << g_input.targetCursor.y << "\n";
}

static void ApplyRawMouseDelta(LONG dx, LONG dy) {
    if (!g_input.capture || (dx == 0 && dy == 0)) {
        return;
    }
    POINT current{};
    if (GetCursorPos(&current) && !PointInInputZone(current)) {
        StopInputCapture(false);
        return;
    }
    g_input.targetCursor.x = static_cast<LONG>(ClampDouble(
        g_input.targetCursor.x + dx,
        0.0,
        std::max(Width(g_input.targetRect) - 1.0, 0.0)));
    g_input.targetCursor.y = static_cast<LONG>(ClampDouble(
        g_input.targetCursor.y + dy,
        0.0,
        std::max(Height(g_input.targetRect) - 1.0, 0.0)));
    SendAbsoluteMouseAtSource(0);
}

static void HandleRawInput(LPARAM lparam) {
    UINT size = 0;
    if (GetRawInputData(reinterpret_cast<HRAWINPUT>(lparam), RID_INPUT, nullptr, &size, sizeof(RAWINPUTHEADER)) != 0 || size == 0) {
        return;
    }
    std::vector<BYTE> buffer(size);
    if (GetRawInputData(reinterpret_cast<HRAWINPUT>(lparam), RID_INPUT, buffer.data(), &size, sizeof(RAWINPUTHEADER)) != size) {
        return;
    }

    const auto* raw = reinterpret_cast<const RAWINPUT*>(buffer.data());
    if (raw->header.dwType != RIM_TYPEMOUSE) {
        return;
    }
    if ((raw->data.mouse.usFlags & MOUSE_MOVE_ABSOLUTE) != 0) {
        return;
    }
    ApplyRawMouseDelta(raw->data.mouse.lLastX, raw->data.mouse.lLastY);
}

static void RegisterRawMouse(HWND hwnd) {
    RAWINPUTDEVICE device{};
    device.usUsagePage = 0x01;
    device.usUsage = 0x02;
    device.dwFlags = RIDEV_INPUTSINK;
    device.hwndTarget = hwnd;
    if (!RegisterRawInputDevices(&device, 1, sizeof(device))) {
        throw std::runtime_error("RegisterRawInputDevices failed");
    }
}

static DWORD MouseFlagFromMessage(WPARAM message, const MSLLHOOKSTRUCT& event) {
    switch (message) {
    case WM_LBUTTONDOWN:
        return MOUSEEVENTF_LEFTDOWN;
    case WM_LBUTTONUP:
        return MOUSEEVENTF_LEFTUP;
    case WM_RBUTTONDOWN:
        return MOUSEEVENTF_RIGHTDOWN;
    case WM_RBUTTONUP:
        return MOUSEEVENTF_RIGHTUP;
    case WM_MBUTTONDOWN:
        return MOUSEEVENTF_MIDDLEDOWN;
    case WM_MBUTTONUP:
        return MOUSEEVENTF_MIDDLEUP;
    case WM_MOUSEWHEEL:
        return MOUSEEVENTF_WHEEL;
    case WM_MOUSEHWHEEL:
        return MOUSEEVENTF_HWHEEL;
    case WM_XBUTTONDOWN:
        return HIWORD(event.mouseData) == XBUTTON1 ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XDOWN;
    case WM_XBUTTONUP:
        return HIWORD(event.mouseData) == XBUTTON1 ? MOUSEEVENTF_XUP : MOUSEEVENTF_XUP;
    default:
        return 0;
    }
}

static LRESULT CALLBACK LowLevelMouseProc(int code, WPARAM wparam, LPARAM lparam) {
    if (code < 0 || !g_input.enabled || !g_input.capture) {
        return CallNextHookEx(g_input.mouseHook, code, wparam, lparam);
    }

    const auto* event = reinterpret_cast<MSLLHOOKSTRUCT*>(lparam);
    if ((event->flags & LLMHF_INJECTED) != 0) {
        return CallNextHookEx(g_input.mouseHook, code, wparam, lparam);
    }

    if (!PointInInputZone(event->pt)) {
        StopInputCapture(false);
        return CallNextHookEx(g_input.mouseHook, code, wparam, lparam);
    }

    if (wparam == WM_MOUSEMOVE) {
        return 1;
    }

    DWORD flags = MouseFlagFromMessage(wparam, *event);
    if (flags != 0) {
        DWORD mouseData = 0;
        if (wparam == WM_MOUSEWHEEL || wparam == WM_MOUSEHWHEEL || wparam == WM_XBUTTONDOWN || wparam == WM_XBUTTONUP) {
            mouseData = HIWORD(event->mouseData);
        }
        SendAbsoluteMouseAtSource(flags, mouseData);
        return 1;
    }

    return CallNextHookEx(g_input.mouseHook, code, wparam, lparam);
}

static bool IsVirtualKeyDown(int vk) {
    return (GetAsyncKeyState(vk) & 0x8000) != 0;
}

static bool IsWinShiftSHotkey(const KBDLLHOOKSTRUCT& event, WPARAM wparam) {
    if (wparam != WM_KEYDOWN && wparam != WM_SYSKEYDOWN) {
        return false;
    }
    if (event.vkCode != 'S') {
        return false;
    }
    bool winDown = IsVirtualKeyDown(VK_LWIN) || IsVirtualKeyDown(VK_RWIN);
    bool shiftDown = IsVirtualKeyDown(VK_SHIFT) || IsVirtualKeyDown(VK_LSHIFT) || IsVirtualKeyDown(VK_RSHIFT);
    return winDown && shiftDown;
}

static bool IsScreenshotReleaseHotkey(const KBDLLHOOKSTRUCT& event, WPARAM wparam) {
    if (wparam != WM_KEYDOWN && wparam != WM_SYSKEYDOWN) {
        return false;
    }
    if (event.vkCode == VK_SNAPSHOT) {
        return true;
    }
    return IsWinShiftSHotkey(event, wparam);
}

static LRESULT CALLBACK LowLevelKeyboardProc(int code, WPARAM wparam, LPARAM lparam) {
    if (code < 0) {
        return CallNextHookEx(g_input.keyboardHook, code, wparam, lparam);
    }
    const auto* event = reinterpret_cast<KBDLLHOOKSTRUCT*>(lparam);
    if (g_input.capture && IsScreenshotReleaseHotkey(*event, wparam)) {
        std::cout << "input_capture_hotkey=screenshot\n";
        StopInputCapture(false);
        return CallNextHookEx(nullptr, code, wparam, lparam);
    }
    if (g_input.capture && (wparam == WM_KEYDOWN || wparam == WM_SYSKEYDOWN) && event->vkCode == VK_F8) {
        StopInputCapture();
        return 1;
    }
    return CallNextHookEx(g_input.keyboardHook, code, wparam, lparam);
}

static std::wstring ToLower(std::wstring value) {
    for (auto& ch : value) {
        ch = static_cast<wchar_t>(towlower(ch));
    }
    return value;
}

static bool TryParseResolution(const std::wstring& value, int& width, int& height) {
    auto pos = value.find(L'x');
    if (pos == std::wstring::npos) {
        pos = value.find(L'X');
    }
    if (pos == std::wstring::npos) {
        return false;
    }
    try {
        width = std::stoi(value.substr(0, pos));
        height = std::stoi(value.substr(pos + 1));
        return width > 0 && height > 0;
    } catch (...) {
        return false;
    }
}

static bool IsVirtualDisplay(const DisplayInfo& display) {
    std::wstring haystack = ToLower(display.name + L" " + display.text);
    return haystack.find(L"iddsample") != std::wstring::npos ||
           haystack.find(L"displaybridge") != std::wstring::npos ||
           haystack.find(L"sbms") != std::wstring::npos;
}

static std::vector<DisplayInfo> EnumDisplays() {
    std::vector<DisplayInfo> displays;
    for (DWORD i = 0;; ++i) {
        DISPLAY_DEVICEW device{};
        device.cb = sizeof(device);
        if (!EnumDisplayDevicesW(nullptr, i, &device, 0)) {
            break;
        }
        if ((device.StateFlags & DISPLAY_DEVICE_ACTIVE) == 0) {
            continue;
        }

        DEVMODEW mode{};
        mode.dmSize = sizeof(mode);
        if (!EnumDisplaySettingsW(device.DeviceName, ENUM_CURRENT_SETTINGS, &mode)) {
            continue;
        }

        DisplayInfo info;
        info.name = device.DeviceName;
        info.text = device.DeviceString;
        info.rect.left = mode.dmPosition.x;
        info.rect.top = mode.dmPosition.y;
        info.rect.right = mode.dmPosition.x + static_cast<LONG>(mode.dmPelsWidth);
        info.rect.bottom = mode.dmPosition.y + static_cast<LONG>(mode.dmPelsHeight);
        info.frequency = mode.dmDisplayFrequency;
        info.primary = (device.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE) != 0;
        displays.push_back(info);
    }
    return displays;
}

static DisplayInfo FindDisplay(const std::vector<DisplayInfo>& displays, const std::wstring& selector) {
    auto selectorLower = ToLower(selector);
    for (const auto& display : displays) {
        if (ToLower(display.name) == selectorLower) {
            return display;
        }
    }

    int wantedW = 0;
    int wantedH = 0;
    if (TryParseResolution(selector, wantedW, wantedH)) {
        std::vector<DisplayInfo> matches;
        for (const auto& display : displays) {
            if (Width(display.rect) == wantedW && Height(display.rect) == wantedH) {
                matches.push_back(display);
            }
        }
        if (matches.size() == 1) {
            return matches[0];
        }
        if (matches.size() > 1) {
            throw std::runtime_error("display selector matched multiple displays");
        }
    }

    for (const auto& display : displays) {
        if (ToLower(display.text).find(selectorLower) != std::wstring::npos) {
            return display;
        }
    }

    throw std::runtime_error("display selector did not match an active display");
}

static DisplayInfo FindSourceDisplay(const std::vector<DisplayInfo>& displays, const std::wstring& selector, bool allowPhysicalSource) {
    auto selectorLower = ToLower(selector);
    for (const auto& display : displays) {
        if (ToLower(display.name) == selectorLower) {
            if (!allowPhysicalSource && (display.primary || !IsVirtualDisplay(display))) {
                throw std::runtime_error("source selector matched a physical display; refusing to mirror the real desktop as source");
            }
            return display;
        }
    }

    int wantedW = 0;
    int wantedH = 0;
    if (TryParseResolution(selector, wantedW, wantedH)) {
        std::vector<DisplayInfo> virtualMatches;
        std::vector<DisplayInfo> physicalMatches;
        for (const auto& display : displays) {
            if (Width(display.rect) == wantedW && Height(display.rect) == wantedH) {
                if (IsVirtualDisplay(display) && !display.primary) {
                    virtualMatches.push_back(display);
                } else {
                    physicalMatches.push_back(display);
                }
            }
        }
        if (virtualMatches.size() == 1) {
            return virtualMatches[0];
        }
        if (virtualMatches.size() > 1) {
            throw std::runtime_error("source selector matched multiple virtual displays");
        }
        if (!allowPhysicalSource && !physicalMatches.empty()) {
            throw std::runtime_error("source resolution only matched physical displays; virtual display was not created or selected");
        }
        if (allowPhysicalSource && physicalMatches.size() == 1) {
            return physicalMatches[0];
        }
        if (allowPhysicalSource && physicalMatches.size() > 1) {
            throw std::runtime_error("source selector matched multiple physical displays");
        }
    }

    for (const auto& display : displays) {
        if (ToLower(display.text).find(selectorLower) != std::wstring::npos) {
            if (!allowPhysicalSource && (display.primary || !IsVirtualDisplay(display))) {
                throw std::runtime_error("source selector matched a physical display name; refusing to use it as source");
            }
            return display;
        }
    }

    throw std::runtime_error("source selector did not match an active virtual display");
}

static std::vector<DxOutputInfo> EnumDxOutputs() {
    std::vector<DxOutputInfo> outputs;
    ComPtr<IDXGIFactory1> factory;
    Check(CreateDXGIFactory1(IID_PPV_ARGS(&factory)), "CreateDXGIFactory1");

    for (UINT adapterIndex = 0;; ++adapterIndex) {
        ComPtr<IDXGIAdapter1> adapter;
        if (factory->EnumAdapters1(adapterIndex, &adapter) == DXGI_ERROR_NOT_FOUND) {
            break;
        }

        for (UINT outputIndex = 0;; ++outputIndex) {
            ComPtr<IDXGIOutput> output;
            if (adapter->EnumOutputs(outputIndex, &output) == DXGI_ERROR_NOT_FOUND) {
                break;
            }
            DXGI_OUTPUT_DESC desc{};
            Check(output->GetDesc(&desc), "IDXGIOutput::GetDesc");
            ComPtr<IDXGIOutput1> output1;
            if (FAILED(output.As(&output1))) {
                continue;
            }

            DxOutputInfo info;
            info.adapterIndex = adapterIndex;
            info.outputIndex = outputIndex;
            info.name = desc.DeviceName;
            info.rect = desc.DesktopCoordinates;
            info.adapter = adapter;
            info.output = output;
            info.output1 = output1;
            outputs.push_back(info);
        }
    }
    return outputs;
}

static DxOutputInfo FindDxOutputForDisplay(const std::vector<DxOutputInfo>& outputs, const DisplayInfo& display) {
    for (const auto& output : outputs) {
        if (output.name == display.name) {
            return output;
        }
    }
    for (const auto& output : outputs) {
        if (Width(output.rect) == Width(display.rect) && Height(output.rect) == Height(display.rect)) {
            return output;
        }
    }
    throw std::runtime_error("could not map display to a DXGI output");
}

static void PrintList() {
    std::wcout << L"Win32 displays:\n";
    for (const auto& display : EnumDisplays()) {
        std::wcout << L"  " << display.name << (display.primary ? L" primary" : L"")
                   << L": pos=" << display.rect.left << L"," << display.rect.top
                   << L" mode=" << Width(display.rect) << L"x" << Height(display.rect)
                   << L"@" << display.frequency << L" name=" << display.text << L"\n";
    }

    std::wcout << L"\nDXGI outputs:\n";
    for (const auto& output : EnumDxOutputs()) {
        std::wcout << L"  adapter=" << output.adapterIndex << L" output=" << output.outputIndex
                   << L" " << output.name
                   << L" pos=" << output.rect.left << L"," << output.rect.top
                   << L" mode=" << Width(output.rect) << L"x" << Height(output.rect) << L"\n";
    }
}

static LRESULT CALLBACK WindowProc(HWND hwnd, UINT msg, WPARAM wparam, LPARAM lparam) {
    switch (msg) {
    case WM_INPUT:
        if (g_input.enabled && g_input.capture) {
            HandleRawInput(lparam);
            return 0;
        }
        break;
    case WM_LBUTTONDOWN:
        if (g_input.enabled && !g_input.capture) {
            StartInputCapture(hwnd, GET_X_LPARAM(lparam), GET_Y_LPARAM(lparam));
            SendAbsoluteMouseAtSource(MOUSEEVENTF_LEFTDOWN);
            return 0;
        }
        break;
    case WM_KEYDOWN:
        if (wparam == VK_ESCAPE || wparam == 'Q') {
            g_running = false;
            DestroyWindow(hwnd);
            return 0;
        }
        break;
    case WM_CLOSE:
    case WM_DESTROY:
        StopInputCapture();
        g_running = false;
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(hwnd, msg, wparam, lparam);
}

static HWND CreateOutputWindow(HINSTANCE instance, const DisplayInfo& target) {
    const wchar_t* className = L"SBMSNativeOutput";
    WNDCLASSW wc{};
    wc.lpfnWndProc = WindowProc;
    wc.hInstance = instance;
    wc.lpszClassName = className;
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    RegisterClassW(&wc);

    HWND hwnd = CreateWindowExW(
        WS_EX_TOPMOST,
        className,
        L"SBMS Native Output",
        WS_POPUP,
        target.rect.left,
        target.rect.top,
        Width(target.rect),
        Height(target.rect),
        nullptr,
        nullptr,
        instance,
        nullptr);
    if (!hwnd) {
        throw std::runtime_error("CreateWindowExW failed");
    }
    ShowWindow(hwnd, SW_SHOW);
    UpdateWindow(hwnd);
    std::wcout << L"output_window "
               << L"x=" << target.rect.left
               << L" y=" << target.rect.top
               << L" w=" << Width(target.rect)
               << L" h=" << Height(target.rect)
               << L"\n";
    return hwnd;
}

static ComPtr<ID3DBlob> CompileShader(const char* source, const char* entry, const char* target) {
    ComPtr<ID3DBlob> blob;
    ComPtr<ID3DBlob> errors;
    HRESULT hr = D3DCompile(source, strlen(source), nullptr, nullptr, nullptr, entry, target, 0, 0, &blob, &errors);
    if (FAILED(hr)) {
        if (errors) {
            std::cerr << static_cast<const char*>(errors->GetBufferPointer()) << "\n";
        }
        Check(hr, "D3DCompile");
    }
    return blob;
}

struct Args {
    std::wstring source = L"4550x2560";
    std::wstring target = L"2560x1440";
    std::wstring filter = L"linear";
    bool list = false;
    bool vsync = false;
    bool input = true;
    bool moveWindows = true;
    bool allowPhysicalSource = false;
    int seconds = 0;
};

static constexpr int kTopologyChangedExitCode = 100;
static constexpr int kSourceUnavailableExitCode = 101;

static bool IsSourceUnavailableError(const std::string& message) {
    return message.find("source selector did not match an active virtual display") != std::string::npos;
}

static int FilterModeFromName(const std::wstring& name) {
    if (name == L"linear") {
        return 0;
    }
    if (name == L"point") {
        return 1;
    }
    if (name == L"box2x") {
        return 2;
    }
    std::wcerr << L"Unknown filter: " << name << L" (use linear, point, or box2x)\n";
    std::exit(2);
}

static void EnableDpiAwareness() {
    HMODULE user32 = GetModuleHandleW(L"user32.dll");
    if (user32) {
        using SetDpiContextFn = BOOL(WINAPI*)(DPI_AWARENESS_CONTEXT);
        auto setDpiContext = reinterpret_cast<SetDpiContextFn>(
            GetProcAddress(user32, "SetProcessDpiAwarenessContext"));
        if (setDpiContext && setDpiContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)) {
            return;
        }
    }
    SetProcessDPIAware();
}

static Args ParseArgs(int argc, wchar_t** argv) {
    Args args;
    for (int i = 1; i < argc; ++i) {
        std::wstring arg = argv[i];
        if (arg == L"--list") {
            args.list = true;
        } else if (arg == L"--source" && i + 1 < argc) {
            args.source = argv[++i];
        } else if (arg == L"--target" && i + 1 < argc) {
            args.target = argv[++i];
        } else if (arg == L"--filter" && i + 1 < argc) {
            args.filter = argv[++i];
        } else if (arg == L"--vsync") {
            args.vsync = true;
        } else if (arg == L"--no-input") {
            args.input = false;
        } else if (arg == L"--no-window-move") {
            args.moveWindows = false;
        } else if (arg == L"--allow-physical-source") {
            args.allowPhysicalSource = true;
        } else if (arg == L"--seconds" && i + 1 < argc) {
            args.seconds = std::stoi(argv[++i]);
            if (args.seconds < 0) {
                args.seconds = 0;
            }
        } else {
            std::wcerr << L"Unknown argument: " << arg << L"\n";
            std::exit(2);
        }
    }
    return args;
}

int wmain(int argc, wchar_t** argv) {
    try {
        EnableDpiAwareness();

        Args args = ParseArgs(argc, argv);
        if (args.list) {
            PrintList();
            return 0;
        }

        auto displays = EnumDisplays();
        DisplayInfo sourceDisplay = FindSourceDisplay(displays, args.source, args.allowPhysicalSource);
        DisplayInfo targetDisplay = FindDisplay(displays, args.target);
        if (ToLower(sourceDisplay.name) == ToLower(targetDisplay.name)) {
            throw std::runtime_error("source and target resolved to the same display");
        }
        int filterMode = FilterModeFromName(args.filter);
        auto sourceOutput = FindDxOutputForDisplay(EnumDxOutputs(), sourceDisplay);
        g_input.enabled = args.input;
        g_windowMigration.enabled = args.moveWindows;
        g_input.sourceRect = sourceDisplay.rect;
        g_input.targetRect = targetDisplay.rect;
        g_input.returnRect = targetDisplay.rect;
        for (const auto& display : displays) {
            if (display.primary) {
                g_input.returnRect = display.rect;
                break;
            }
        }
        g_input.targetCursor.x = Width(targetDisplay.rect) / 2;
        g_input.targetCursor.y = Height(targetDisplay.rect) / 2;

        std::wcout << L"source " << sourceDisplay.name << L" "
                   << Width(sourceDisplay.rect) << L"x" << Height(sourceDisplay.rect)
                   << L"@" << sourceDisplay.frequency << L"\n";
        std::wcout << L"target " << targetDisplay.name << L" "
                   << Width(targetDisplay.rect) << L"x" << Height(targetDisplay.rect)
                   << L"@" << targetDisplay.frequency << L"\n";
        std::wcout << L"dxgi adapter=" << sourceOutput.adapterIndex
                   << L" output=" << sourceOutput.outputIndex << L"\n";
        if (g_input.enabled) {
            std::cout << "input_mapper=on click the mirror window to capture; press F8 to release\n";
        } else {
            std::cout << "input_mapper=off\n";
        }
        std::cout << "window_migration=" << (g_windowMigration.enabled ? "on" : "off") << "\n";
        std::wcout << L"filter=" << args.filter << L"\n";

        MoveTargetWindowsToVirtual(targetDisplay.rect, sourceDisplay.rect);
        HWND hwnd = CreateOutputWindow(GetModuleHandleW(nullptr), targetDisplay);
        g_input.hwnd = hwnd;
        if (g_input.enabled) {
            RegisterRawMouse(hwnd);
        }

        UINT deviceFlags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
#if defined(_DEBUG)
        deviceFlags |= D3D11_CREATE_DEVICE_DEBUG;
#endif
        D3D_FEATURE_LEVEL levels[] = {D3D_FEATURE_LEVEL_11_1, D3D_FEATURE_LEVEL_11_0};
        D3D_FEATURE_LEVEL createdLevel{};
        ComPtr<ID3D11Device> device;
        ComPtr<ID3D11DeviceContext> context;
        Check(D3D11CreateDevice(
                  sourceOutput.adapter.Get(),
                  D3D_DRIVER_TYPE_UNKNOWN,
                  nullptr,
                  deviceFlags,
                  levels,
                  ARRAYSIZE(levels),
                  D3D11_SDK_VERSION,
                  &device,
                  &createdLevel,
                  &context),
              "D3D11CreateDevice");

        ComPtr<IDXGIOutputDuplication> duplication;
        Check(sourceOutput.output1->DuplicateOutput(device.Get(), &duplication), "DuplicateOutput");

        ComPtr<IDXGIDevice> dxgiDevice;
        Check(device.As(&dxgiDevice), "Query IDXGIDevice");
        ComPtr<IDXGIAdapter> adapter;
        Check(dxgiDevice->GetAdapter(&adapter), "GetAdapter");
        ComPtr<IDXGIFactory> factory;
        Check(adapter->GetParent(IID_PPV_ARGS(&factory)), "GetParent factory");

        DXGI_SWAP_CHAIN_DESC swapDesc{};
        swapDesc.BufferDesc.Width = Width(targetDisplay.rect);
        swapDesc.BufferDesc.Height = Height(targetDisplay.rect);
        swapDesc.BufferDesc.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        swapDesc.SampleDesc.Count = 1;
        swapDesc.BufferUsage = DXGI_USAGE_RENDER_TARGET_OUTPUT;
        swapDesc.BufferCount = 2;
        swapDesc.OutputWindow = hwnd;
        swapDesc.Windowed = TRUE;
        swapDesc.SwapEffect = DXGI_SWAP_EFFECT_DISCARD;

        ComPtr<IDXGISwapChain> swapChain;
        Check(factory->CreateSwapChain(device.Get(), &swapDesc, &swapChain), "CreateSwapChain");
        factory->MakeWindowAssociation(hwnd, DXGI_MWA_NO_ALT_ENTER);

        ComPtr<ID3D11Texture2D> backBuffer;
        Check(swapChain->GetBuffer(0, IID_PPV_ARGS(&backBuffer)), "GetBuffer backbuffer");
        ComPtr<ID3D11RenderTargetView> rtv;
        Check(device->CreateRenderTargetView(backBuffer.Get(), nullptr, &rtv), "CreateRenderTargetView");

        const char* shader = R"(
Texture2D sourceTex : register(t0);
SamplerState sourceSampler : register(s0);

cbuffer CursorBuffer : register(b0) {
    float4 cursor;
};

cbuffer RenderBuffer : register(b1) {
    float4 renderParams;
};

struct VSOut {
    float4 pos : SV_POSITION;
    float2 uv : TEXCOORD0;
};

VSOut VSMain(uint id : SV_VertexID) {
    float2 pos[3] = {
        float2(-1.0, -1.0),
        float2(-1.0,  3.0),
        float2( 3.0, -1.0)
    };
    VSOut output;
    output.pos = float4(pos[id], 0.0, 1.0);
    output.uv = float2((pos[id].x + 1.0) * 0.5, (1.0 - pos[id].y) * 0.5);
    return output;
}

float4 PointSample(float2 uv) {
    uint width;
    uint height;
    sourceTex.GetDimensions(width, height);
    uint2 coord = min(uint2(saturate(uv) * float2(width, height)), uint2(width - 1, height - 1));
    return sourceTex.Load(int3(coord, 0));
}

float4 Box2xSample(float2 pixelPos) {
    uint width;
    uint height;
    sourceTex.GetDimensions(width, height);
    uint2 targetPixel = uint2(max(pixelPos - 0.5, float2(0.0, 0.0)));
    uint2 base = targetPixel * 2;
    float4 color =
        sourceTex.Load(int3(min(base + uint2(0, 0), uint2(width - 1, height - 1)), 0)) +
        sourceTex.Load(int3(min(base + uint2(1, 0), uint2(width - 1, height - 1)), 0)) +
        sourceTex.Load(int3(min(base + uint2(0, 1), uint2(width - 1, height - 1)), 0)) +
        sourceTex.Load(int3(min(base + uint2(1, 1), uint2(width - 1, height - 1)), 0));
    return color * 0.25;
}

float4 PSMain(VSOut input) : SV_TARGET {
    int filterMode = (int)(renderParams.x + 0.5);
    float4 color = sourceTex.Sample(sourceSampler, input.uv);
    if (filterMode == 1) {
        color = PointSample(input.uv);
    } else if (filterMode == 2) {
        color = Box2xSample(input.pos.xy);
    }
    if (cursor.z > 0.5) {
        float2 delta = abs(input.pos.xy - cursor.xy);
        bool outer = (delta.x <= 5.0 && delta.y <= 18.0) || (delta.y <= 5.0 && delta.x <= 18.0);
        bool inner = (delta.x <= 2.0 && delta.y <= 14.0) || (delta.y <= 2.0 && delta.x <= 14.0);
        if (outer) {
            return inner ? float4(1.0, 1.0, 1.0, 1.0) : float4(0.0, 0.0, 0.0, 1.0);
        }
    }
    return color;
}
)";
        auto vsBlob = CompileShader(shader, "VSMain", "vs_5_0");
        auto psBlob = CompileShader(shader, "PSMain", "ps_5_0");
        ComPtr<ID3D11VertexShader> vs;
        ComPtr<ID3D11PixelShader> ps;
        Check(device->CreateVertexShader(vsBlob->GetBufferPointer(), vsBlob->GetBufferSize(), nullptr, &vs), "CreateVertexShader");
        Check(device->CreatePixelShader(psBlob->GetBufferPointer(), psBlob->GetBufferSize(), nullptr, &ps), "CreatePixelShader");

        D3D11_SAMPLER_DESC samplerDesc{};
        samplerDesc.Filter = D3D11_FILTER_MIN_MAG_MIP_LINEAR;
        samplerDesc.AddressU = D3D11_TEXTURE_ADDRESS_CLAMP;
        samplerDesc.AddressV = D3D11_TEXTURE_ADDRESS_CLAMP;
        samplerDesc.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        samplerDesc.MaxLOD = D3D11_FLOAT32_MAX;
        ComPtr<ID3D11SamplerState> sampler;
        Check(device->CreateSamplerState(&samplerDesc, &sampler), "CreateSamplerState");

        D3D11_BUFFER_DESC cursorBufferDesc{};
        cursorBufferDesc.ByteWidth = 16;
        cursorBufferDesc.Usage = D3D11_USAGE_DEFAULT;
        cursorBufferDesc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        ComPtr<ID3D11Buffer> cursorBuffer;
        Check(device->CreateBuffer(&cursorBufferDesc, nullptr, &cursorBuffer), "Create cursor constant buffer");

        D3D11_BUFFER_DESC renderBufferDesc{};
        renderBufferDesc.ByteWidth = 16;
        renderBufferDesc.Usage = D3D11_USAGE_DEFAULT;
        renderBufferDesc.BindFlags = D3D11_BIND_CONSTANT_BUFFER;
        ComPtr<ID3D11Buffer> renderBuffer;
        Check(device->CreateBuffer(&renderBufferDesc, nullptr, &renderBuffer), "Create render constant buffer");

        D3D11_VIEWPORT viewport{};
        viewport.Width = static_cast<float>(Width(targetDisplay.rect));
        viewport.Height = static_cast<float>(Height(targetDisplay.rect));
        viewport.MinDepth = 0.0f;
        viewport.MaxDepth = 1.0f;

        ComPtr<ID3D11Texture2D> shaderTexture;
        ComPtr<ID3D11ShaderResourceView> srv;
        UINT64 presented = 0;
        auto started = std::chrono::steady_clock::now();
        auto lastStat = std::chrono::steady_clock::now();

        while (g_running) {
            if (args.seconds > 0) {
                auto totalElapsed = std::chrono::duration<double>(std::chrono::steady_clock::now() - started);
                if (totalElapsed.count() >= args.seconds) {
                    break;
                }
            }

            MSG msg{};
            while (PeekMessageW(&msg, nullptr, 0, 0, PM_REMOVE)) {
                TranslateMessage(&msg);
                DispatchMessageW(&msg);
            }
            PumpWindowMigration();

            DXGI_OUTDUPL_FRAME_INFO frameInfo{};
            ComPtr<IDXGIResource> resource;
            HRESULT acquire = duplication->AcquireNextFrame(2, &frameInfo, &resource);
            if (acquire == DXGI_ERROR_WAIT_TIMEOUT) {
                continue;
            }
            if (acquire == DXGI_ERROR_ACCESS_LOST ||
                acquire == DXGI_ERROR_DEVICE_REMOVED ||
                acquire == DXGI_ERROR_DEVICE_RESET) {
                std::cerr << "topology_change=AcquireNextFrame hr=0x"
                          << std::hex << static_cast<unsigned int>(acquire)
                          << std::dec << "\n";
                CleanupInputMapper();
                RestoreMigratedWindows();
                return kTopologyChangedExitCode;
            }
            Check(acquire, "AcquireNextFrame");

            ComPtr<ID3D11Texture2D> acquiredTexture;
            Check(resource.As(&acquiredTexture), "Query acquired texture");
            D3D11_TEXTURE2D_DESC sourceDesc{};
            acquiredTexture->GetDesc(&sourceDesc);

            if (!shaderTexture) {
                D3D11_TEXTURE2D_DESC textureDesc = sourceDesc;
                textureDesc.BindFlags = D3D11_BIND_SHADER_RESOURCE;
                textureDesc.CPUAccessFlags = 0;
                textureDesc.MiscFlags = 0;
                textureDesc.Usage = D3D11_USAGE_DEFAULT;
                Check(device->CreateTexture2D(&textureDesc, nullptr, &shaderTexture), "Create shader texture");
                Check(device->CreateShaderResourceView(shaderTexture.Get(), nullptr, &srv), "Create shader resource view");
            }

            context->CopyResource(shaderTexture.Get(), acquiredTexture.Get());
            duplication->ReleaseFrame();

            FLOAT clear[] = {0.0f, 0.0f, 0.0f, 1.0f};
            context->OMSetRenderTargets(1, rtv.GetAddressOf(), nullptr);
            context->ClearRenderTargetView(rtv.Get(), clear);
            context->RSSetViewports(1, &viewport);
            context->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            context->VSSetShader(vs.Get(), nullptr, 0);
            context->PSSetShader(ps.Get(), nullptr, 0);
            context->PSSetSamplers(0, 1, sampler.GetAddressOf());
            context->PSSetShaderResources(0, 1, srv.GetAddressOf());
            struct CursorConstants {
                float x;
                float y;
                float enabled;
                float pad;
            } cursorConstants{
                static_cast<float>(g_input.targetCursor.x),
                static_cast<float>(g_input.targetCursor.y),
                g_input.capture ? 1.0f : 0.0f,
                0.0f};
            context->UpdateSubresource(cursorBuffer.Get(), 0, nullptr, &cursorConstants, 0, 0);
            context->PSSetConstantBuffers(0, 1, cursorBuffer.GetAddressOf());
            struct RenderConstants {
                float filterMode;
                float pad0;
                float pad1;
                float pad2;
            } renderConstants{static_cast<float>(filterMode), 0.0f, 0.0f, 0.0f};
            context->UpdateSubresource(renderBuffer.Get(), 0, nullptr, &renderConstants, 0, 0);
            context->PSSetConstantBuffers(1, 1, renderBuffer.GetAddressOf());
            context->Draw(3, 0);
            HRESULT present = swapChain->Present(args.vsync ? 1 : 0, 0);
            if (present == DXGI_ERROR_DEVICE_REMOVED ||
                present == DXGI_ERROR_DEVICE_RESET ||
                present == DXGI_ERROR_ACCESS_LOST) {
                std::cerr << "topology_change=Present hr=0x"
                          << std::hex << static_cast<unsigned int>(present)
                          << std::dec << "\n";
                CleanupInputMapper();
                RestoreMigratedWindows();
                return kTopologyChangedExitCode;
            }
            Check(present, "Present");

            ID3D11ShaderResourceView* nullSrv[] = {nullptr};
            context->PSSetShaderResources(0, 1, nullSrv);

            ++presented;
            auto now = std::chrono::steady_clock::now();
            std::chrono::duration<double> elapsed = now - lastStat;
            if (elapsed.count() >= 1.0) {
                std::cout << "present_fps=" << (presented / elapsed.count()) << "\n";
                presented = 0;
                lastStat = now;
            }
        }

        CleanupInputMapper();
        RestoreMigratedWindows();
        return 0;
    } catch (const std::exception& exc) {
        CleanupInputMapper();
        RestoreMigratedWindows();
        std::string message = exc.what();
        std::cerr << "error: " << message << "\n";
        /*
         * Issue #5: during multi-monitor mode changes Windows can briefly publish a
         * virtual display to one process and hide or renumber it for the next process.
         * The GUI already waits for the display to appear before launching native, but a
         * fresh native process may still hit this selector miss while the topology
         * transaction is settling. Return a dedicated code so the GUI can keep the
         * software-device host alive, re-enumerate current \\.\DISPLAYxx ids, and retry
         * instead of treating the race as a fatal native crash.
         */
        return IsSourceUnavailableError(message) ? kSourceUnavailableExitCode : 1;
    }
}

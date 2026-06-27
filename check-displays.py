import ctypes
from ctypes import wintypes
import sys


ENUM_CURRENT_SETTINGS = -1
DISPLAY_DEVICE_ACTIVE = 0x00000001
DISPLAY_DEVICE_PRIMARY_DEVICE = 0x00000004


class POINTL(ctypes.Structure):
    _fields_ = [
        ("x", ctypes.c_long),
        ("y", ctypes.c_long),
    ]


class DISPLAY_DEVICEW(ctypes.Structure):
    _fields_ = [
        ("cb", wintypes.DWORD),
        ("DeviceName", wintypes.WCHAR * 32),
        ("DeviceString", wintypes.WCHAR * 128),
        ("StateFlags", wintypes.DWORD),
        ("DeviceID", wintypes.WCHAR * 128),
        ("DeviceKey", wintypes.WCHAR * 128),
    ]


class DEVMODEW(ctypes.Structure):
    _fields_ = [
        ("dmDeviceName", wintypes.WCHAR * 32),
        ("dmSpecVersion", wintypes.WORD),
        ("dmDriverVersion", wintypes.WORD),
        ("dmSize", wintypes.WORD),
        ("dmDriverExtra", wintypes.WORD),
        ("dmFields", wintypes.DWORD),
        ("dmPosition", POINTL),
        ("dmDisplayOrientation", wintypes.DWORD),
        ("dmDisplayFixedOutput", wintypes.DWORD),
        ("dmColor", ctypes.c_short),
        ("dmDuplex", ctypes.c_short),
        ("dmYResolution", ctypes.c_short),
        ("dmTTOption", ctypes.c_short),
        ("dmCollate", ctypes.c_short),
        ("dmFormName", wintypes.WCHAR * 32),
        ("dmLogPixels", wintypes.WORD),
        ("dmBitsPerPel", wintypes.DWORD),
        ("dmPelsWidth", wintypes.DWORD),
        ("dmPelsHeight", wintypes.DWORD),
        ("dmDisplayFlags", wintypes.DWORD),
        ("dmDisplayFrequency", wintypes.DWORD),
        ("dmICMMethod", wintypes.DWORD),
        ("dmICMIntent", wintypes.DWORD),
        ("dmMediaType", wintypes.DWORD),
        ("dmDitherType", wintypes.DWORD),
        ("dmReserved1", wintypes.DWORD),
        ("dmReserved2", wintypes.DWORD),
        ("dmPanningWidth", wintypes.DWORD),
        ("dmPanningHeight", wintypes.DWORD),
    ]


user32 = ctypes.WinDLL("user32", use_last_error=True)
EnumDisplayDevicesW = user32.EnumDisplayDevicesW
EnumDisplayDevicesW.argtypes = [
    wintypes.LPCWSTR,
    wintypes.DWORD,
    ctypes.POINTER(DISPLAY_DEVICEW),
    wintypes.DWORD,
]
EnumDisplayDevicesW.restype = wintypes.BOOL

EnumDisplaySettingsW = user32.EnumDisplaySettingsW
EnumDisplaySettingsW.argtypes = [
    wintypes.LPCWSTR,
    wintypes.DWORD,
    ctypes.POINTER(DEVMODEW),
]
EnumDisplaySettingsW.restype = wintypes.BOOL


def main() -> int:
    found = False
    index = 0

    while True:
        device = DISPLAY_DEVICEW()
        device.cb = ctypes.sizeof(DISPLAY_DEVICEW)

        if not EnumDisplayDevicesW(None, index, ctypes.byref(device), 0):
            break

        index += 1

        if not (device.StateFlags & DISPLAY_DEVICE_ACTIVE):
            continue

        mode = DEVMODEW()
        mode.dmSize = ctypes.sizeof(DEVMODEW)

        if not EnumDisplaySettingsW(device.DeviceName, ENUM_CURRENT_SETTINGS, ctypes.byref(mode)):
            continue

        primary = bool(device.StateFlags & DISPLAY_DEVICE_PRIMARY_DEVICE)
        print(
            f"{device.DeviceName} primary={primary} "
            f"pos={mode.dmPosition.x},{mode.dmPosition.y} "
            f"mode={mode.dmPelsWidth}x{mode.dmPelsHeight}@{mode.dmDisplayFrequency} "
            f"name={device.DeviceString}"
        )

        if mode.dmPelsWidth == 4550 and mode.dmPelsHeight == 2560:
            found = True

    if found:
        print("PASS: Windows reports a 4550x2560 display mode.")
        return 0

    print("FAIL: no 4550x2560 display mode is currently reported.")
    return 1


if __name__ == "__main__":
    sys.exit(main())

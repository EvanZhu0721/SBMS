using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace SBMSGui
{
    internal struct WindowRecoveryRect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public int Width { get { return Right - Left; } }
        public int Height { get { return Bottom - Top; } }
    }

    internal struct WindowRecoveryPoint
    {
        public int X;
        public int Y;
    }

    internal struct WindowRecoveryPlacement
    {
        public uint Flags;
        public uint ShowCommand;
        public WindowRecoveryPoint MinPosition;
        public WindowRecoveryPoint MaxPosition;
        public WindowRecoveryRect NormalPosition;
    }

    internal sealed class WindowMigrationRecord
    {
        public string JournalVersion;
        public ulong WindowHandle;
        public uint ProcessId;
        public long ProcessCreationTime;
        public WindowRecoveryRect Original;
        public WindowRecoveryRect Migrated;
        public WindowRecoveryRect PhysicalDisplay;
        public WindowRecoveryRect VirtualDisplay;
        public bool HasOriginalPlacement;
        public WindowRecoveryPlacement OriginalPlacement;
    }

    internal interface IWindowRecoveryApi
    {
        bool TryGetIdentity(IntPtr window, out uint processId, out long processCreationTime);
        bool TryGetWindowRect(IntPtr window, out WindowRecoveryRect rect);
        bool TryGetWindowPlacement(IntPtr window, out WindowRecoveryPlacement placement);
        bool TryGetMonitorWorkArea(WindowRecoveryRect preferredRect, out WindowRecoveryRect workArea);
        bool TryRestoreWindow(
            IntPtr window,
            WindowRecoveryRect rect,
            bool restorePlacement,
            WindowRecoveryPlacement placement);
    }

    internal sealed class WindowMigrationRecoveryLease : IDisposable
    {
        private const string MutexPrefix = "Local\\SBMS.WindowRecovery.";
        private Mutex mutex;
        private bool ownsMutex;

        private WindowMigrationRecoveryLease(Mutex mutex)
        {
            this.mutex = mutex;
            ownsMutex = true;
        }

        public static bool TryAcquire(
            string sessionDirectory,
            int timeoutMilliseconds,
            out WindowMigrationRecoveryLease lease)
        {
            lease = null;
            if (string.IsNullOrWhiteSpace(sessionDirectory))
            {
                throw new ArgumentException("A recovery session directory is required.", "sessionDirectory");
            }
            if (timeoutMilliseconds < 0)
            {
                throw new ArgumentOutOfRangeException("timeoutMilliseconds");
            }

            string normalized = Path.GetFullPath(sessionDirectory)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .ToUpperInvariant();
            byte[] digest;
            using (SHA256 hash = SHA256.Create())
            {
                digest = hash.ComputeHash(Encoding.UTF8.GetBytes(normalized));
            }
            var name = new StringBuilder(MutexPrefix);
            for (int i = 0; i < digest.Length; ++i)
            {
                name.Append(digest[i].ToString("X2", CultureInfo.InvariantCulture));
            }

            var candidate = new Mutex(false, name.ToString());
            bool acquired;
            try
            {
                acquired = candidate.WaitOne(timeoutMilliseconds);
            }
            catch (AbandonedMutexException)
            {
                acquired = true;
            }
            if (!acquired)
            {
                candidate.Dispose();
                return false;
            }

            lease = new WindowMigrationRecoveryLease(candidate);
            return true;
        }

        public void Dispose()
        {
            Mutex owned = mutex;
            mutex = null;
            if (owned == null)
            {
                return;
            }
            if (ownsMutex)
            {
                ownsMutex = false;
                owned.ReleaseMutex();
            }
            owned.Dispose();
        }
    }

    internal sealed class WindowMigrationRecoveryResult
    {
        public int Prepared;
        public int Restored;
        public int AlreadyOriginal;
        public int Corrupt;
        public int IoFailures;
        public int Unresolved;
        public readonly List<string> FailureDetails = new List<string>();

        public void Merge(WindowMigrationRecoveryResult other)
        {
            if (other == null)
            {
                return;
            }
            Prepared += other.Prepared;
            Restored += other.Restored;
            AlreadyOriginal += other.AlreadyOriginal;
            Corrupt += other.Corrupt;
            IoFailures += other.IoFailures;
            Unresolved += other.Unresolved;
            FailureDetails.AddRange(other.FailureDetails);
        }

        internal void AddCorrupt(string path, int lineNumber)
        {
            ++Corrupt;
            ++Unresolved;
            FailureDetails.Add(
                "corrupt journal line path=" + (path ?? "") +
                " line=" + lineNumber.ToString(CultureInfo.InvariantCulture));
        }

        internal void AddIoFailure(string operation, string path, Exception error)
        {
            ++IoFailures;
            ++Unresolved;
            FailureDetails.Add(
                "journal " + operation +
                " failed path=" + (path ?? "") +
                " error=" + (error == null ? "unknown" : error.Message));
        }
    }

    internal sealed class WindowMigrationJournal
    {
        private sealed class JournalReadResult
        {
            public readonly List<WindowMigrationRecord> Pending =
                new List<WindowMigrationRecord>();
            public readonly List<int> CorruptLines = new List<int>();
        }

        private const string LegacyVersion = "SBMSWM1";
        private const string Version = "SBMSWM2";
        private readonly IWindowRecoveryApi windowApi;

        public WindowMigrationJournal(IWindowRecoveryApi windowApi)
        {
            if (windowApi == null)
            {
                throw new ArgumentNullException("windowApi");
            }
            this.windowApi = windowApi;
        }

        public WindowMigrationRecoveryResult RecoverDirectory(string directory)
        {
            var total = new WindowMigrationRecoveryResult();
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
            {
                return total;
            }

            string[] paths;
            try
            {
                paths = Directory.GetFiles(directory, "*.journal", SearchOption.TopDirectoryOnly);
            }
            catch (Exception ex)
            {
                if (!IsFileSystemFailure(ex))
                {
                    throw;
                }
                total.AddIoFailure("enumerate", directory, ex);
                return total;
            }

            foreach (string path in paths)
            {
                total.Merge(RecoverFile(path));
            }
            return total;
        }

        public WindowMigrationRecoveryResult RecoverFile(string path)
        {
            var result = new WindowMigrationRecoveryResult();
            JournalReadResult readResult;
            try
            {
                readResult = ReadPending(path);
            }
            catch (Exception ex)
            {
                if (!IsFileSystemFailure(ex))
                {
                    throw;
                }
                result.AddIoFailure("read", path, ex);
                return result;
            }

            for (int i = 0; i < readResult.CorruptLines.Count; ++i)
            {
                result.AddCorrupt(path, readResult.CorruptLines[i]);
            }

            List<WindowMigrationRecord> records = readResult.Pending;
            result.Prepared = records.Count;
            for (int i = 0; i < records.Count; ++i)
            {
                WindowMigrationRecord record = records[i];
                IntPtr window = new IntPtr(unchecked((long)record.WindowHandle));
                uint processId;
                long creationTime;
                if (!windowApi.TryGetIdentity(window, out processId, out creationTime) ||
                    (processId != 0 &&
                     (processId != record.ProcessId ||
                      creationTime != record.ProcessCreationTime)))
                {
                    ++result.Unresolved;
                    continue;
                }
                if (processId == 0)
                {
                    ++result.AlreadyOriginal;
                    TryAppendResolved(path, record, result);
                    continue;
                }

                WindowRecoveryRect current;
                if (!windowApi.TryGetWindowRect(window, out current))
                {
                    ++result.Unresolved;
                    continue;
                }

                WindowRecoveryPlacement currentPlacement;
                bool hasCurrentPlacement =
                    windowApi.TryGetWindowPlacement(window, out currentPlacement);
                if (record.HasOriginalPlacement && !hasCurrentPlacement)
                {
                    ++result.Unresolved;
                    continue;
                }

                WindowRecoveryRect currentReference =
                    hasCurrentPlacement && IsUsableRect(currentPlacement.NormalPosition)
                        ? currentPlacement.NormalPosition
                        : current;
                WindowRecoveryRect originalReference =
                    record.HasOriginalPlacement
                        ? record.OriginalPlacement.NormalPosition
                        : record.Original;
                bool placementAlreadyOriginal =
                    !record.HasOriginalPlacement ||
                    NormalizeShowCommand(currentPlacement.ShowCommand) ==
                        NormalizeShowCommand(record.OriginalPlacement.ShowCommand);
                if ((RectEquals(current, record.Original) ||
                     RectEquals(currentReference, originalReference)) &&
                    placementAlreadyOriginal)
                {
                    ++result.AlreadyOriginal;
                    TryAppendResolved(path, record, result);
                    continue;
                }

                WindowRecoveryRect restoreRect;
                if (record.HasOriginalPlacement)
                {
                    bool knownMigrationState =
                        PointInRect(
                            record.VirtualDisplay,
                            RectCenterX(currentReference),
                            RectCenterY(currentReference)) ||
                        PointInRect(
                            record.VirtualDisplay,
                            RectCenterX(current),
                            RectCenterY(current)) ||
                        RectEquals(currentReference, record.Migrated) ||
                        RectEquals(current, record.Migrated) ||
                        RectEquals(currentReference, originalReference);
                    if (!knownMigrationState)
                    {
                        ++result.Unresolved;
                        continue;
                    }
                    restoreRect = record.OriginalPlacement.NormalPosition;
                }
                else if (PointInRect(record.VirtualDisplay, RectCenterX(current), RectCenterY(current)))
                {
                    restoreRect = MapRect(current, record.VirtualDisplay, record.PhysicalDisplay);
                }
                else if (RectEquals(current, record.Migrated))
                {
                    restoreRect = record.Original;
                }
                else
                {
                    ++result.Unresolved;
                    continue;
                }

                WindowRecoveryRect workArea;
                if (!windowApi.TryGetMonitorWorkArea(restoreRect, out workArea) ||
                    !IsUsableRect(workArea))
                {
                    ++result.Unresolved;
                    continue;
                }
                restoreRect = ClampRectToWorkArea(restoreRect, workArea);
                WindowRecoveryPlacement restorePlacement = record.OriginalPlacement;
                if (record.HasOriginalPlacement)
                {
                    restorePlacement.NormalPosition = restoreRect;
                }

                if (!windowApi.TryRestoreWindow(
                    window,
                    restoreRect,
                    record.HasOriginalPlacement,
                    restorePlacement))
                {
                    ++result.Unresolved;
                    continue;
                }

                ++result.Restored;
                TryAppendResolved(path, record, result);
            }

            if (result.Unresolved == 0 && File.Exists(path))
            {
                try
                {
                    File.Delete(path);
                }
                catch (Exception ex)
                {
                    if (!IsFileSystemFailure(ex))
                    {
                        throw;
                    }
                    result.AddIoFailure("delete", path, ex);
                }
            }
            return result;
        }

        public static string FormatPrepared(WindowMigrationRecord record)
        {
            return string.Join("|", new[]
            {
                Version,
                "P",
                record.WindowHandle.ToString("X", CultureInfo.InvariantCulture),
                record.ProcessId.ToString(CultureInfo.InvariantCulture),
                record.ProcessCreationTime.ToString("X", CultureInfo.InvariantCulture),
                FormatRect(record.Original),
                FormatRect(record.Migrated),
                FormatRect(record.PhysicalDisplay),
                FormatRect(record.VirtualDisplay),
                FormatPlacement(record.HasOriginalPlacement
                    ? record.OriginalPlacement
                    : CreateNormalPlacement(record.Original))
            });
        }

        private static JournalReadResult ReadPending(string path)
        {
            var result = new JournalReadResult();
            var pending = new Dictionary<string, WindowMigrationRecord>(StringComparer.Ordinal);
            if (!File.Exists(path))
            {
                return result;
            }

            string[] lines = File.ReadAllLines(path, Encoding.UTF8);
            for (int lineIndex = 0; lineIndex < lines.Length; ++lineIndex)
            {
                string line = lines[lineIndex];
                if (string.IsNullOrWhiteSpace(line))
                {
                    continue;
                }

                string[] parts = line.Split('|');
                bool supportedVersion =
                    parts.Length >= 1 &&
                    (parts[0] == Version || parts[0] == LegacyVersion);
                if (parts.Length < 5 || !supportedVersion)
                {
                    result.CorruptLines.Add(lineIndex + 1);
                    continue;
                }

                if (parts[1] == "R")
                {
                    ulong windowHandle;
                    uint processId;
                    long processCreationTime;
                    if (parts.Length != 5 ||
                        !TryParseIdentity(
                            parts,
                            out windowHandle,
                            out processId,
                            out processCreationTime))
                    {
                        result.CorruptLines.Add(lineIndex + 1);
                        continue;
                    }
                    pending.Remove(BuildIdentityKey(windowHandle, processId, processCreationTime));
                    continue;
                }
                bool validPreparedLength =
                    (parts[0] == LegacyVersion && parts.Length == 9) ||
                    (parts[0] == Version && parts.Length == 10);
                if (parts[1] != "P" || !validPreparedLength)
                {
                    result.CorruptLines.Add(lineIndex + 1);
                    continue;
                }

                WindowMigrationRecord record;
                if (TryParsePrepared(parts, out record))
                {
                    pending[BuildIdentityKey(
                        record.WindowHandle,
                        record.ProcessId,
                        record.ProcessCreationTime)] = record;
                }
                else
                {
                    result.CorruptLines.Add(lineIndex + 1);
                }
            }
            result.Pending.AddRange(pending.Values);
            return result;
        }

        private static bool TryParsePrepared(string[] parts, out WindowMigrationRecord record)
        {
            record = null;
            ulong windowHandle;
            uint processId;
            long creationTime;
            WindowRecoveryRect original;
            WindowRecoveryRect migrated;
            WindowRecoveryRect physicalDisplay;
            WindowRecoveryRect virtualDisplay;
            WindowRecoveryPlacement originalPlacement = new WindowRecoveryPlacement();
            bool hasOriginalPlacement = parts[0] == Version;
            if (!TryParseIdentity(parts, out windowHandle, out processId, out creationTime) ||
                !TryParseRect(parts[5], out original) ||
                !TryParseRect(parts[6], out migrated) ||
                !TryParseRect(parts[7], out physicalDisplay) ||
                !TryParseRect(parts[8], out virtualDisplay) ||
                (hasOriginalPlacement &&
                 !TryParsePlacement(parts[9], out originalPlacement)))
            {
                return false;
            }

            record = new WindowMigrationRecord
            {
                JournalVersion = parts[0],
                WindowHandle = windowHandle,
                ProcessId = processId,
                ProcessCreationTime = creationTime,
                Original = original,
                Migrated = migrated,
                PhysicalDisplay = physicalDisplay,
                VirtualDisplay = virtualDisplay,
                HasOriginalPlacement = hasOriginalPlacement,
                OriginalPlacement = originalPlacement
            };
            return true;
        }

        private static bool TryParseIdentity(
            string[] parts,
            out ulong windowHandle,
            out uint processId,
            out long processCreationTime)
        {
            windowHandle = 0;
            processId = 0;
            processCreationTime = 0;
            return parts.Length >= 5 &&
                   ulong.TryParse(
                       parts[2],
                       NumberStyles.HexNumber,
                       CultureInfo.InvariantCulture,
                       out windowHandle) &&
                   uint.TryParse(
                       parts[3],
                       NumberStyles.Integer,
                       CultureInfo.InvariantCulture,
                       out processId) &&
                   long.TryParse(
                       parts[4],
                       NumberStyles.HexNumber,
                       CultureInfo.InvariantCulture,
                       out processCreationTime);
        }

        private static string BuildIdentityKey(
            ulong windowHandle,
            uint processId,
            long processCreationTime)
        {
            return windowHandle.ToString("X", CultureInfo.InvariantCulture) + "|" +
                   processId.ToString(CultureInfo.InvariantCulture) + "|" +
                   processCreationTime.ToString("X", CultureInfo.InvariantCulture);
        }

        private static bool TryAppendResolved(
            string path,
            WindowMigrationRecord record,
            WindowMigrationRecoveryResult result)
        {
            try
            {
                AppendResolved(path, record);
                return true;
            }
            catch (Exception ex)
            {
                if (!IsFileSystemFailure(ex))
                {
                    throw;
                }
                result.AddIoFailure("append", path, ex);
                return false;
            }
        }

        private static bool IsFileSystemFailure(Exception error)
        {
            return error is IOException ||
                   error is UnauthorizedAccessException ||
                   error is System.Security.SecurityException ||
                   error is ArgumentException ||
                   error is NotSupportedException;
        }

        private static void AppendResolved(string path, WindowMigrationRecord record)
        {
            string line = string.Join("|", new[]
            {
                string.IsNullOrEmpty(record.JournalVersion) ? Version : record.JournalVersion,
                "R",
                record.WindowHandle.ToString("X", CultureInfo.InvariantCulture),
                record.ProcessId.ToString(CultureInfo.InvariantCulture),
                record.ProcessCreationTime.ToString("X", CultureInfo.InvariantCulture)
            });
            using (var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
            using (var writer = new StreamWriter(stream, new UTF8Encoding(false)))
            {
                writer.WriteLine(line);
                writer.Flush();
                stream.Flush(true);
            }
        }

        private static string FormatRect(WindowRecoveryRect rect)
        {
            return rect.Left.ToString(CultureInfo.InvariantCulture) + "," +
                   rect.Top.ToString(CultureInfo.InvariantCulture) + "," +
                   rect.Right.ToString(CultureInfo.InvariantCulture) + "," +
                   rect.Bottom.ToString(CultureInfo.InvariantCulture);
        }

        private static bool TryParseRect(string value, out WindowRecoveryRect rect)
        {
            rect = new WindowRecoveryRect();
            string[] parts = value.Split(',');
            return parts.Length == 4 &&
                   int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out rect.Left) &&
                   int.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out rect.Top) &&
                   int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out rect.Right) &&
                   int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out rect.Bottom);
        }

        private static string FormatPlacement(WindowRecoveryPlacement placement)
        {
            return placement.Flags.ToString(CultureInfo.InvariantCulture) + "," +
                   placement.ShowCommand.ToString(CultureInfo.InvariantCulture) + "," +
                   placement.MinPosition.X.ToString(CultureInfo.InvariantCulture) + "," +
                   placement.MinPosition.Y.ToString(CultureInfo.InvariantCulture) + "," +
                   placement.MaxPosition.X.ToString(CultureInfo.InvariantCulture) + "," +
                   placement.MaxPosition.Y.ToString(CultureInfo.InvariantCulture) + "," +
                   FormatRect(placement.NormalPosition);
        }

        private static bool TryParsePlacement(
            string value,
            out WindowRecoveryPlacement placement)
        {
            placement = new WindowRecoveryPlacement();
            string[] parts = value.Split(',');
            return parts.Length == 10 &&
                   uint.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out placement.Flags) &&
                   uint.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out placement.ShowCommand) &&
                   int.TryParse(parts[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out placement.MinPosition.X) &&
                   int.TryParse(parts[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out placement.MinPosition.Y) &&
                   int.TryParse(parts[4], NumberStyles.Integer, CultureInfo.InvariantCulture, out placement.MaxPosition.X) &&
                   int.TryParse(parts[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out placement.MaxPosition.Y) &&
                   int.TryParse(parts[6], NumberStyles.Integer, CultureInfo.InvariantCulture, out placement.NormalPosition.Left) &&
                   int.TryParse(parts[7], NumberStyles.Integer, CultureInfo.InvariantCulture, out placement.NormalPosition.Top) &&
                   int.TryParse(parts[8], NumberStyles.Integer, CultureInfo.InvariantCulture, out placement.NormalPosition.Right) &&
                   int.TryParse(parts[9], NumberStyles.Integer, CultureInfo.InvariantCulture, out placement.NormalPosition.Bottom);
        }

        private static WindowRecoveryPlacement CreateNormalPlacement(WindowRecoveryRect rect)
        {
            return new WindowRecoveryPlacement
            {
                ShowCommand = 1,
                NormalPosition = rect
            };
        }

        private static uint NormalizeShowCommand(uint showCommand)
        {
            return showCommand == 2 || showCommand == 6 || showCommand == 7
                ? 2U
                : showCommand == 3
                    ? 3U
                    : 1U;
        }

        private static bool IsUsableRect(WindowRecoveryRect rect)
        {
            return rect.Width > 0 && rect.Height > 0;
        }

        internal static WindowRecoveryRect ClampRectToWorkArea(
            WindowRecoveryRect rect,
            WindowRecoveryRect workArea)
        {
            if (!IsUsableRect(workArea))
            {
                return rect;
            }
            int width = Math.Min(Math.Max(1, rect.Width), workArea.Width);
            int height = Math.Min(Math.Max(1, rect.Height), workArea.Height);
            int left = Math.Max(workArea.Left, Math.Min(rect.Left, workArea.Right - width));
            int top = Math.Max(workArea.Top, Math.Min(rect.Top, workArea.Bottom - height));
            return new WindowRecoveryRect
            {
                Left = left,
                Top = top,
                Right = left + width,
                Bottom = top + height
            };
        }

        private static bool RectEquals(WindowRecoveryRect left, WindowRecoveryRect right)
        {
            return left.Left == right.Left &&
                   left.Top == right.Top &&
                   left.Right == right.Right &&
                   left.Bottom == right.Bottom;
        }

        private static int RectCenterX(WindowRecoveryRect rect)
        {
            return rect.Left + rect.Width / 2;
        }

        private static int RectCenterY(WindowRecoveryRect rect)
        {
            return rect.Top + rect.Height / 2;
        }

        private static bool PointInRect(WindowRecoveryRect rect, int x, int y)
        {
            return x >= rect.Left && x < rect.Right && y >= rect.Top && y < rect.Bottom;
        }

        private static WindowRecoveryRect MapRect(
            WindowRecoveryRect rect,
            WindowRecoveryRect from,
            WindowRecoveryRect to)
        {
            double scaleX = from.Width == 0 ? 1.0 : (double)to.Width / from.Width;
            double scaleY = from.Height == 0 ? 1.0 : (double)to.Height / from.Height;
            int left = to.Left + (int)Math.Round((rect.Left - from.Left) * scaleX);
            int top = to.Top + (int)Math.Round((rect.Top - from.Top) * scaleY);
            int width = Math.Max(1, (int)Math.Round(rect.Width * scaleX));
            int height = Math.Max(1, (int)Math.Round(rect.Height * scaleY));
            return new WindowRecoveryRect
            {
                Left = left,
                Top = top,
                Right = left + width,
                Bottom = top + height
            };
        }
    }

    internal sealed class Win32WindowRecoveryApi : IWindowRecoveryApi
    {
        private const uint ProcessQueryLimitedInformation = 0x1000;
        private const uint SwpNoZOrder = 0x0004;
        private const uint SwpNoActivate = 0x0010;
        private const uint MonitorDefaultToNearest = 0x00000002;

        public bool TryGetIdentity(IntPtr window, out uint processId, out long processCreationTime)
        {
            processId = 0;
            processCreationTime = 0;
            if (!IsWindow(window))
            {
                return true;
            }
            GetWindowThreadProcessId(window, out processId);
            if (processId == 0)
            {
                return false;
            }

            IntPtr process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
            if (process == IntPtr.Zero)
            {
                return false;
            }
            try
            {
                FILETIME creation;
                FILETIME exit;
                FILETIME kernel;
                FILETIME user;
                if (!GetProcessTimes(process, out creation, out exit, out kernel, out user))
                {
                    return false;
                }
                processCreationTime = ((long)creation.High << 32) | creation.Low;
                return true;
            }
            finally
            {
                CloseHandle(process);
            }
        }

        public bool TryGetWindowRect(IntPtr window, out WindowRecoveryRect rect)
        {
            RECT native;
            if (!GetWindowRect(window, out native))
            {
                rect = new WindowRecoveryRect();
                return false;
            }
            rect = FromNative(native);
            return true;
        }

        public bool TryGetWindowPlacement(
            IntPtr window,
            out WindowRecoveryPlacement placement)
        {
            var native = new WINDOWPLACEMENT();
            native.Length = Marshal.SizeOf(typeof(WINDOWPLACEMENT));
            if (!GetWindowPlacement(window, ref native))
            {
                placement = new WindowRecoveryPlacement();
                return false;
            }
            placement = new WindowRecoveryPlacement
            {
                Flags = native.Flags,
                ShowCommand = native.ShowCommand,
                MinPosition = FromNative(native.MinPosition),
                MaxPosition = FromNative(native.MaxPosition),
                NormalPosition = FromNative(native.NormalPosition)
            };
            return true;
        }

        public bool TryGetMonitorWorkArea(
            WindowRecoveryRect preferredRect,
            out WindowRecoveryRect workArea)
        {
            RECT nativeRect = ToNative(preferredRect);
            IntPtr monitor = MonitorFromRect(ref nativeRect, MonitorDefaultToNearest);
            if (monitor == IntPtr.Zero)
            {
                workArea = new WindowRecoveryRect();
                return false;
            }
            var info = new MONITORINFO();
            info.Size = Marshal.SizeOf(typeof(MONITORINFO));
            if (!GetMonitorInfo(monitor, ref info))
            {
                workArea = new WindowRecoveryRect();
                return false;
            }
            workArea = FromNative(info.WorkArea);
            return true;
        }

        public bool TryRestoreWindow(
            IntPtr window,
            WindowRecoveryRect rect,
            bool restorePlacement,
            WindowRecoveryPlacement placement)
        {
            if (restorePlacement)
            {
                var native = new WINDOWPLACEMENT
                {
                    Length = Marshal.SizeOf(typeof(WINDOWPLACEMENT)),
                    Flags = placement.Flags,
                    ShowCommand = placement.ShowCommand,
                    MinPosition = ToNative(placement.MinPosition),
                    MaxPosition = ToNative(placement.MaxPosition),
                    NormalPosition = ToNative(placement.NormalPosition)
                };
                return SetWindowPlacement(window, ref native);
            }
            return SetWindowPos(
                window,
                IntPtr.Zero,
                rect.Left,
                rect.Top,
                Math.Max(1, rect.Width),
                Math.Max(1, rect.Height),
                SwpNoZOrder | SwpNoActivate);
        }

        private static WindowRecoveryPoint FromNative(POINT point)
        {
            return new WindowRecoveryPoint { X = point.X, Y = point.Y };
        }

        private static POINT ToNative(WindowRecoveryPoint point)
        {
            return new POINT { X = point.X, Y = point.Y };
        }

        private static RECT ToNative(WindowRecoveryRect rect)
        {
            return new RECT
            {
                Left = rect.Left,
                Top = rect.Top,
                Right = rect.Right,
                Bottom = rect.Bottom
            };
        }

        private static WindowRecoveryRect FromNative(RECT rect)
        {
            return new WindowRecoveryRect
            {
                Left = rect.Left,
                Top = rect.Top,
                Right = rect.Right,
                Bottom = rect.Bottom
            };
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPLACEMENT
        {
            public int Length;
            public uint Flags;
            public uint ShowCommand;
            public POINT MinPosition;
            public POINT MaxPosition;
            public RECT NormalPosition;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int Size;
            public RECT Monitor;
            public RECT WorkArea;
            public uint Flags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct FILETIME
        {
            public uint Low;
            public uint High;
        }

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr window, out RECT rect);

        [DllImport("user32.dll")]
        private static extern bool GetWindowPlacement(IntPtr window, ref WINDOWPLACEMENT placement);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPlacement(IntPtr window, ref WINDOWPLACEMENT placement);

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromRect(ref RECT rect, uint flags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(
            IntPtr window,
            IntPtr insertAfter,
            int x,
            int y,
            int width,
            int height,
            uint flags);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint desiredAccess, bool inheritHandle, uint processId);

        [DllImport("kernel32.dll")]
        private static extern bool GetProcessTimes(
            IntPtr process,
            out FILETIME creation,
            out FILETIME exit,
            out FILETIME kernel,
            out FILETIME user);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);
    }
}

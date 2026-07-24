using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.Win32.SafeHandles;

namespace SBMSGui
{
    internal sealed class ChildProcessJob : IDisposable
    {
        private const uint JobObjectLimitKillOnJobClose = 0x00002000;
        private const int JobObjectExtendedLimitInformation = 9;

        private SafeFileHandle handle;

        public ChildProcessJob()
        {
            handle = CreateJobObject(IntPtr.Zero, null);
            if (handle == null || handle.IsInvalid)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "CreateJobObject failed.");
            }

            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(limits, buffer, false);
                if (!SetInformationJobObject(
                    handle,
                    JobObjectExtendedLimitInformation,
                    buffer,
                    (uint)size))
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "SetInformationJobObject(KILL_ON_JOB_CLOSE) failed.");
                }
            }
            catch
            {
                Dispose();
                throw;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        public bool TryAssign(Process process, out string error)
        {
            error = "";
            if (process == null)
            {
                error = "Cannot assign a null process.";
                return false;
            }
            if (handle == null || handle.IsClosed || handle.IsInvalid)
            {
                error = "The child-process job is not available.";
                return false;
            }

            try
            {
                if (!AssignProcessToJobObject(handle, process.Handle))
                {
                    error = new Win32Exception(Marshal.GetLastWin32Error()).Message;
                    return false;
                }
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        public void Dispose()
        {
            SafeFileHandle current = handle;
            handle = null;
            if (current != null)
            {
                current.Dispose();
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit;
            public long PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize;
            public UIntPtr MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass;
            public uint SchedulingClass;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct IO_COUNTERS
        {
            public ulong ReadOperationCount;
            public ulong WriteOperationCount;
            public ulong OtherOperationCount;
            public ulong ReadTransferCount;
            public ulong WriteTransferCount;
            public ulong OtherTransferCount;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
            public IO_COUNTERS IoInfo;
            public UIntPtr ProcessMemoryLimit;
            public UIntPtr JobMemoryLimit;
            public UIntPtr PeakProcessMemoryUsed;
            public UIntPtr PeakJobMemoryUsed;
        }

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern SafeFileHandle CreateJobObject(
            IntPtr jobAttributes,
            string name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetInformationJobObject(
            SafeFileHandle job,
            int informationClass,
            IntPtr information,
            uint informationLength);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool AssignProcessToJobObject(
            SafeFileHandle job,
            IntPtr process);
    }

    internal sealed class ChildLaunchGate : IDisposable
    {
        private EventWaitHandle gate;

        public string Name { get; private set; }

        public ChildLaunchGate()
        {
            Name = "Local\\SBMS-ChildLaunch-" + Guid.NewGuid().ToString("N");
            bool createdNew;
            gate = new EventWaitHandle(
                false,
                EventResetMode.ManualReset,
                Name,
                out createdNew);
            if (!createdNew)
            {
                gate.Dispose();
                gate = null;
                throw new InvalidOperationException("A unique child launch gate could not be created.");
            }
        }

        public void Release()
        {
            if (gate == null)
            {
                throw new ObjectDisposedException("ChildLaunchGate");
            }
            gate.Set();
        }

        public void Dispose()
        {
            EventWaitHandle current = gate;
            gate = null;
            if (current != null)
            {
                current.Dispose();
            }
        }
    }
}

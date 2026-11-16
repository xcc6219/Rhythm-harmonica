using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;

namespace HarmonicaStudio
{
    // The child is assigned while suspended: it cannot start unmanaged descendants.
    internal sealed class ProcessJob : IDisposable
    {
        private static readonly OwnedProcessLifetime applicationLifetime = new OwnedProcessLifetime();
        private readonly object sync = new object();
        private readonly OwnedProcessLifetime lifetime;
        private IntPtr job, process;
        private Task outputTask, errorTask;
        private bool disposed;
        public int Id { get; private set; }

        private ProcessJob(OwnedProcessLifetime owner) { lifetime = owner; }

        internal static void ShutdownOwnedJobs() { applicationLifetime.Shutdown(); }

        public static ProcessJob Start(ProcessStartInfo info, Action<string> output, Action<string> error)
        { return applicationLifetime.Start(info, output, error); }

        internal static ProcessJob StartOwned(ProcessStartInfo info, Action<string> output, Action<string> error, OwnedProcessLifetime owner)
        {
            if (info == null) throw new ArgumentNullException("info");
            if (info.UseShellExecute) throw new ArgumentException("后台任务须直接启动程序。", "info");
            if (!File.Exists(info.FileName)) throw new FileNotFoundException("后台程序不存在。", info.FileName);
            var result = new ProcessJob(owner);
            IntPtr readOutput = IntPtr.Zero, writeOutput = IntPtr.Zero;
            IntPtr readError = IntPtr.Zero, writeError = IntPtr.Zero, input = IntPtr.Zero, environment = IntPtr.Zero;
            var native = new ProcessInformation();
            IntPtr attributeList = IntPtr.Zero, inheritedHandles = IntPtr.Zero;
            bool assigned = false, attributesInitialized = false;
            try
            {
                result.job = CreateJobObject(IntPtr.Zero, null);
                if (result.job == IntPtr.Zero) throw NativeError("无法创建后台任务。");
                var limits = new ExtendedLimitInformation();
                limits.BasicLimitInformation.LimitFlags = 0x2000; // KILL_ON_JOB_CLOSE
                if (!SetInformationJobObject(result.job, 9, ref limits, (uint)Marshal.SizeOf(typeof(ExtendedLimitInformation))))
                    throw NativeError("无法管理后台任务。");
                var security = new SecurityAttributes { Length = Marshal.SizeOf(typeof(SecurityAttributes)), InheritHandle = true };
                if (!CreatePipe(out readOutput, out writeOutput, ref security, 0) ||
                    !CreatePipe(out readError, out writeError, ref security, 0)) throw NativeError("无法读取后台进度。");
                if (!SetHandleInformation(readOutput, 1, 0) || !SetHandleInformation(readError, 1, 0))
                    throw NativeError("无法设置后台进度。");
                input = CreateFile("NUL", 0x80000000, 3, ref security, 3, 0, IntPtr.Zero);
                if (input == new IntPtr(-1)) throw NativeError("无法设置后台输入。");
                UIntPtr attributeBytes = UIntPtr.Zero;
                InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref attributeBytes);
                if (attributeBytes == UIntPtr.Zero || attributeBytes.ToUInt64() > 65536)
                    throw NativeError("无法限定后台任务的句柄继承。");
                attributeList = Marshal.AllocHGlobal((int)attributeBytes.ToUInt64());
                if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeBytes))
                    throw NativeError("无法准备后台任务的标准流。");
                attributesInitialized = true;
                inheritedHandles = Marshal.AllocHGlobal(3 * IntPtr.Size);
                Marshal.WriteIntPtr(inheritedHandles, 0, input);
                Marshal.WriteIntPtr(inheritedHandles, IntPtr.Size, writeOutput);
                Marshal.WriteIntPtr(inheritedHandles, 2 * IntPtr.Size, writeError);
                if (!UpdateProcThreadAttribute(attributeList, 0, new UIntPtr(0x20002), inheritedHandles,
                    new UIntPtr((uint)(3 * IntPtr.Size)), IntPtr.Zero, IntPtr.Zero))
                    throw NativeError("无法限定后台任务的标准流。");
                var startup = new StartupInformationEx
                {
                    Startup = new StartupInformation
                    {
                        Size = Marshal.SizeOf(typeof(StartupInformationEx)), Flags = 0x100 | 1,
                        ShowWindow = 0, StandardInput = input, StandardOutput = writeOutput, StandardError = writeError
                    },
                    AttributeList = attributeList
                };
                environment = Marshal.StringToHGlobalUni(EnvironmentBlock(info));
                var command = new StringBuilder(AudioMidiConverter.Quote(info.FileName) + " " + (info.Arguments ?? ""));
                if (!CreateProcess(info.FileName, command, IntPtr.Zero, IntPtr.Zero, true,
                    0x4 | 0x8000000 | 0x400 | 0x80000, environment, info.WorkingDirectory, ref startup, out native))
                    throw NativeError("无法启动后台任务。");
                result.process = native.Process;
                result.Id = unchecked((int)native.ProcessId);
                if (!AssignProcessToJobObject(result.job, native.Process))
                    throw NativeError("无法安全管理后台任务，请重新打开软件后重试。");
                assigned = true;
                Close(ref writeOutput); Close(ref writeError); Close(ref input);
                result.outputTask = ReadLines(readOutput, info.StandardOutputEncoding ?? Encoding.UTF8, output);
                readOutput = IntPtr.Zero;
                result.errorTask = ReadLines(readError, info.StandardErrorEncoding ?? Encoding.UTF8, error);
                readError = IntPtr.Zero;
                if (ResumeThread(native.Thread) == UInt32.MaxValue) throw NativeError("无法运行后台任务。");
                return result;
            }
            catch
            {
                // On assignment failure this process is still suspended and has no children.
                if (!assigned && native.Process != IntPtr.Zero)
                { TerminateProcess(native.Process, 1); WaitForSingleObject(native.Process, 2000); }
                result.Dispose(); throw;
            }
            finally
            {
                Close(ref native.Thread); Close(ref readOutput); Close(ref writeOutput);
                Close(ref readError); Close(ref writeError); Close(ref input);
                if (environment != IntPtr.Zero) Marshal.FreeHGlobal(environment);
                if (attributesInitialized) DeleteProcThreadAttributeList(attributeList);
                if (attributeList != IntPtr.Zero) Marshal.FreeHGlobal(attributeList);
                if (inheritedHandles != IntPtr.Zero) Marshal.FreeHGlobal(inheritedHandles);
            }
        }

        public bool HasExited { get { return WaitForExit(0); } }
        public int ExitCode
        {
            get
            {
                lock (sync)
                {
                    ThrowIfDisposed(); uint code;
                    if (WaitForSingleObject(process, 0) != 0) throw new InvalidOperationException("后台任务尚未完成。");
                    if (!GetExitCodeProcess(process, out code)) throw NativeError("无法读取后台任务结果。");
                    return unchecked((int)code);
                }
            }
        }
        public bool WaitForExit(int milliseconds)
        {
            if (milliseconds < 0) throw new ArgumentOutOfRangeException("milliseconds");
            lock (sync)
            {
                ThrowIfDisposed(); uint status = WaitForSingleObject(process, (uint)milliseconds);
                if (status == UInt32.MaxValue) throw NativeError("无法等待后台任务。");
                return status == 0;
            }
        }
        public void Stop()
        {
            lock (sync)
            {
                if (disposed) return;
                RequestStopLocked();
                WaitForTreeExitLocked(2000);
            }
        }
        internal void RequestStop()
        { lock (sync) { if (!disposed) RequestStopLocked(); } }
        private void RequestStopLocked()
        {
            // Closing our sole, noninherited job handle is the fail-safe if termination fails.
            if (job != IntPtr.Zero && !TerminateJobObject(job, 1)) Close(ref job);
        }
        internal int ActiveProcessCount
        {
            get { lock (sync) { ThrowIfDisposed(); return QueryActiveProcesses(); } }
        }
        private int QueryActiveProcesses()
        {
            if (job == IntPtr.Zero) return 0;
            var accounting = new BasicAccountingInformation();
            if (!QueryInformationJobObject(job, 1, ref accounting, (uint)Marshal.SizeOf(typeof(BasicAccountingInformation)), IntPtr.Zero))
                throw NativeError("无法确认后台任务树退出状态。");
            return checked((int)accounting.ActiveProcesses);
        }
        private void WaitForTreeExitLocked(int milliseconds)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            while (job != IntPtr.Zero)
            {
                try { if (QueryActiveProcesses() == 0) break; }
                catch (Win32Exception) { break; }
                int remaining = Math.Max(0, milliseconds - (int)Math.Min(milliseconds, elapsed.ElapsedMilliseconds));
                if (remaining == 0) break;
                Thread.Sleep(Math.Min(10, remaining));
            }
            int rest = Math.Max(0, milliseconds - (int)Math.Min(milliseconds, elapsed.ElapsedMilliseconds));
            if (process != IntPtr.Zero) WaitForSingleObject(process, (uint)rest);
        }
        public void DrainOutput()
        {
            Task[] readers = { outputTask ?? Task.FromResult(0), errorTask ?? Task.FromResult(0) };
            if (!Task.WaitAll(readers, 5000)) { Stop(); Task.WaitAll(readers, 5000); }
        }
        public void Dispose() { DisposeWithin(2000); }
        internal void DisposeWithin(int milliseconds)
        {
            Stopwatch elapsed = Stopwatch.StartNew();
            lock (sync)
            {
                if (disposed) return;
                RequestStopLocked();
                WaitForTreeExitLocked(milliseconds);
                Close(ref job);
                Close(ref process); disposed = true;
            }
            lifetime.Forget(this);
            int remaining = Math.Max(0, milliseconds - (int)Math.Min(milliseconds, elapsed.ElapsedMilliseconds));
            try { Task.WaitAll(new[] { outputTask ?? Task.FromResult(0), errorTask ?? Task.FromResult(0) }, remaining); }
            catch (AggregateException) { /* Closing pipes can end a reader with an I/O exception. */ }
        }
        private void ThrowIfDisposed()
        {
            if (!disposed) return;
            if (lifetime.IsShuttingDown) throw new OperationCanceledException("软件正在退出，后台任务已取消。");
            throw new ObjectDisposedException("ProcessJob");
        }
        private static Task ReadLines(IntPtr handle, Encoding encoding, Action<string> callback)
        {
            return Task.Run(delegate
            {
                using (var stream = new FileStream(new SafeFileHandle(handle, true), FileAccess.Read))
                using (var reader = new StreamReader(stream, encoding, true, 4096))
                {
                    string line;
                    while ((line = reader.ReadLine()) != null)
                        if (callback != null) { try { callback(line); } catch (Exception) { } }
                }
            });
        }
        private static string EnvironmentBlock(ProcessStartInfo info)
        {
            var values = new SortedDictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string key in info.EnvironmentVariables.Keys) values[key] = info.EnvironmentVariables[key];
            var text = new StringBuilder();
            foreach (var pair in values) text.Append(pair.Key).Append('=').Append(pair.Value).Append('\0');
            return text.Append('\0').ToString();
        }
        private static Win32Exception NativeError(string text) { return new Win32Exception(Marshal.GetLastWin32Error(), text); }
        private static void Close(ref IntPtr handle)
        {
            if (handle != IntPtr.Zero && handle != new IntPtr(-1)) CloseHandle(handle);
            handle = IntPtr.Zero;
        }
        [StructLayout(LayoutKind.Sequential)] private struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool InheritHandle; }
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] private struct StartupInformation
        {
            public int Size; public string Reserved, Desktop, Title;
            public uint X, Y, XSize, YSize, XCountChars, YCountChars, FillAttribute, Flags;
            public short ShowWindow, ReservedBytes; public IntPtr ReservedPointer, StandardInput, StandardOutput, StandardError;
        }
        [StructLayout(LayoutKind.Sequential)] private struct ProcessInformation { public IntPtr Process, Thread; public uint ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)] private struct StartupInformationEx { public StartupInformation Startup; public IntPtr AttributeList; }
        [StructLayout(LayoutKind.Sequential)] private struct BasicAccountingInformation
        {
            public long TotalUserTime, TotalKernelTime, PeriodUserTime, PeriodKernelTime;
            public uint PageFaults, TotalProcesses, ActiveProcesses, TerminatedProcesses;
        }
        [StructLayout(LayoutKind.Sequential)] private struct BasicLimitInformation
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit; public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize; public uint ActiveProcessLimit;
            public UIntPtr Affinity; public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)] private struct IoCounters { public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount; }
        [StructLayout(LayoutKind.Sequential)] private struct ExtendedLimitInformation
        {
            public BasicLimitInformation BasicLimitInformation; public IoCounters IoInfo;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateJobObjectW")] private static extern IntPtr CreateJobObject(IntPtr attributes, string name);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref ExtendedLimitInformation info, uint length);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateJobObject(IntPtr job, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool QueryInformationJobObject(IntPtr job, int infoClass, ref BasicAccountingInformation info, uint length, IntPtr returnedLength);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref UIntPtr size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, UIntPtr attribute, IntPtr value, UIntPtr size, IntPtr previous, IntPtr returnedSize);
        [DllImport("kernel32.dll")] private static extern void DeleteProcThreadAttributeList(IntPtr list);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreatePipe(out IntPtr read, out IntPtr write, ref SecurityAttributes attributes, uint size);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool SetHandleInformation(IntPtr handle, uint mask, uint flags);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")] private static extern IntPtr CreateFile(string name, uint access, uint share, ref SecurityAttributes attributes, uint creation, uint flags, IntPtr template);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateProcessW")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateProcess(string app, StringBuilder command, IntPtr processAttributes, IntPtr threadAttributes, bool inherit, uint flags, IntPtr environment, string directory, ref StartupInformationEx startup, out ProcessInformation information);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint ResumeThread(IntPtr thread);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool TerminateProcess(IntPtr process, uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] private static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetExitCodeProcess(IntPtr process, out uint exitCode);
        [DllImport("kernel32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CloseHandle(IntPtr handle);
    }
}

using System;
using System.Collections.Generic;
using System.Diagnostics;

namespace HarmonicaStudio
{
    // Tracks only jobs created through this scope. No PID/name enumeration is used.
    internal sealed class OwnedProcessLifetime : IDisposable
    {
        private readonly object gate = new object();
        private readonly HashSet<ProcessJob> jobs = new HashSet<ProcessJob>();
        private bool shuttingDown;

        internal bool IsShuttingDown { get { lock (gate) return shuttingDown; } }
        internal int Count { get { lock (gate) return jobs.Count; } }
        internal ProcessJob Start(ProcessStartInfo info, Action<string> output, Action<string> error)
        {
            lock (gate)
            {
                if (shuttingDown) throw new OperationCanceledException("软件正在退出，后台任务已取消。");
                // Shutdown cannot miss a child between its creation and registration.
                ProcessJob process = ProcessJob.StartOwned(info, output, error, this);
                jobs.Add(process);
                return process;
            }
        }
        internal void Forget(ProcessJob process) { lock (gate) jobs.Remove(process); }
        internal void Shutdown()
        {
            ProcessJob[] active;
            lock (gate)
            {
                if (shuttingDown) return;
                shuttingDown = true;
                active = new ProcessJob[jobs.Count]; jobs.CopyTo(active);
            }
            // Stop all trees first, then share one two-second drain budget.
            // A slow child never grants every other tree a fresh wait budget.
            Stopwatch elapsed = Stopwatch.StartNew();
            foreach (ProcessJob process in active) process.RequestStop();
            foreach (ProcessJob process in active)
                process.DisposeWithin(Math.Max(0, 2000 - (int)Math.Min(2000, elapsed.ElapsedMilliseconds)));
            lock (gate) jobs.Clear();
        }
        public void Dispose() { Shutdown(); }
    }
}

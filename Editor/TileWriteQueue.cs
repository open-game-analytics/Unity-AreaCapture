using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace AreaCapture.Editor
{
    /// <summary>
    /// Runs the CPU/disk half of a tile (empty check, PNG encode, file write) on the thread pool while the main
    /// thread renders the next tile. At most <c>maxInFlight</c> tiles are queued, so the pixel buffers held in
    /// memory stay bounded: <see cref="Acquire"/> blocks the producer until a slot is free.
    /// </summary>
    internal class TileWriteQueue
    {
        private readonly SemaphoreSlim slots;
        private readonly List<Task> tasks = new List<Task>();
        private readonly object gate = new object();
        private Exception firstError;
        private long workerTicks;

        public TileWriteQueue(int maxInFlight)
        {
            slots = new SemaphoreSlim(maxInFlight, maxInFlight);
        }

        /// <summary>The first exception thrown by a work item, or null.</summary>
        public Exception Error { get { lock (gate) return firstError; } }

        /// <summary>Summed busy time of all workers (CPU-time, not wall-clock).</summary>
        public TimeSpan WorkerTime => TimeSpan.FromSeconds((double)Interlocked.Read(ref workerTicks) / Stopwatch.Frequency);

        /// <summary>Blocks until a slot is free. Every call must be followed by exactly one <see cref="Run"/>.</summary>
        public void Acquire() => slots.Wait();

        /// <summary>Starts <paramref name="work"/> on a worker and frees the slot taken by <see cref="Acquire"/> when done.</summary>
        public void Run(Action work)
        {
            Task task = Task.Run(() =>
            {
                long start = Stopwatch.GetTimestamp();
                try
                {
                    work();
                }
                catch (Exception e)
                {
                    lock (gate) { if (firstError == null) firstError = e; }
                }
                finally
                {
                    Interlocked.Add(ref workerTicks, Stopwatch.GetTimestamp() - start);
                    slots.Release();
                }
            });

            lock (gate)
            {
                tasks.RemoveAll(t => t.IsCompleted);
                tasks.Add(task);
            }
        }

        /// <summary>Waits until every started work item has finished. Work items never throw out of the task.</summary>
        public void Drain()
        {
            Task[] pending;
            lock (gate) pending = tasks.ToArray();
            Task.WaitAll(pending);
        }
    }
}

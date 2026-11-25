using System;
using System.Collections.Concurrent;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using HarmonyLib;
using TerrariaTurbo.Diagnostics;

namespace TerrariaTurbo.Patches
{
    /// <summary>
    /// Eliminates per-call CountdownEvent + RangeTask allocations and ThreadPriority.Highest
    /// busy-spin contention in ReLogic.Threading.FastParallel.For.
    /// </summary>
    public static class FastParallelPatch
    {
        private static Type _fastParallelType;
        private static Func<bool> _getForceOnCallingThread;
        private static Action<Delegate, int, int, object> _fastInvoker;

        private sealed class WorkSlot
        {
            public BatchState Batch;
            public int Start;
            public int End;
        }

        private sealed class BatchState
        {
            private static readonly int MaxSlots = Math.Max(1, Environment.ProcessorCount);
            public readonly WorkSlot[] Slots;
            public readonly ManualResetEventSlim DoneEvent = new ManualResetEventSlim(false);
            public Delegate Callback;
            public object Context;
            public int Remaining;
            // 0 = running, 1 = waiter sleeping on DoneEvent, 2 = worker signaling, 3 = signal complete
            public int SyncState;
            public bool NeedsReset;

            public BatchState()
            {
                Slots = new WorkSlot[MaxSlots];
                for (int i = 0; i < MaxSlots; i++)
                {
                    Slots[i] = new WorkSlot { Batch = this };
                }
            }
        }

        private static readonly ConcurrentStack<BatchState> _batchPool = new ConcurrentStack<BatchState>();
        private static readonly WaitCallback _workerCallback = ExecuteWorkSlot;

        public static void Apply(Harmony harmony)
        {
            _fastParallelType = AccessTools.TypeByName("ReLogic.Threading.FastParallel");
            if (_fastParallelType == null)
                throw new InvalidOperationException("Could not find ReLogic.Threading.FastParallel.");

            Type actionType = AccessTools.TypeByName("ReLogic.Threading.ParallelForAction");
            if (actionType == null)
                throw new InvalidOperationException("Could not find ReLogic.Threading.ParallelForAction.");

            _fastInvoker = BuildFastDelegateInvoker(actionType);

            PropertyInfo forceProp = AccessTools.Property(_fastParallelType, "ForceTasksOnCallingThread");
            if (forceProp != null && forceProp.GetGetMethod() != null)
            {
                _getForceOnCallingThread = (Func<bool>)Delegate.CreateDelegate(typeof(Func<bool>), forceProp.GetGetMethod());
            }
            else
            {
                _getForceOnCallingThread = () => false;
            }

            // Pre-warm two BatchState instances so first lighting pass has zero allocations
            _batchPool.Push(new BatchState());
            _batchPool.Push(new BatchState());

            MethodInfo target = AccessTools.Method(_fastParallelType, "For", new[] { typeof(int), typeof(int), actionType, typeof(object) });
            MethodInfo prefix = AccessTools.Method(typeof(FastParallelPatch), nameof(ForPrefix));
            harmony.Patch(target, prefix: new HarmonyMethod(prefix));
        }

        private static Action<Delegate, int, int, object> BuildFastDelegateInvoker(Type delegateType)
        {
            MethodInfo invokeMethod = delegateType.GetMethod("Invoke");
            var delParam = Expression.Parameter(typeof(Delegate), "del");
            var startParam = Expression.Parameter(typeof(int), "start");
            var endParam = Expression.Parameter(typeof(int), "end");
            var ctxParam = Expression.Parameter(typeof(object), "ctx");

            var castDel = Expression.Convert(delParam, delegateType);
            var call = Expression.Call(castDel, invokeMethod, startParam, endParam, ctxParam);
            return Expression.Lambda<Action<Delegate, int, int, object>>(call, delParam, startParam, endParam, ctxParam).Compile();
        }

        private static bool ForPrefix(int fromInclusive, int toExclusive, Delegate callback, object context)
        {
            int count = toExclusive - fromInclusive;
            if (count <= 0)
                return false;

            Interlocked.Increment(ref PerfTelemetry.FastParallelCalls);

            int workers = _getForceOnCallingThread()
                ? 1
                : Math.Min(Math.Max(1, Environment.ProcessorCount - 1), count);

            if (workers <= 1)
            {
                Interlocked.Increment(ref PerfTelemetry.FastParallelChunks);
                _fastInvoker(callback, fromInclusive, toExclusive, context);
                return false;
            }

            if (!_batchPool.TryPop(out BatchState batch))
            {
                batch = new BatchState();
            }

            if (batch.NeedsReset)
            {
                batch.DoneEvent.Reset();
                batch.NeedsReset = false;
            }

            batch.Callback = callback;
            batch.Context = context;
            Volatile.Write(ref batch.SyncState, 0);
            Volatile.Write(ref batch.Remaining, workers);

            int chunk = count / workers;
            int rem = count % workers;
            int cursor = toExclusive;

            // Assign ranges from back to front just like vanilla FastParallel, running slot 0 on caller
            for (int i = workers - 1; i >= 0; i--)
            {
                int len = chunk + (i < rem ? 1 : 0);
                cursor -= len;
                WorkSlot slot = batch.Slots[i];
                slot.Start = cursor;
                slot.End = cursor + len;

                if (i == 0)
                {
                    ExecuteWorkSlot(slot);
                }
                else
                {
                    ThreadPool.UnsafeQueueUserWorkItem(_workerCallback, slot);
                }
            }

            Interlocked.Add(ref PerfTelemetry.FastParallelChunks, workers);

            var spin = new SpinWait();
            while (Volatile.Read(ref batch.Remaining) > 0)
            {
                if (spin.Count >= 40)
                    break;
                spin.SpinOnce();
            }

            if (Volatile.Read(ref batch.Remaining) > 0 &&
                Interlocked.CompareExchange(ref batch.SyncState, 1, 0) == 0)
            {
                batch.NeedsReset = true;
                batch.DoneEvent.Wait();
                while (Volatile.Read(ref batch.SyncState) != 3)
                {
                    Thread.SpinWait(1);
                }
            }

            batch.Callback = null;
            batch.Context = null;
            _batchPool.Push(batch);
            return false;
        }

        private static void ExecuteWorkSlot(object state)
        {
            var slot = (WorkSlot)state;
            BatchState batch = slot.Batch;
            try
            {
                if (slot.Start != slot.End)
                {
                    _fastInvoker(batch.Callback, slot.Start, slot.End, batch.Context);
                }
            }
            finally
            {
                if (Interlocked.Decrement(ref batch.Remaining) == 0)
                {
                    if (Interlocked.Exchange(ref batch.SyncState, 2) == 1)
                    {
                        batch.DoneEvent.Set();
                        Volatile.Write(ref batch.SyncState, 3);
                    }
                }
            }
        }
    }
}

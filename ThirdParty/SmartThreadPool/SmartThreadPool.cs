#region Release History

// Smart Thread Pool
// 7 Aug 2004 - Initial release
//
// 14 Sep 2004 - Bug fixes 
//
// 15 Oct 2004 - Added new features
//        - Work items return result.
//        - Support waiting synchronization for multiple work items.
//        - Work items can be cancelled.
//        - Passage of the caller thread’s context to the thread in the pool.
//        - Minimal usage of WIN32 handles.
//        - Minor bug fixes.
//
// 26 Dec 2004 - Changes:
//        - Removed static constructors.
//      - Added finalizers.
//        - Changed Exceptions so they are serializable.
//        - Fixed the bug in one of the SmartThreadPool constructors.
//        - Changed the SmartThreadPool.WaitAll() so it will support any number of waiters. 
//        The SmartThreadPool.WaitAny() is still limited by the .NET Framework.
//        - Added PostExecute with options on which cases to call it.
//      - Added option to dispose of the state objects.
//      - Added a WaitForIdle() method that waits until the work items queue is empty.
//      - Added an STPStartInfo class for the initialization of the thread pool.
//      - Changed exception handling so if a work item throws an exception it 
//        is rethrown at GetResult(), rather then firing an UnhandledException event.
//        Note that PostExecute exception are always ignored.
//
// 25 Mar 2005 - Changes:
//        - Fixed lost of work items bug
//
// 3 Jul 2005: Changes.
//      - Fixed bug where Enqueue() throws an exception because PopWaiter() returned null, hardly reconstructed. 
//
// 16 Aug 2005: Changes.
//        - Fixed bug where the InUseThreads becomes negative when canceling work items. 
//
// 31 Jan 2006 - Changes:
//        - Added work items priority
//        - Removed support of chained delegates in callbacks and post executes (nobody really use this)
//        - Added work items groups
//        - Added work items groups idle event
//        - Changed SmartThreadPool.WaitAll() behavior so when it gets empty array
//          it returns true rather then throwing an exception.
//        - Added option to start the STP and the WIG as suspended
//        - Exception behavior changed, the real exception is returned by an 
//          inner exception
//        - Added performance counters
//        - Added priority to the threads in the pool
//
// 13 Feb 2006 - Changes:
//        - Added a call to the dispose of the Performance Counter so
//          their won't be a Performance Counter leak.
//        - Added exception catch in case the Performance Counters cannot 
//          be created.
//
// 17 May 2008 - Changes:
//      - Changed the dispose behavior and removed the Finalizers.
//      - Enabled the change of the MaxThreads and MinThreads at run time.
//      - Enabled the change of the Concurrency of a IWorkItemsGroup at run 
//        time If the IWorkItemsGroup is a SmartThreadPool then the Concurrency 
//        refers to the MaxThreads. 
//      - Improved the cancel behavior.
//      - Added events for thread creation and termination. 
//      - Fixed the HttpContext context capture.
//      - Changed internal collections so they use generic collections
//      - Added IsIdle flag to the SmartThreadPool and IWorkItemsGroup
//      - Added support for WinCE
//      - Added support for Action<T> and Func<T>
//
// 07 April 2009 - Changes:
//      - Added support for Silverlight and Mono
//      - Added Join, Choice, and Pipe to SmartThreadPool.
//      - Added local performance counters (for Mono, Silverlight, and WindowsCE)
//      - Changed duration measures from DateTime.Now to Stopwatch.
//      - Queues changed from System.Collections.Queue to System.Collections.Generic.LinkedList<T>.
//
// 21 December 2009 - Changes:
//      - Added work item timeout (passive)
//
// 20 August 2012 - Changes:
//      - Added set name to threads
//      - Fixed the WorkItemsQueue.Dequeue. 
//        Replaced while (!Monitor.TryEnter(this)); with lock(this) { ... }
//      - Fixed SmartThreadPool.Pipe
//      - Added IsBackground option to threads
//      - Added ApartmentState to threads
//      - Fixed thread creation when queuing many work items at the same time.
//
// 24 August 2012 - Changes:
//      - Enabled cancel abort after cancel. See: http://smartthreadpool.codeplex.com/discussions/345937 by alecswan
//      - Added option to set MaxStackSize of threads 

#endregion

using System;
using System.Security;
using System.Threading;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.CompilerServices;

using Amib.Threading.Internal;

namespace Amib.Threading
{
    #region SmartThreadPool class
    /// <summary>
    /// Smart thread pool class.
    /// </summary>
    public partial class SmartThreadPool : WorkItemsGroupBase, IDisposable
    {
        #region Public Default Constants

        /// <summary>
        /// Default minimum number of threads the thread pool contains. (0)
        /// </summary>
        public const int DefaultMinWorkerThreads = 0;

        /// <summary>
        /// Default maximum number of threads the thread pool contains. (25)
        /// </summary>
        public const int DefaultMaxWorkerThreads = 25;

        /// <summary>
        /// Default idle timeout in milliseconds. (One minute)
        /// </summary>
        public const int DefaultIdleTimeout = 60 * 1000; // One minute

        /// <summary>
        /// Indicate to copy the security context of the caller and then use it in the call. (false)
        /// </summary>
        public const bool DefaultUseCallerCallContext = false;

        /// <summary>
        /// Indicate to dispose of the state objects if they support the IDispose interface. (false)
        /// </summary>
        public const bool DefaultDisposeOfStateObjects = false;

        /// <summary>
        /// The default option to run the post execute (CallToPostExecute.Always)
        /// </summary>
        public const CallToPostExecute DefaultCallToPostExecute = CallToPostExecute.Always;

        /// <summary>
        /// The default post execute method to run. (None)
        /// When null it means not to call it.
        /// </summary>
        public static readonly PostExecuteWorkItemCallback DefaultPostExecuteWorkItemCallback;

        /// <summary>
        /// The default is to work on work items as soon as they arrive
        /// and not to wait for the start. (false)
        /// </summary>
        public const bool DefaultStartSuspended = false;

        /// <summary>
        /// The default name to use for the performance counters instance. (null)
        /// </summary>
        public static readonly string DefaultPerformanceCounterInstanceName;

        /// <summary>
        /// The default thread priority (ThreadPriority.Normal)
        /// </summary>
        public const ThreadPriority DefaultThreadPriority = ThreadPriority.Normal;

        /// <summary>
        /// The default thread pool name. (SmartThreadPool)
        /// </summary>
        public const string DefaultThreadPoolName = "SmartThreadPool";

        /// <summary>
        /// The default Max Stack Size. (SmartThreadPool)
        /// </summary>
        public static readonly int? DefaultMaxStackSize = null;

        /// <summary>
        /// The default fill state with params. (false)
        /// It is relevant only to QueueWorkItem of Action&lt;...&gt;/Func&lt;...&gt;
        /// </summary>
        public const bool DefaultFillStateWithArgs = false;

        /// <summary>
        /// The default thread backgroundness. (true)
        /// </summary>
        public const bool DefaultAreThreadsBackground = true;

        /// <summary>
        /// The default apartment state of a thread in the thread pool. 
        /// The default is ApartmentState.Unknown which means the STP will not 
        /// set the apartment of the thread. It will use the .NET default.
        /// </summary>
        public const ApartmentState DefaultApartmentState = ApartmentState.Unknown;

        #endregion

        #region Member Variables

        /// <summary>
        /// Dictionary of all the threads in the thread pool.
        /// </summary>
        private readonly ConcurrentDictionary<int, ThreadEntry> m_workerThreads = new();
        private readonly object m_workerThreadsLock = new();

        /// <summary>
        /// Queue of work items.
        /// </summary>
        private readonly WorkItemsQueue m_workItemsQueue = new();

        /// <summary>
        /// Count the work items handled.
        /// Used by the performance counter.
        /// </summary>
        private int m_workItemsProcessed;

        /// <summary>
        /// Number of threads that currently work (not idle).
        /// </summary>
        private int m_inUseWorkerThreads;

        /// <summary>
        /// Stores a copy of the original STPStartInfo.
        /// It is used to change the MinThread and MaxThreads
        /// </summary>
        private readonly STPStartInfo m_stpStartInfo;

        /// <summary>
        /// Total number of work items that are stored in the work items queue 
        /// plus the work items that the threads in the pool are working on.
        /// </summary>
        private int m_currentWorkItemsCount;

        /// <summary>
        /// Signaled when the thread pool is idle, i.e. no thread is busy
        /// and the work items queue is empty
        /// </summary>
        private ManualResetEvent m_isIdleWaitHandle = new(true);

        /// <summary>
        /// An event to signal all the threads to quit immediately.
        /// </summary>
        private ManualResetEvent m_shuttingDownEvent = new(false);

        /// <summary>
        /// A flag to indicate if the Smart Thread Pool is now suspended.
        /// </summary>
        private bool m_isSuspended;

        /// <summary>
        /// A flag to indicate the threads to quit.
        /// </summary>
        private bool m_shutdown;

        /// <summary>
        /// Counts the threads created in the pool.
        /// It is used to name the threads.
        /// </summary>
        private int m_threadCounter;

        /// <summary>
        /// Indicate that the SmartThreadPool has been disposed
        /// </summary>
        private bool m_isDisposed;

        private static long m_lastThreadCreateTS = long.MinValue;

        /// <summary>
        /// Holds all the WorkItemsGroup instaces that have at least one 
        /// work item int the SmartThreadPool
        /// This variable is used in case of Shutdown
        /// </summary>
        private readonly ConcurrentDictionary<int, WorkItemsGroup> m_workItemsGroups = new();

        /// <summary>
        /// A common object for all the work items int the STP
        /// so we can mark them to cancel in O(1)
        /// </summary>
        private CanceledWorkItemsGroup m_canceledSmartThreadPool = new();

        /// <summary>
        /// An event to call after a thread is created, but before 
        /// it's first use.
        /// </summary>
        private event ThreadInitializationHandler m_onThreadInitialization;

        /// <summary>
        /// An event to call when a thread is about to exit, after 
        /// it is no longer belong to the pool.
        /// </summary>
        private event ThreadTerminationHandler m_onThreadTermination;

        #endregion

        #region Per thread

        /// <summary>
        /// A reference to the current work item a thread from the thread pool 
        /// is executing.
        /// </summary>
        [ThreadStatic]
        internal static ThreadEntry CurrentThreadEntry;

        #endregion

        #region Construction and Finalization

        /// <summary>
        /// Constructor
        /// </summary>
        public SmartThreadPool()
        {
            m_stpStartInfo = new STPStartInfo();
            Initialize();
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="idleTimeout">Idle timeout in milliseconds</param>
        public SmartThreadPool(int idleTimeout)
        {
            m_stpStartInfo = new STPStartInfo
            {
                IdleTimeout = idleTimeout,
            };
            Initialize();
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="idleTimeout">Idle timeout in milliseconds</param>
        /// <param name="maxWorkerThreads">Upper limit of threads in the pool</param>
        public SmartThreadPool(int idleTimeout, int maxWorkerThreads)
        {
            m_stpStartInfo = new STPStartInfo
            {
                IdleTimeout = idleTimeout,
                MaxWorkerThreads = maxWorkerThreads,
            };
            Initialize();
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="idleTimeout">Idle timeout in milliseconds</param>
        /// <param name="maxWorkerThreads">Upper limit of threads in the pool</param>
        /// <param name="minWorkerThreads">Lower limit of threads in the pool</param>
        public SmartThreadPool(int idleTimeout, int maxWorkerThreads, int minWorkerThreads)
        {
            m_stpStartInfo = new STPStartInfo
            {
                IdleTimeout = idleTimeout,
                MaxWorkerThreads = maxWorkerThreads,
                MinWorkerThreads = minWorkerThreads,
            };
            Initialize();
        }

        /// <summary>
        /// Constructor
        /// </summary>
        /// <param name="stpStartInfo">A SmartThreadPool configuration that overrides the default behavior</param>
        public SmartThreadPool(STPStartInfo stpStartInfo)
        {
            m_stpStartInfo = new STPStartInfo(stpStartInfo);
            Initialize();
        }

        private void Initialize()
        {
            Name = m_stpStartInfo.ThreadPoolName;
            ValidateSTPStartInfo();

            // _stpStartInfoRW stores a read/write copy of the STPStartInfo.
            // Actually only MaxWorkerThreads and MinWorkerThreads are overwritten

            m_isSuspended = m_stpStartInfo.StartSuspended;

            // If the STP is not started suspended then start the threads.
            if (!m_isSuspended)
            {
                StartOptimalNumberOfThreads();
            }
        }

        private void StartOptimalNumberOfThreads()
        {
            int threadsCount;
            lock (m_workerThreadsLock)
            {
                threadsCount = m_workItemsQueue.Count;
                if (threadsCount == m_stpStartInfo.MinWorkerThreads)
                    return;
                if (threadsCount < m_stpStartInfo.MinWorkerThreads)
                    threadsCount = m_stpStartInfo.MinWorkerThreads;
                else if (threadsCount > m_stpStartInfo.MaxWorkerThreads)
                    threadsCount = m_stpStartInfo.MaxWorkerThreads;
                threadsCount -= m_workerThreads.Count;
            }
            StartThreads(threadsCount);
        }

        private void ValidateSTPStartInfo()
        {
            if (m_stpStartInfo.MinWorkerThreads < 0)
            {
                throw new ArgumentOutOfRangeException(
                    "MinWorkerThreads", "MinWorkerThreads cannot be negative");
            }

            if (m_stpStartInfo.MaxWorkerThreads <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    "MaxWorkerThreads", "MaxWorkerThreads must be greater than zero");
            }

            if (m_stpStartInfo.MinWorkerThreads > m_stpStartInfo.MaxWorkerThreads)
            {
                throw new ArgumentOutOfRangeException(
                    "MinWorkerThreads, maxWorkerThreads",
                    "MaxWorkerThreads must be greater or equal to MinWorkerThreads");
            }
        }

        #endregion

        #region Thread Processing

        /// <summary>
        /// Waits on the queue for a work item, shutdown, or timeout.
        /// </summary>
        /// <returns>
        /// Returns the WaitingCallback or null in case of timeout or shutdown.
        /// </returns>
        private WorkItem Dequeue()
        {
            return m_workItemsQueue.DequeueWorkItem(m_stpStartInfo.IdleTimeout, m_shuttingDownEvent);
        }

        /// <summary>
        /// Put a new work item in the queue
        /// </summary>
        /// <param name="workItem">A work item to queue</param>
        internal override void Enqueue(WorkItem workItem)
        {
            // Make sure the workItem is not null
            Debug.Assert(workItem is not null);

            IncrementWorkItemsCount();

            workItem.CanceledSmartThreadPool = m_canceledSmartThreadPool;
            workItem.WorkItemIsQueued();
            m_workItemsQueue.EnqueueWorkItem(workItem);

            // If all the threads are busy then try to create a new one
            if (m_currentWorkItemsCount > m_workerThreads.Count)
            {
                StartThreads(1);
            }
        }

        private void IncrementWorkItemsCount()
        {
            int count = Interlocked.Increment(ref m_currentWorkItemsCount);
            //Trace.WriteLine("WorkItemsCount = " + _currentWorkItemsCount.ToString());
            if (count == 1)
            {
                IsIdle = false;
                m_isIdleWaitHandle.Reset();
            }
        }

        private void DecrementWorkItemsCount()
        {
            int count = Interlocked.Decrement(ref m_currentWorkItemsCount);
            //Trace.WriteLine("WorkItemsCount = " + _currentWorkItemsCount.ToString());
            if (count == 0)
            {
                IsIdle = true;
                m_isIdleWaitHandle.Set();
            }

            Interlocked.Increment(ref m_workItemsProcessed);
        }

        private int baseWorkIDs = Environment.TickCount;
        internal void RegisterWorkItemsGroup(IWorkItemsGroup workItemsGroup)
        {
            int localID = Interlocked.Increment(ref baseWorkIDs);
            while (m_workItemsGroups.ContainsKey(localID))
                localID = Interlocked.Increment(ref baseWorkIDs);

            workItemsGroup.localID = localID;
            m_workItemsGroups[localID] = (WorkItemsGroup)workItemsGroup;
        }

        internal void UnregisterWorkItemsGroup(IWorkItemsGroup workItemsGroup)
        {
            m_workItemsGroups.TryRemove(workItemsGroup.localID, out WorkItemsGroup _);
        }

        /// <summary>
        /// Inform that the current thread is about to quit or quiting.
        /// The same thread may call this method more than once.
        /// </summary>
        private void InformCompleted()
        {
            if (m_workerThreads.TryRemove(Environment.CurrentManagedThreadId, out ThreadEntry te))
            {
                te.Clean();
            }
        }

        /// <summary>
        /// Starts new threads
        /// </summary>
        /// <param name="threadsCount">The number of threads to start</param>
        private void StartThreads(int threadsCount)
        {
            if (m_isSuspended)
                return;

            lock (m_workerThreadsLock)
            {
                // Don't start threads on shut down
                if (m_shutdown)
                    return;

                int tmpcount = m_workerThreads.Count;
                if(tmpcount > m_stpStartInfo.MinWorkerThreads)
                {
                    long last = Interlocked.Read(ref m_lastThreadCreateTS);
                    if (DateTime.UtcNow.Ticks - last < 50 * TimeSpan.TicksPerMillisecond)
                        return;
                }

                tmpcount = m_stpStartInfo.MaxWorkerThreads - tmpcount;
                if (threadsCount > tmpcount)
                    threadsCount = tmpcount;

                while(threadsCount > 0)
                {
                    // Create a new thread
                    Thread workerThread;
                    if(m_stpStartInfo.SuppressFlow)
                    {
                        using(ExecutionContext.SuppressFlow())
                        {
                            workerThread =
                                m_stpStartInfo.MaxStackSize.HasValue
                                ? new Thread(ProcessQueuedItems, m_stpStartInfo.MaxStackSize.Value)
                                : new Thread(ProcessQueuedItems);
                         }
                    }
                    else
                    {
                        workerThread =
                                m_stpStartInfo.MaxStackSize.HasValue
                                ? new Thread(ProcessQueuedItems, m_stpStartInfo.MaxStackSize.Value)
                                : new Thread(ProcessQueuedItems);
                    }

                    // Configure the new thread and start it
                    workerThread.IsBackground = m_stpStartInfo.AreThreadsBackground;

                    if (m_stpStartInfo.ApartmentState != ApartmentState.Unknown)
                        workerThread.SetApartmentState(m_stpStartInfo.ApartmentState);

                    workerThread.Priority = m_stpStartInfo.ThreadPriority;
                    workerThread.Name = $"STP:{Name}:{m_threadCounter}";

                    Interlocked.Exchange(ref m_lastThreadCreateTS, DateTime.UtcNow.Ticks);
                    ++m_threadCounter;
                    --threadsCount;

                    // Add it to the dictionary and update its creation time.
                    m_workerThreads[workerThread.ManagedThreadId] = new ThreadEntry(this, workerThread);

                    workerThread.Start();
                }
            }
        }

        /// <summary>
        /// A worker thread method that processes work items from the work items queue.
        /// </summary>
        private void ProcessQueuedItems()
        {
            // Keep the entry of the dictionary as thread's variable to avoid the synchronization locks
            // of the dictionary.
            CurrentThreadEntry = m_workerThreads[Environment.CurrentManagedThreadId];

            bool informedCompleted = false;
            FireOnThreadInitialization();

            try
            {
                bool bInUseWorkerThreadsWasIncremented = false;
                int maxworkers = m_stpStartInfo.MaxWorkerThreads;
                int minworkers = m_stpStartInfo.MinWorkerThreads;

                // Process until shutdown.
                while (!m_shutdown)
                {
                    // The following block handles the when the MaxWorkerThreads has been
                    // incremented by the user at run-time.
                    // Double lock for quit.
                    if (m_workerThreads.Count > maxworkers)
                    {
                        lock (m_workerThreadsLock)
                        {
                            if (m_workerThreads.Count > maxworkers)
                            {
                                // Inform that the thread is quiting and then quit.
                                // This method must be called within this lock or else
                                // more threads will quit and the thread pool will go
                                // below the lower limit.
                                InformCompleted();
                                informedCompleted = true;
                                break;
                            }
                        }
                    }

                    CurrentThreadEntry.IAmAlive();

                    // Wait for a work item, shutdown, or timeout
                    WorkItem workItem = Dequeue();

                    // On timeout or shut down.
                    if (workItem is null)
                    {
                        // Double lock for quit.
                        if (m_workerThreads.Count > minworkers)
                        {
                            lock (m_workerThreadsLock)
                            {
                                if (m_workerThreads.Count > minworkers)
                                {
                                    // Inform that the thread is quiting and then quit.
                                    // This method must be called within this lock or else
                                    // more threads will quit and the thread pool will go
                                    // below the lower limit.
                                    InformCompleted();
                                    informedCompleted = true;
                                    break;
                                }
                            }
                        }
                        continue;
                    }

                    CurrentThreadEntry.IAmAlive();

                    try
                    {
                        // Initialize the value to false
                        bInUseWorkerThreadsWasIncremented = false;

                        // Set the Current Work Item of the thread.
                        // Store the Current Work Item  before the workItem.StartingWorkItem() is called, 
                        // so WorkItem.Cancel can work when the work item is between InQueue and InProgress 
                        // states.
                        // If the work item has been cancelled BEFORE the workItem.StartingWorkItem() 
                        // (work item is in InQueue state) then workItem.StartingWorkItem() will return false.
                        // If the work item has been cancelled AFTER the workItem.StartingWorkItem() then
                        // (work item is in InProgress state) then the thread will be aborted
                        CurrentThreadEntry.CurrentWorkItem = workItem;

                        // Change the state of the work item to 'in progress' if possible.
                        // We do it here so if the work item has been canceled we won't 
                        // increment the _inUseWorkerThreads.
                        // The cancel mechanism doesn't delete items from the queue,  
                        // it marks the work item as canceled, and when the work item
                        // is dequeued, we just skip it.
                        // If the post execute of work item is set to always or to
                        // call when the work item is canceled then the StartingWorkItem()
                        // will return true, so the post execute can run.
                        if (!workItem.StartingWorkItem())
                        {
                            CurrentThreadEntry.CurrentWorkItem = null;
                            continue;
                        }

                        // Execute the callback.  Make sure to accurately
                        // record how many callbacks are currently executing.
                        int inUseWorkerThreads = Interlocked.Increment(ref m_inUseWorkerThreads);

                        // Mark that the _inUseWorkerThreads incremented, so in the finally{}
                        // statement we will decrement it correctly.
                        bInUseWorkerThreadsWasIncremented = true;

                        workItem.FireWorkItemStarted();

                        ExecuteWorkItem(workItem);
                    }
                    catch (Exception ex)
                    {
                        ex.GetHashCode();
                        // Do nothing
                    }
                    finally
                    {
                        workItem.DisposeOfState();

                        // Set the CurrentWorkItem to null, since we 
                        // no longer run user's code.
                        CurrentThreadEntry.CurrentWorkItem = null;

                        // Decrement the _inUseWorkerThreads only if we had 
                        // incremented it. Note the cancelled work items don't
                        // increment _inUseWorkerThreads.
                        if (bInUseWorkerThreadsWasIncremented)
                        {
                            int inUseWorkerThreads = Interlocked.Decrement(ref m_inUseWorkerThreads);
                        }

                        // Notify that the work item has been completed.
                        // WorkItemsGroup may enqueue their next work item.
                        workItem.FireWorkItemCompleted();

                        // Decrement the number of work items here so the idle 
                        // ManualResetEvent won't fluctuate.
                        DecrementWorkItemsCount();
                    }
                }
            }
            /*
            catch (ThreadAbortException tae)
            {
                //tae.GetHashCode();
                // Handle the abort exception gracfully.
                //Thread.ResetAbort();
            }
            */
            catch (Exception e)
            {
                Debug.Assert(e is not null);
            }
            finally
            {
                if(!informedCompleted)
                    InformCompleted();
                FireOnThreadTermination();
                m_workItemsQueue.CloseThreadWaiter();
                CurrentThreadEntry = null;
            }
        }

        private static void ExecuteWorkItem(WorkItem workItem)
        {
            try
            {
                workItem.Execute();
            }
            finally
            {
            }
        }


        #endregion

        #region Public Methods

        private void ValidateWaitForIdle()
        {
            if (CurrentThreadEntry is not null && CurrentThreadEntry.AssociatedSmartThreadPool == this)
            {
                throw new NotSupportedException(
                    "WaitForIdle cannot be called from a thread on its SmartThreadPool, it causes a deadlock");
            }
        }

        internal static void ValidateWorkItemsGroupWaitForIdle(IWorkItemsGroup workItemsGroup)
        {
            if (CurrentThreadEntry is not null)
                ValidateWorkItemsGroupWaitForIdleImpl(workItemsGroup, CurrentThreadEntry.CurrentWorkItem);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ValidateWorkItemsGroupWaitForIdleImpl(IWorkItemsGroup workItemsGroup, WorkItem workItem)
        {
            if ((workItemsGroup is not null) &&
                (workItem is not null) &&
                workItem.WasQueuedBy(workItemsGroup))
            {
                throw new NotSupportedException("WaitForIdle cannot be called from a thread on its SmartThreadPool, it causes a deadlock");
            }
        }

        /// <summary>
        /// Force the SmartThreadPool to shutdown
        /// </summary>
        public void Shutdown()
        {
            Shutdown(true, 0);
        }

        /// <summary>
        /// Force the SmartThreadPool to shutdown with timeout
        /// </summary>
        public void Shutdown(bool forceAbort, TimeSpan timeout)
        {
            Shutdown(forceAbort, (int)timeout.TotalMilliseconds);
        }

        /// <summary>
        /// Empties the queue of work items and abort the threads in the pool.
        /// </summary>
        public void Shutdown(bool forceAbort, int millisecondsTimeout)
        {
            ValidateNotDisposed();

            ThreadEntry[] threadEntries;
            lock (m_workerThreadsLock)
            {
                // Shutdown the work items queue
                m_workItemsQueue.Dispose();

                // Signal the threads to exit
                m_shutdown = true;
                m_shuttingDownEvent.Set();

                // Make a copy of the threads' references in the pool
                threadEntries = new ThreadEntry[m_workerThreads.Count];
                m_workerThreads.Values.CopyTo(threadEntries, 0);
                m_workerThreads.Clear();
            }

            int millisecondsLeft = millisecondsTimeout;
            Stopwatch stopwatch = Stopwatch.StartNew();
            //DateTime start = DateTime.UtcNow;
            bool waitInfinitely = (Timeout.Infinite == millisecondsTimeout);
            bool timeout = false;

            // Each iteration we update the time left for the timeout.
            foreach (ThreadEntry te in threadEntries)
            {
                if (te is null)
                    continue;

                Thread thread = te.WorkThread;

                // Join don't work with negative numbers
                if (!waitInfinitely && (millisecondsLeft < 0))
                {
                    timeout = true;
                    break;
                }

                // Wait for the thread to terminate
                bool success = thread.Join(millisecondsLeft);
                if (!success)
                {
                    timeout = true;
                    break;
                }

                if (!waitInfinitely)
                {
                    // Update the time left to wait
                    //TimeSpan ts = DateTime.UtcNow - start;
                    millisecondsLeft = millisecondsTimeout - (int)stopwatch.ElapsedMilliseconds;
                }
                te.WorkThread = null;
            }

            if (timeout && forceAbort)
            {
                // Abort the threads in the pool
                foreach (ThreadEntry te in threadEntries)
                {
                    if (te is null)
                        continue;

                    Thread thread = te.WorkThread;
                    if (thread is not null && thread.IsAlive )
                    {
                        try
                        {
                            //thread.Abort(); // Shutdown
                            te.WorkThread = null;
                        }
                        catch (SecurityException e)
                        {
                            e.GetHashCode();
                        }
                        catch (ThreadStateException ex)
                        {
                            ex.GetHashCode();
                            // In case the thread has been terminated 
                            // after the check if it is alive.
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Wait for all work items to complete
        /// </summary>
        /// <param name="waitableResults">Array of work item result objects</param>
        /// <returns>
        /// true when every work item in workItemResults has completed; otherwise false.
        /// </returns>
        public static bool WaitAll( IWaitableResult[] waitableResults)
        {
            return WaitAll(waitableResults, Timeout.Infinite, true);
        }

        /// <summary>
        /// Wait for all work items to complete
        /// </summary>
        /// <param name="waitableResults">Array of work item result objects</param>
        /// <param name="timeout">The number of milliseconds to wait, or a TimeSpan that represents -1 milliseconds to wait indefinitely. </param>
        /// <param name="exitContext">
        /// true to exit the synchronization domain for the context before the wait (if in a synchronized context), and reacquire it; otherwise, false. 
        /// </param>
        /// <returns>
        /// true when every work item in workItemResults has completed; otherwise false.
        /// </returns>
        public static bool WaitAll( IWaitableResult[] waitableResults, TimeSpan timeout, bool exitContext)
        {
            return WaitAll(waitableResults, (int)timeout.TotalMilliseconds, exitContext);
        }

        /// <summary>
        /// Wait for all work items to complete
        /// </summary>
        /// <param name="waitableResults">Array of work item result objects</param>
        /// <param name="timeout">The number of milliseconds to wait, or a TimeSpan that represents -1 milliseconds to wait indefinitely. </param>
        /// <param name="exitContext">
        /// true to exit the synchronization domain for the context before the wait (if in a synchronized context), and reacquire it; otherwise, false. 
        /// </param>
        /// <param name="cancelWaitHandle">A cancel wait handle to interrupt the wait if needed</param>
        /// <returns>
        /// true when every work item in workItemResults has completed; otherwise false.
        /// </returns>
        public static bool WaitAll( IWaitableResult[] waitableResults, TimeSpan timeout,
            bool exitContext, WaitHandle cancelWaitHandle)
        {
            return WaitAll(waitableResults, (int)timeout.TotalMilliseconds, exitContext, cancelWaitHandle);
        }

        /// <summary>
        /// Wait for all work items to complete
        /// </summary>
        /// <param name="waitableResults">Array of work item result objects</param>
        /// <param name="millisecondsTimeout">The number of milliseconds to wait, or Timeout.Infinite (-1) to wait indefinitely.</param>
        /// <param name="exitContext">
        /// true to exit the synchronization domain for the context before the wait (if in a synchronized context), and reacquire it; otherwise, false. 
        /// </param>
        /// <returns>
        /// true when every work item in workItemResults has completed; otherwise false.
        /// </returns>
        public static bool WaitAll( IWaitableResult[] waitableResults, int millisecondsTimeout, bool exitContext)
        {
            return WorkItem.WaitAll(waitableResults, millisecondsTimeout, exitContext, null);
        }

        /// <summary>
        /// Wait for all work items to complete
        /// </summary>
        /// <param name="waitableResults">Array of work item result objects</param>
        /// <param name="millisecondsTimeout">The number of milliseconds to wait, or Timeout.Infinite (-1) to wait indefinitely.</param>
        /// <param name="exitContext">
        /// true to exit the synchronization domain for the context before the wait (if in a synchronized context), and reacquire it; otherwise, false. 
        /// </param>
        /// <param name="cancelWaitHandle">A cancel wait handle to interrupt the wait if needed</param>
        /// <returns>
        /// true when every work item in workItemResults has completed; otherwise false.
        /// </returns>
        public static bool WaitAll( IWaitableResult[] waitableResults, int millisecondsTimeout,
            bool exitContext, WaitHandle cancelWaitHandle)
        {
            return WorkItem.WaitAll(waitableResults, millisecondsTimeout, exitContext, cancelWaitHandle);
        }


        /// <summary>
        /// Waits for any of the work items in the specified array to complete, cancel, or timeout
        /// </summary>
        /// <param name="waitableResults">Array of work item result objects</param>
        /// <returns>
        /// The array index of the work item result that satisfied the wait, or WaitTimeout if any of the work items has been canceled.
        /// </returns>
        public static int WaitAny( IWaitableResult[] waitableResults)
        {
            return WaitAny(waitableResults, Timeout.Infinite, true);
        }

        /// <summary>
        /// Waits for any of the work items in the specified array to complete, cancel, or timeout
        /// </summary>
        /// <param name="waitableResults">Array of work item result objects</param>
        /// <param name="timeout">The number of milliseconds to wait, or a TimeSpan that represents -1 milliseconds to wait indefinitely. </param>
        /// <param name="exitContext">
        /// true to exit the synchronization domain for the context before the wait (if in a synchronized context), and reacquire it; otherwise, false. 
        /// </param>
        /// <returns>
        /// The array index of the work item result that satisfied the wait, or WaitTimeout if no work item result satisfied the wait and a time interval equivalent to millisecondsTimeout has passed or the work item has been canceled.
        /// </returns>
        public static int WaitAny( IWaitableResult[] waitableResults, TimeSpan timeout, bool exitContext)
        {
            return WaitAny(waitableResults, (int)timeout.TotalMilliseconds, exitContext);
        }

        /// <summary>
        /// Waits for any of the work items in the specified array to complete, cancel, or timeout
        /// </summary>
        /// <param name="waitableResults">Array of work item result objects</param>
        /// <param name="timeout">The number of milliseconds to wait, or a TimeSpan that represents -1 milliseconds to wait indefinitely. </param>
        /// <param name="exitContext">
        /// true to exit the synchronization domain for the context before the wait (if in a synchronized context), and reacquire it; otherwise, false. 
        /// </param>
        /// <param name="cancelWaitHandle">A cancel wait handle to interrupt the wait if needed</param>
        /// <returns>
        /// The array index of the work item result that satisfied the wait, or WaitTimeout if no work item result satisfied the wait and a time interval equivalent to millisecondsTimeout has passed or the work item has been canceled.
        /// </returns>
        public static int WaitAny( IWaitableResult[] waitableResults, TimeSpan timeout,
            bool exitContext, WaitHandle cancelWaitHandle)
        {
            return WaitAny(waitableResults, (int)timeout.TotalMilliseconds, exitContext, cancelWaitHandle);
        }

        /// <summary>
        /// Waits for any of the work items in the specified array to complete, cancel, or timeout
        /// </summary>
        /// <param name="waitableResults">Array of work item result objects</param>
        /// <param name="millisecondsTimeout">The number of milliseconds to wait, or Timeout.Infinite (-1) to wait indefinitely.</param>
        /// <param name="exitContext">
        /// true to exit the synchronization domain for the context before the wait (if in a synchronized context), and reacquire it; otherwise, false. 
        /// </param>
        /// <returns>
        /// The array index of the work item result that satisfied the wait, or WaitTimeout if no work item result satisfied the wait and a time interval equivalent to millisecondsTimeout has passed or the work item has been canceled.
        /// </returns>
        public static int WaitAny( IWaitableResult[] waitableResults, int millisecondsTimeout, bool exitContext)
        {
            return WorkItem.WaitAny(waitableResults, millisecondsTimeout, exitContext, null);
        }

        /// <summary>
        /// Waits for any of the work items in the specified array to complete, cancel, or timeout
        /// </summary>
        /// <param name="waitableResults">Array of work item result objects</param>
        /// <param name="millisecondsTimeout">The number of milliseconds to wait, or Timeout.Infinite (-1) to wait indefinitely.</param>
        /// <param name="exitContext">
        /// true to exit the synchronization domain for the context before the wait (if in a synchronized context), and reacquire it; otherwise, false. 
        /// </param>
        /// <param name="cancelWaitHandle">A cancel wait handle to interrupt the wait if needed</param>
        /// <returns>
        /// The array index of the work item result that satisfied the wait, or WaitTimeout if no work item result satisfied the wait and a time interval equivalent to millisecondsTimeout has passed or the work item has been canceled.
        /// </returns>
        public static int WaitAny( IWaitableResult[] waitableResults, int millisecondsTimeout,
            bool exitContext, WaitHandle cancelWaitHandle)
        {
            return WorkItem.WaitAny(waitableResults, millisecondsTimeout, exitContext, cancelWaitHandle);
        }

        /// <summary>
        /// Creates a new WorkItemsGroup.
        /// </summary>
        /// <param name="concurrency">The number of work items that can be run concurrently</param>
        /// <returns>A reference to the WorkItemsGroup</returns>
        public IWorkItemsGroup CreateWorkItemsGroup(int concurrency)
        {
            IWorkItemsGroup workItemsGroup = new WorkItemsGroup(this, concurrency, m_stpStartInfo);
            return workItemsGroup;
        }

        /// <summary>
        /// Creates a new WorkItemsGroup.
        /// </summary>
        /// <param name="concurrency">The number of work items that can be run concurrently</param>
        /// <param name="wigStartInfo">A WorkItemsGroup configuration that overrides the default behavior</param>
        /// <returns>A reference to the WorkItemsGroup</returns>
        public IWorkItemsGroup CreateWorkItemsGroup(int concurrency, WIGStartInfo wigStartInfo)
        {
            IWorkItemsGroup workItemsGroup = new WorkItemsGroup(this, concurrency, wigStartInfo);
            return workItemsGroup;
        }

        #region Fire Thread's Events

        private void FireOnThreadInitialization()
        {
            if (null != m_onThreadInitialization)
            {
                foreach (ThreadInitializationHandler tih in m_onThreadInitialization.GetInvocationList())
                {
                    try
                    {
                        tih();
                    }
                    catch
                    {
                        Debug.Assert(false);
                        throw;
                    }
                }
            }
        }

        private void FireOnThreadTermination()
        {
            if (null != m_onThreadTermination)
            {
                foreach (ThreadTerminationHandler tth in m_onThreadTermination.GetInvocationList())
                {
                    try
                    {
                        tth();
                    }
                    catch
                    {
                        Debug.Assert(false);
                        throw;
                    }
                }
            }
        }

        #endregion

        /// <summary>
        /// This event is fired when a thread is created.
        /// Use it to initialize a thread before the work items use it.
        /// </summary>
        public event ThreadInitializationHandler OnThreadInitialization
        {
            add { m_onThreadInitialization += value; }
            remove { m_onThreadInitialization -= value; }
        }

        /// <summary>
        /// This event is fired when a thread is terminating.
        /// Use it for cleanup.
        /// </summary>
        public event ThreadTerminationHandler OnThreadTermination
        {
            add { m_onThreadTermination += value; }
            remove { m_onThreadTermination -= value; }
        }


        internal void CancelAbortWorkItemsGroup(WorkItemsGroup wig)
        {
            foreach (ThreadEntry threadEntry in m_workerThreads.Values)
            {
                WorkItem workItem = threadEntry.CurrentWorkItem;
                if (null != workItem && !workItem.IsCanceled && workItem.WasQueuedBy(wig))
                {
                    threadEntry.CurrentWorkItem.GetWorkItemResult().Cancel(true);
                }
            }
        }

        #endregion

        #region Properties

        /// <summary>
        /// Get/Set the lower limit of threads in the pool.
        /// </summary>
        public int MinThreads
        {
            get
            {
                ValidateNotDisposed();
                return m_stpStartInfo.MinWorkerThreads;
            }
            set
            {
                Debug.Assert(value >= 0);
                Debug.Assert(value <= m_stpStartInfo.MaxWorkerThreads);
                if (m_stpStartInfo.MaxWorkerThreads < value)
                {
                    m_stpStartInfo.MaxWorkerThreads = value;
                }
                m_stpStartInfo.MinWorkerThreads = value;
                StartOptimalNumberOfThreads();
            }
        }

        /// <summary>
        /// Get/Set the upper limit of threads in the pool.
        /// </summary>
        public int MaxThreads
        {
            get
            {
                ValidateNotDisposed();
                return m_stpStartInfo.MaxWorkerThreads;
            }

            set
            {
                Debug.Assert(value > 0);
                Debug.Assert(value >= m_stpStartInfo.MinWorkerThreads);
                if (m_stpStartInfo.MinWorkerThreads > value)
                {
                    m_stpStartInfo.MinWorkerThreads = value;
                }
                m_stpStartInfo.MaxWorkerThreads = value;
                StartOptimalNumberOfThreads();
            }
        }
        /// <summary>
        /// Get the number of threads in the thread pool.
        /// Should be between the lower and the upper limits.
        /// </summary>
        public int ActiveThreads
        {
            get
            {
                ValidateNotDisposed();
                return m_workerThreads.Count;
            }
        }

        /// <summary>
        /// Get the number of busy (not idle) threads in the thread pool.
        /// </summary>
        public int InUseThreads
        {
            get
            {
                ValidateNotDisposed();
                return m_inUseWorkerThreads;
            }
        }

        /// <summary>
        /// Returns true if the current running work item has been cancelled.
        /// Must be used within the work item's callback method.
        /// The work item should sample this value in order to know if it
        /// needs to quit before its completion.
        /// </summary>
        public static bool IsWorkItemCanceled
        {
            get
            {
                return CurrentThreadEntry.CurrentWorkItem.IsCanceled;
            }
        }

        /// <summary>
        /// Checks if the work item has been cancelled, and if yes then abort the thread.
        /// Can be used with Cancel and timeout
        /// </summary>
        public static void AbortOnWorkItemCancel()
        {
            if (IsWorkItemCanceled)
            {
                //Thread.CurrentThread.Abort();
            }
        }

        /// <summary>
        /// Thread Pool start information (readonly)
        /// </summary>
        public STPStartInfo STPStartInfo
        {
            get
            {
                return m_stpStartInfo.AsReadOnly();
            }
        }

        public bool IsShuttingdown
        {
            get { return m_shutdown; }
        }

        #endregion

        #region IDisposable Members

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected void Dispose(bool disposing)
        {
            if (!m_isDisposed)
            {
                if (!m_shutdown)
                {
                    Shutdown();
                }

                if (m_shuttingDownEvent is not null)
                {
                    m_shuttingDownEvent.Close();
                    m_shuttingDownEvent = null;
                }
                m_workerThreads.Clear();

                if (m_isIdleWaitHandle is not null)
                {
                    m_isIdleWaitHandle.Close();
                    m_isIdleWaitHandle = null;
                }

                m_isDisposed = true;
            }
        }

        private void ValidateNotDisposed()
        {
            if (m_isDisposed)
            {
                throw new ObjectDisposedException(GetType().ToString(), "The SmartThreadPool has been shutdown");
            }
        }
        #endregion

        #region WorkItemsGroupBase Overrides

        /// <summary>
        /// Get/Set the maximum number of work items that execute cocurrency on the thread pool
        /// </summary>
        public override int Concurrency
        {
            get { return MaxThreads; }
            set { MaxThreads = value; }
        }

        /// <summary>
        /// Get the number of work items in the queue.
        /// </summary>
        public override int WaitingCallbacks
        {
            get
            {
                ValidateNotDisposed();
                return m_workItemsQueue.Count;
            }
        }

        /// <summary>
        /// Get an array with all the state objects of the currently running items.
        /// The array represents a snap shot and impact performance.
        /// </summary>
        public override object[] GetStates()
        {
            object[] states = m_workItemsQueue.GetStates();
            return states;
        }

        /// <summary>
        /// WorkItemsGroup start information (readonly)
        /// </summary>
        public override WIGStartInfo WIGStartInfo
        {
            get { return m_stpStartInfo.AsReadOnly(); }
        }

        /// <summary>
        /// Start the thread pool if it was started suspended.
        /// If it is already running, this method is ignored.
        /// </summary>
        public override void Start()
        {
            if (!m_isSuspended)
            {
                return;
            }
            m_isSuspended = false;

            foreach (WorkItemsGroup workItemsGroup in m_workItemsGroups.Values)
            {
                workItemsGroup?.OnSTPIsStarting();
            }

            StartOptimalNumberOfThreads();
        }

        /// <summary>
        /// Cancel all work items using thread abortion
        /// </summary>
        /// <param name="abortExecution">True to stop work items by raising ThreadAbortException</param>
        public override void Cancel(bool abortExecution)
        {
            m_canceledSmartThreadPool.IsCanceled = true;
            m_canceledSmartThreadPool = new CanceledWorkItemsGroup();

            foreach (WorkItemsGroup workItemsGroup in m_workItemsGroups.Values)
            {
                workItemsGroup?.Cancel(abortExecution);
            }

            if (abortExecution)
            {
                foreach (ThreadEntry threadEntry in m_workerThreads.Values)
                {
                    if(threadEntry.AssociatedSmartThreadPool == this)
                    {
                        WorkItem workItem = threadEntry.CurrentWorkItem;
                        if (workItem is not null && !workItem.IsCanceled)
                        {
                            threadEntry.CurrentWorkItem.GetWorkItemResult().Cancel(true);
                        }
                    }
                }
            }
        }

        /// <summary>
        /// Wait for the thread pool to be idle
        /// </summary>
        public override bool WaitForIdle(int millisecondsTimeout)
        {
            ValidateWaitForIdle();
            return STPEventWaitHandle.WaitOne(m_isIdleWaitHandle, millisecondsTimeout, false);
        }

        /// <summary>
        /// This event is fired when all work items are completed.
        /// (When IsIdle changes to true)
        /// This event only work on WorkItemsGroup. On SmartThreadPool
        /// it throws the NotImplementedException.
        /// </summary>
        public override event WorkItemsGroupIdleHandler OnIdle
        {
            add
            {
                //_onIdle += value;
            }
            remove
            {
                //_onIdle -= value;
            }
        }

        internal override void PreQueueWorkItem()
        {
            ValidateNotDisposed();
        }

        #endregion

        #region Join, Choice, Pipe, etc.

        /// <summary>
        /// Executes all actions in parallel.
        /// Returns when they all finish.
        /// </summary>
        /// <param name="actions">Actions to execute</param>
        public void Join(IEnumerable<Action> actions)
        {
            WIGStartInfo wigStartInfo = new() { StartSuspended = true };
            IWorkItemsGroup workItemsGroup = CreateWorkItemsGroup(int.MaxValue, wigStartInfo);
            foreach (Action action in actions)
            {
                workItemsGroup.QueueWorkItem(action);
            }
            workItemsGroup.Start();
            workItemsGroup.WaitForIdle();
        }

        /// <summary>
        /// Executes all actions in parallel.
        /// Returns when they all finish.
        /// </summary>
        /// <param name="actions">Actions to execute</param>
        public void Join(params Action[] actions)
        {
            Join((IEnumerable<Action>)actions);
        }

        private class ChoiceIndex
        {
            public int _index = -1;
        }

        /// <summary>
        /// Executes all actions in parallel
        /// Returns when the first one completes
        /// </summary>
        /// <param name="actions">Actions to execute</param>
        public int Choice(IEnumerable<Action> actions)
        {
            WIGStartInfo wigStartInfo = new() { StartSuspended = true };
            IWorkItemsGroup workItemsGroup = CreateWorkItemsGroup(int.MaxValue, wigStartInfo);

            ManualResetEvent anActionCompleted = new(false);

            ChoiceIndex choiceIndex = new();

            int i = 0;
            foreach (Action action in actions)
            {
                Action act = action;
                int value = i;
                workItemsGroup.QueueWorkItem(() => { act(); Interlocked.CompareExchange(ref choiceIndex._index, value, -1); anActionCompleted.Set(); });
                ++i;
            }
            workItemsGroup.Start();
            anActionCompleted.WaitOne();
            anActionCompleted.Dispose();

            return choiceIndex._index;
        }

        /// <summary>
        /// Executes all actions in parallel
        /// Returns when the first one completes
        /// </summary>
        /// <param name="actions">Actions to execute</param>
        public int Choice(params Action[] actions)
        {
            return Choice((IEnumerable<Action>)actions);
        }

        /// <summary>
        /// Executes actions in sequence asynchronously.
        /// Returns immediately.
        /// </summary>
        /// <param name="pipeState">A state context that passes </param>
        /// <param name="actions">Actions to execute in the order they should run</param>
        public void Pipe<T>(T pipeState, IEnumerable<Action<T>> actions)
        {
            WIGStartInfo wigStartInfo = new() { StartSuspended = true };
            IWorkItemsGroup workItemsGroup = CreateWorkItemsGroup(1, wigStartInfo);
            foreach (Action<T> action in actions)
            {
                Action<T> act = action;
                workItemsGroup.QueueWorkItem(() => act(pipeState));
            }
            workItemsGroup.Start();
            workItemsGroup.WaitForIdle();
        }

        /// <summary>
        /// Executes actions in sequence asynchronously.
        /// Returns immediately.
        /// </summary>
        /// <param name="pipeState"></param>
        /// <param name="actions">Actions to execute in the order they should run</param>
        public void Pipe<T>(T pipeState, params Action<T>[] actions)
        {
            Pipe(pipeState, (IEnumerable<Action<T>>)actions);
        }
        #endregion
    }
    #endregion
}

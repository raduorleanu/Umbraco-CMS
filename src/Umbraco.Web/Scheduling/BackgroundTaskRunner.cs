﻿using System;
using System.Threading;
using System.Threading.Tasks;
using System.Threading.Tasks.Dataflow;
using System.Web.Hosting;
using Umbraco.Core;
using Umbraco.Core.Events;
using Umbraco.Core.Logging;
using LightInject;

namespace Umbraco.Web.Scheduling
{
    /// <summary>
    /// Manages a queue of tasks and runs them in the background.
    /// </summary>
    /// <remarks>This class exists for logging purposes - the one you want to use is BackgroundTaskRunner{T}.</remarks>
    public abstract class BackgroundTaskRunner
    { }

    /// <summary>
    /// Manages a queue of tasks of type <typeparamref name="T"/> and runs them in the background.
    /// </summary>
    /// <typeparam name="T">The type of the managed tasks.</typeparam>
    /// <remarks>The task runner is web-aware and will ensure that it shuts down correctly when the AppDomain
    /// shuts down (ie is unloaded).</remarks>
    public class BackgroundTaskRunner<T> : BackgroundTaskRunner, IBackgroundTaskRunner<T>
        where T : class, IBackgroundTask
    {
        // do not remove this comment!
        //
        // if you plan to do anything on this class, first go and read
        // http://blog.stephencleary.com/2012/12/dont-block-in-asynchronous-code.html
        // http://stackoverflow.com/questions/19481964/calling-taskcompletionsource-setresult-in-a-non-blocking-manner
        // http://stackoverflow.com/questions/21225361/is-there-anything-like-asynchronous-blockingcollectiont
        // and more, and more, and more
        // and remember: async is hard

        private readonly string _logPrefix;
        private readonly BackgroundTaskRunnerOptions _options;
        private readonly ILogger _logger;
        private readonly object _locker = new object();

        private readonly BufferBlock<T> _tasks = new BufferBlock<T>(new DataflowBlockOptions());

        // in various places we are testing these vars outside a lock, so make them volatile
        private volatile bool _isRunning; // is running
        private volatile bool _completed; // does not accept tasks anymore, may still be running

        private Task _runningTask; // the threading task that is currently executing background tasks
        private CancellationTokenSource _shutdownTokenSource; // used to cancel everything and shutdown
        private CancellationTokenSource _cancelTokenSource; // used to cancel the current task
        private CancellationToken _shutdownToken;

        private bool _terminating; // ensures we raise that event only once
        private bool _terminated; // remember we've terminated
        private readonly TaskCompletionSource<int> _terminatedSource = new TaskCompletionSource<int>(); // enable awaiting termination

        // fixme - this is temp
        // at the moment MainDom is internal so we have to find a way to hook into it - temp
        public class MainDomHook
        {
            private MainDomHook(MainDom mainDom, Action install, Action release)
            {
                MainDom = mainDom;
                Install = install;
                Release = release;
            }

            internal MainDom MainDom { get; }
            public Action Install { get; }
            public Action Release { get; }

            public static MainDomHook Create(Action install, Action release)
            {
                return new MainDomHook(Core.Composing.Current.Container.GetInstance<MainDom>(), install, release);
            }

            public static MainDomHook CreateForTest(Action install, Action release)
            {
                return new MainDomHook(null, install, release);
            }

            public bool Register()
            {
                if (MainDom != null)
                    return MainDom.Register(Install, Release);

                // tests
                Install?.Invoke();
                return true;
            }
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="BackgroundTaskRunner{T}"/> class.
        /// </summary>
        /// <param name="logger">A logger.</param>
        /// <param name="hook">An optional main domain hook.</param>
        public BackgroundTaskRunner(ILogger logger, MainDomHook hook = null)
            : this(typeof(T).FullName, new BackgroundTaskRunnerOptions(), logger, hook)
        { }

        /// <summary>
        /// Initializes a new instance of the <see cref="BackgroundTaskRunner{T}"/> class.
        /// </summary>
        /// <param name="name">The name of the runner.</param>
        /// <param name="logger">A logger.</param>
        /// <param name="hook">An optional main domain hook.</param>
        public BackgroundTaskRunner(string name, ILogger logger, MainDomHook hook = null)
            : this(name, new BackgroundTaskRunnerOptions(), logger, hook)
        { }

        /// <summary>
        /// Initializes a new instance of the <see cref="BackgroundTaskRunner{T}"/> class with a set of options.
        /// </summary>
        /// <param name="options">The set of options.</param>
        /// <param name="logger">A logger.</param>
        /// <param name="hook">An optional main domain hook.</param>
        public BackgroundTaskRunner(BackgroundTaskRunnerOptions options, ILogger logger, MainDomHook hook = null)
            : this(typeof(T).FullName, options, logger, hook)
        { }

        /// <summary>
        /// Initializes a new instance of the <see cref="BackgroundTaskRunner{T}"/> class with a set of options.
        /// </summary>
        /// <param name="name">The name of the runner.</param>
        /// <param name="options">The set of options.</param>
        /// <param name="logger">A logger.</param>
        /// <param name="hook">An optional main domain hook.</param>
        public BackgroundTaskRunner(string name, BackgroundTaskRunnerOptions options, ILogger logger, MainDomHook hook = null)
        {
            if (options == null) throw new ArgumentNullException(nameof(options));
            if (logger == null) throw new ArgumentNullException(nameof(logger));
            _options = options;
            _logPrefix = "[" + name + "] ";
            _logger = logger;

            if (options.Hosted)
                HostingEnvironment.RegisterObject(this);

            if (hook != null)
                _completed = _terminated = hook.Register() == false;

            if (options.AutoStart && _terminated == false)
                StartUp();
        }

        /// <summary>
        /// Gets the number of tasks in the queue.
        /// </summary>
        public int TaskCount => _tasks.Count;

        /// <summary>
        /// Gets a value indicating whether a threading task is currently running.
        /// </summary>
        public bool IsRunning => _isRunning;

        /// <summary>
        /// Gets a value indicating whether the runner has completed and cannot accept tasks anymore.
        /// </summary>
        public bool IsCompleted => _completed;

        /// <summary>
        /// Gets the running threading task as an immutable awaitable.
        /// </summary>
        /// <exception cref="InvalidOperationException">There is no running task.</exception>
        /// <remarks>
        /// <para>Unless the AutoStart option is true, there will be no current threading task until
        /// a background task is added to the queue, and there will be no current threading task
        /// when the queue is empty. In which case this method returns null.</para>
        /// <para>The returned value can be awaited and that is all (eg no continuation).</para>
        /// </remarks>
        internal ThreadingTaskImmutable CurrentThreadingTask
        {
            get
            {
                lock (_locker)
                {
                    return _runningTask == null ? null : new ThreadingTaskImmutable(_runningTask);
                }
            }
        }

        /// <summary>
        /// Gets an awaitable used to await the runner running operation.
        /// </summary>
        /// <returns>An awaitable instance.</returns>
        /// <remarks>Used to wait until the runner is no longer running (IsRunning == false),
        /// though the runner could be started again afterwards by adding tasks to it. If
        /// the runner is not running, returns a completed awaitable.</remarks>
        public ThreadingTaskImmutable StoppedAwaitable
        {
            get
            {
                lock (_locker)
                {
                    var task = _runningTask ?? Task.FromResult(0);
                    return new ThreadingTaskImmutable(task);
                }
            }
        }

        /// <summary>
        /// Gets an awaitable object that can be used to await for the runner to terminate.
        /// </summary>
        /// <returns>An awaitable object.</returns>
        /// <remarks>
        /// <para>Used to wait until the runner has terminated.</para>
        /// <para>This is for unit tests and should not be used otherwise. In most cases when the runner
        /// has terminated, the application domain is going down and it is not the right time to do things.</para>
        /// </remarks>
        internal ThreadingTaskImmutable TerminatedAwaitable
        {
            get
            {
                lock (_locker)
                {
                    return new ThreadingTaskImmutable(_terminatedSource.Task);
                }
            }
        }

        /// <summary>
        /// Adds a task to the queue.
        /// </summary>
        /// <param name="task">The task to add.</param>
        /// <exception cref="InvalidOperationException">The task runner has completed.</exception>
        public void Add(T task)
        {
            lock (_locker)
            {
                if (_completed)
                    throw new InvalidOperationException("The task runner has completed.");

                // add task
                _logger.Debug<BackgroundTaskRunner>("{LogPrefix} Task Added {TaskType}", _logPrefix , task.GetType().FullName);
                _tasks.Post(task);

                // start
                StartUpLocked();
            }
        }

        /// <summary>
        /// Tries to add a task to the queue.
        /// </summary>
        /// <param name="task">The task to add.</param>
        /// <returns>true if the task could be added to the queue; otherwise false.</returns>
        /// <remarks>Returns false if the runner is completed.</remarks>
        public bool TryAdd(T task)
        {
            lock (_locker)
            {
                if (_completed)
                {
                    _logger.Debug<BackgroundTaskRunner>("{LogPrefix} Task cannot be added {TaskType}, the task runner has already shutdown", _logPrefix, task.GetType().FullName);
                    return false;
                }

                // add task
                _logger.Debug<BackgroundTaskRunner>("{LogPrefix} Task added {TaskType}", _logPrefix, task.GetType().FullName);
                _tasks.Post(task);

                // start
                StartUpLocked();

                return true;
            }
        }

        /// <summary>
        /// Cancels to current task, if any.
        /// </summary>
        /// <remarks>Has no effect if the task runs synchronously, or does not want to cancel.</remarks>
        public void CancelCurrentBackgroundTask()
        {
            lock (_locker)
            {
                if (_completed)
                    throw new InvalidOperationException("The task runner has completed.");
                _cancelTokenSource?.Cancel();
            }
        }

        /// <summary>
        /// Starts the tasks runner, if not already running.
        /// </summary>
        /// <remarks>Is invoked each time a task is added, to ensure it is going to be processed.</remarks>
        /// <exception cref="InvalidOperationException">The task runner has completed.</exception>
        internal void StartUp()
        {
            if (_isRunning) return;

            lock (_locker)
            {
                if (_completed)
                    throw new InvalidOperationException("The task runner has completed.");

                StartUpLocked();
            }
        }

        /// <summary>
        /// Starts the tasks runner, if not already running.
        /// </summary>
        /// <remarks>Must be invoked within lock(_locker) and with _isCompleted being false.</remarks>
        private void StartUpLocked()
        {
            // double check
            if (_isRunning) return;
            _isRunning = true;

            // create a new token source since this is a new process
            _shutdownTokenSource = new CancellationTokenSource();
            _shutdownToken = _shutdownTokenSource.Token;
            _runningTask = Task.Run(async () => await Pump().ConfigureAwait(false), _shutdownToken);

            _logger.Debug<BackgroundTaskRunner>("{LogPrefix} Starting", _logPrefix);
        }

        /// <summary>
        /// Shuts the taks runner down.
        /// </summary>
        /// <param name="force">True for force the runner to stop.</param>
        /// <param name="wait">True to wait until the runner has stopped.</param>
        /// <remarks>If <paramref name="force"/> is false, no more tasks can be queued but all queued tasks
        /// will run. If it is true, then only the current one (if any) will end and no other task will run.</remarks>
        public void Shutdown(bool force, bool wait)
        {
            lock (_locker)
            {
                _completed = true; // do not accept new tasks
                if (_isRunning == false) return; // done already
            }

            // complete the queue
            // will stop waiting on the queue or on a latch
            _tasks.Complete();

            if (force)
            {
                // we must bring everything down, now
                Thread.Sleep(100); // give time to Complete()
                lock (_locker)
                {
                    // was Complete() enough?
                    if (_isRunning == false) return;
                }
                // try to cancel running async tasks (cannot do much about sync tasks)
                // break latched tasks
                // stop processing the queue
                _shutdownTokenSource.Cancel(false); // false is the default
            }

            // tasks in the queue will be executed...
            if (wait == false) return;

            _runningTask?.Wait(); // wait for whatever is running to end...
        }

        private async Task Pump()
        {
            while (true)
            {
                // get the next task
                // if it returns null the runner is going down, stop
                var bgTask = await GetNextBackgroundTask(_shutdownToken);
                if (bgTask == null) return;

                // set a cancellation source so that the current task can be cancelled
                // link from _shutdownToken so that we can use _cancelTokenSource for both
                lock (_locker)
                {
                    _cancelTokenSource = CancellationTokenSource.CreateLinkedTokenSource(_shutdownToken);
                }

                // wait for latch should return the task
                // if it returns null it's either that the task has been cancelled
                // or the whole runner is going down - in both cases, continue,
                // and GetNextBackgroundTask will take care of shutdowns
                bgTask = await WaitForLatch(bgTask, _cancelTokenSource.Token);
                if (bgTask == null) continue;

                // executes & be safe - RunAsync should NOT throw but only raise an event,
                // but... just make sure we never ever take everything down
                try
                {
                    await RunAsync(bgTask, _cancelTokenSource.Token).ConfigureAwait(false);
                }
                catch (Exception e)
                {
                    _logger.Error<BackgroundTaskRunner>("{LogPrefix} Task runner exception", e, _logPrefix);
                }

                // done
                lock (_locker)
                {
                    _cancelTokenSource = null;
                }
            }
        }

        // gets the next background task from the buffer
        private async Task<T> GetNextBackgroundTask(CancellationToken token)
        {
            while (true)
            {
                var task = await GetNextBackgroundTask2(token);
                if (task != null) return task;

                lock (_locker)
                {
                    // deal with race condition
                    if (_shutdownToken.IsCancellationRequested == false && _tasks.Count > 0) continue;

                    // if we really have nothing to do, stop
                    _logger.Debug<BackgroundTaskRunner>("{LogPrefix} Stopping", _logPrefix);

                    if (_options.PreserveRunningTask == false)
                        _runningTask = null;
                    _isRunning = false;
                    _shutdownToken = CancellationToken.None;
                }

                OnEvent(Stopped, "Stopped");
                return null;
            }
        }

        private async Task<T> GetNextBackgroundTask2(CancellationToken shutdownToken)
        {
            // exit if cancelling
            if (shutdownToken.IsCancellationRequested)
                return null;

            // if keepalive is false then don't block, exit if there is
            // no task in the buffer - yes, there is a race cond, which
            // we'll take care of
            if (_options.KeepAlive == false && _tasks.Count == 0)
                return null;

            try
            {
                // A Task<TResult> that informs of whether and when more output is available. If, when the
                // task completes, its Result is true, more output is available in the source (though another
                // consumer of the source may retrieve the data). If it returns false, more output is not
                // and will never be available, due to the source completing prior to output being available.

                var output = await _tasks.OutputAvailableAsync(shutdownToken); // block until output or cancelled
                if (output == false) return null;
            }
            catch (TaskCanceledException)
            {
                return null;
            }

            try
            {
                // A task that represents the asynchronous receive operation. When an item value is successfully
                // received from the source, the returned task is completed and its Result returns the received
                // value. If an item value cannot be retrieved because the source is empty and completed, an
                // InvalidOperationException exception is thrown in the returned task.

                // the source cannot be empty *and* completed here - we know we have output
                return await _tasks.ReceiveAsync(shutdownToken);
            }
            catch (TaskCanceledException)
            {
                return null;
            }
        }

        // if bgTask is not a latched background task, or if it is not latched, returns immediately
        // else waits for the latch, taking care of completion and shutdown and whatnot
        private async Task<T> WaitForLatch(T bgTask, CancellationToken token)
        {
            var latched = bgTask as ILatchedBackgroundTask;
            if (latched == null || latched.IsLatched == false) return bgTask;

            // support cancelling awaiting
            // read https://github.com/dotnet/corefx/issues/2704
            // read http://stackoverflow.com/questions/27238232/how-can-i-cancel-task-whenall
            var tokenTaskSource = new TaskCompletionSource<bool>();
            token.Register(s => ((TaskCompletionSource<bool>)s).SetResult(true), tokenTaskSource);

            // returns the task that completed
            // - latched.Latch completes when the latch releases
            // - _tasks.Completion completes when the runner completes
            // -  tokenTaskSource.Task completes when this task, or the whole runner, is cancelled
            var task = await Task.WhenAny(latched.Latch, _tasks.Completion, tokenTaskSource.Task);

            // ok to run now
            if (task == latched.Latch)
                return bgTask;

            // if shutting down, return the task only if it runs on shutdown
            if (_shutdownToken.IsCancellationRequested == false && latched.RunsOnShutdown) return bgTask;

            // else, either it does not run on shutdown or it's been cancelled, dispose
            latched.Dispose();
            return null;
        }

        // runs the background task, taking care of shutdown (as far as possible - cannot abort
        // a non-async Run for example, so we'll do our best)
        private async Task RunAsync(T bgTask, CancellationToken token)
        {
            try
            {
                OnTaskStarting(new TaskEventArgs<T>(bgTask));

                try
                {
                    try
                    {
                        if (bgTask.IsAsync)
                            //configure await = false since we don't care about the context, we're on a background thread.
                            await bgTask.RunAsync(token).ConfigureAwait(false);
                        else
                            bgTask.Run();
                    }
                    finally // ensure we disposed - unless latched again ie wants to re-run
                    {
                        var lbgTask = bgTask as ILatchedBackgroundTask;
                        if (lbgTask == null || lbgTask.IsLatched == false)
                            bgTask.Dispose();
                    }
                }
                catch (Exception e)
                {
                    OnTaskError(new TaskEventArgs<T>(bgTask, e));
                    throw;
                }

                OnTaskCompleted(new TaskEventArgs<T>(bgTask));
            }
            catch (Exception ex)
            {

                _logger.Error<BackgroundTaskRunner>("{LogPrefix} Task has failed", ex, _logPrefix);
            }
        }

        #region Events

        // triggers when a background task starts
        public event TypedEventHandler<BackgroundTaskRunner<T>, TaskEventArgs<T>> TaskStarting;

        // triggers when a background task has completed
        public event TypedEventHandler<BackgroundTaskRunner<T>, TaskEventArgs<T>> TaskCompleted;

        // triggers when a background task throws
        public event TypedEventHandler<BackgroundTaskRunner<T>, TaskEventArgs<T>> TaskError;

        // triggers when a background task is cancelled
        public event TypedEventHandler<BackgroundTaskRunner<T>, TaskEventArgs<T>> TaskCancelled;

        // triggers when the runner stops (but could start again if a task is added to it)
        internal event TypedEventHandler<BackgroundTaskRunner<T>, EventArgs> Stopped;

        // triggers when the hosting environment requests that the runner terminates
        internal event TypedEventHandler<BackgroundTaskRunner<T>, EventArgs> Terminating;

        // triggers when the runner has terminated (no task can be added, no task is running)
        internal event TypedEventHandler<BackgroundTaskRunner<T>, EventArgs> Terminated;

        private void OnEvent(TypedEventHandler<BackgroundTaskRunner<T>, EventArgs> handler, string name)
        {
            if (handler == null) return;
            OnEvent(handler, name, EventArgs.Empty);
        }

        private void OnEvent<TArgs>(TypedEventHandler<BackgroundTaskRunner<T>, TArgs> handler, string name, TArgs e)
        {
            if (handler == null) return;

            try
            {
                handler(this, e);
            }
            catch (Exception ex)
            {
                _logger.Error<BackgroundTaskRunner>(_logPrefix + name + " exception occurred", ex);
            }
        }

        protected virtual void OnTaskError(TaskEventArgs<T> e)
        {
            OnEvent(TaskError, "TaskError", e);
        }

        protected virtual void OnTaskStarting(TaskEventArgs<T> e)
        {
            OnEvent(TaskStarting, "TaskStarting", e);
        }

        protected virtual void OnTaskCompleted(TaskEventArgs<T> e)
        {
            OnEvent(TaskCompleted, "TaskCompleted", e);
        }

        protected virtual void OnTaskCancelled(TaskEventArgs<T> e)
        {
            OnEvent(TaskCancelled, "TaskCancelled", e);

            // dispose it
            e.Task.Dispose();
        }

        #endregion

        #region IDisposable

        private readonly object _disposalLocker = new object();
        public bool IsDisposed { get; private set; }

        ~BackgroundTaskRunner()
        {
            Dispose(false);
        }

        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        protected virtual void Dispose(bool disposing)
        {
            if (IsDisposed || disposing == false)
                return;

            lock (_disposalLocker)
            {
                if (IsDisposed)
                    return;
                DisposeResources();
                IsDisposed = true;
            }
        }

        protected virtual void DisposeResources()
        {
            // just make sure we eventually go down
            Shutdown(true, false);
        }

        #endregion

        /// <summary>
        /// Requests a registered object to unregister.
        /// </summary>
        /// <param name="immediate">true to indicate the registered object should unregister from the hosting
        /// environment before returning; otherwise, false.</param>
        /// <remarks>
        /// <para>"When the application manager needs to stop a registered object, it will call the Stop method."</para>
        /// <para>The application manager will call the Stop method to ask a registered object to unregister. During
        /// processing of the Stop method, the registered object must call the HostingEnvironment.UnregisterObject method.</para>
        /// </remarks>
        public void Stop(bool immediate)
        {
            // the first time the hosting environment requests that the runner terminates,
            // raise the Terminating event - that could be used to prevent any process that
            // would expect the runner to be available from starting.
            var onTerminating = false;
            lock (_locker)
            {
                if (_terminating == false)
                {
                    _terminating = true;
                    _logger.Info<BackgroundTaskRunner>("{LogPrefix} Terminating {Immediate}", _logPrefix, immediate ? immediate.ToString() : string.Empty);
                    onTerminating = true;
                }
            }

            if (onTerminating)
                OnEvent(Terminating, "Terminating");

            if (immediate == false)
            {
                // The Stop method is first called with the immediate parameter set to false. The object can either complete
                // processing, call the UnregisterObject method, and then return or it can return immediately and complete
                // processing asynchronously before calling the UnregisterObject method.

                _logger.Info<BackgroundTaskRunner>("{LogPrefix} Waiting for tasks to complete", _logPrefix);
                Shutdown(false, false); // do not accept any more tasks, flush the queue, do not wait

                // raise the completed event only after the running threading task has completed
                lock (_locker)
                {
                    if (_runningTask != null)
                        _runningTask.ContinueWith(_ => Terminate(false));
                    else
                        Terminate(false);
                }
            }
            else
            {
                // If the registered object does not complete processing before the application manager's time-out
                // period expires, the Stop method is called again with the immediate parameter set to true. When the
                // immediate parameter is true, the registered object must call the UnregisterObject method before returning;
                // otherwise, its registration will be removed by the application manager.

                _logger.Info<BackgroundTaskRunner>("{LogPrefix} Cancelling tasks", _logPrefix);
                Shutdown(true, true); // cancel all tasks, wait for the current one to end
                Terminate(true);
            }
        }

        // called by Stop either immediately or eventually
        private void Terminate(bool immediate)
        {
            // signal the environment we have terminated
            // log
            // raise the Terminated event
            // complete the awaitable completion source, if any

            HostingEnvironment.UnregisterObject(this);

            TaskCompletionSource<int> terminatedSource;
            lock (_locker)
            {
                _terminated = true;
                terminatedSource = _terminatedSource;
            }

            _logger.Info<BackgroundTaskRunner>("{LogPrefix} Tasks {TaskStatus}, terminated",
                _logPrefix,
                immediate ? "cancelled" : "completed");

            OnEvent(Terminated, "Terminated");

            terminatedSource.SetResult(0);
        }
    }
}

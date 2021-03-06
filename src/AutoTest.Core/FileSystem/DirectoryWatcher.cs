using System.Timers;
using System.Collections.Generic;
using AutoTest.Core.Messaging;
using System.IO;
using AutoTest.Core.DebugLog;
using AutoTest.Core.Configuration;
using AutoTest.Messages;
using AutoTest.Core.Launchers;

namespace AutoTest.Core.FileSystem
{
    public class DirectoryWatcher : IDirectoryWatcher
    {
        private readonly IMessageBus _bus;
        private readonly FileSystemWatcher _watcher;
		private IHandleDelayedConfiguration _delayedConfigurer;
        private IWatchPathLocator _watchPathLocator;
		private IApplicatonLauncher _launcer;
        private System.Timers.Timer _batchTimer;
        private bool _timerIsRunning = false;
        private List<ChangedFile> _buffer = new List<ChangedFile>();
        private object _padLock = new object();
        private IWatchValidator _validator;
		private IConfiguration _configuration;
		private string _watchPath = "";
        private bool _paused = true;
        private string _ignorePath = "";
        private bool _isWatchingSolution = false;

        public bool IsPaused { get { return _paused; } }

        public DirectoryWatcher(IMessageBus bus, IWatchValidator validator, IConfiguration configuration, IHandleDelayedConfiguration delayedConfigurer, IWatchPathLocator watchPathLocator, IApplicatonLauncher launcer)
        {
            _bus = bus;
            _validator = validator;
			_configuration = configuration;
			_delayedConfigurer = delayedConfigurer;
            _watchPathLocator = watchPathLocator;
			_launcer = launcer;
            _watcher = new FileSystemWatcher
                           {
                               NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.Attributes,
                               IncludeSubdirectories = true,
                               Filter = "*.*",
                           };
            
            _watcher.Changed += WatcherChangeHandler;
            _watcher.Created += WatcherChangeHandler;
            _watcher.Deleted += WatcherChangeHandler;
            _watcher.Renamed += WatcherChangeHandler;
            _watcher.Error += WatcherErrorHandler;
            if (!_configuration.StartPaused)
                Resume();
        }

        public void Pause()
        {
            _paused = true;
        }

        public void Resume()
        {
            _paused = false;
        }

        public void Watch(string path)
        {
            _isWatchingSolution = File.Exists(path);
            if (_isWatchingSolution)
                path = Path.GetDirectoryName(path);
            if (!Directory.Exists(path))
            {
                _bus.Publish(new ErrorMessage(string.Format("Invalid watch directory \"{0}\".", path)));
                return;
            }
			mergeLocalConfig(path);
			initializeTimer();
			setupPreProcessors();
			initializeWatchPath(path);
            setWatchPath(path);
            _watcher.EnableRaisingEvents = true;
            if (_configuration.StartPaused)
                Pause();
            else
                Resume();
        }

        private void setWatchPath(string path)
        {
            if (_isWatchingSolution && _configuration.UseLowestCommonDenominatorAsWatchPath)
                path = _watchPathLocator.Locate(path);
            Debug.WriteDebug("Watching {0}, IsWatchingSolution = {1}, UseLowestCommonDenominatorAsWatchPath = {2}", path, _isWatchingSolution, _configuration.UseLowestCommonDenominatorAsWatchPath);
            _bus.Publish(new InformationMessage(string.Format("Starting AutoTest.Net and watching \"{0}\" and all subdirectories.", path)));
            _watcher.Path = path;
			_launcer.Initialize(path);
        }
		
		private void setupPreProcessors()
		{
			if (_configuration.RerunFailedTestsFirst)
				_delayedConfigurer.AddRunFailedTestsFirstPreProcessor();
		}
		
		private void initializeWatchPath(string path)
		{
			if (path.EndsWith(Path.DirectorySeparatorChar.ToString()))
				_watchPath = Path.GetDirectoryName(path);
			else
				_watchPath = path;
            _ignorePath = Path.GetDirectoryName(new PathParser(_configuration.IgnoreFile).ToAbsolute(_watchPath));
            if (!Directory.Exists(_ignorePath) || _configuration.IgnoreFile.Trim().Length == 0)
                _ignorePath = _watchPath;
		}
		
		private void initializeTimer()
		{
			_batchTimer = new Timer(_configuration.FileChangeBatchDelay);
            _batchTimer.Enabled = true;
            _batchTimer.Elapsed += _batchTimer_Elapsed;
		}
		
		private void mergeLocalConfig(string path)
		{
			var file = Path.Combine(path, "AutoTest.config");
			if (File.Exists(file))
				_bus.Publish(new InformationMessage("Loading local config file"));
			_configuration.Reload(file);
		}

        private void WatcherChangeHandler(object sender, FileSystemEventArgs e)
        {
            if (_paused)
                return;
            addToBuffer(new ChangedFile(e.FullPath));
        }

        void WatcherErrorHandler(object sender, ErrorEventArgs e)
        {
            Debug.WriteDebug("FileSystemWatcher failed to handle changes");
            Debug.WriteDebug(e.GetException().ToString());
        }

        private void _batchTimer_Elapsed(object sender, ElapsedEventArgs e)
        {
            lock (_padLock)
            {
                if (_buffer.Count > 0)
                {
                    Debug.AboutToPublishFileChanges(_buffer.Count);
                    var fileChange = new FileChangeMessage();
                    fileChange.AddFile(_buffer.ToArray());
                    _bus.Publish(fileChange);
                }
                _buffer.Clear();
                stopTimer();
            }
        }

        private void addToBuffer(ChangedFile file)
        {
            if (Directory.Exists(file.FullName))
                return;
            if (!_validator.ShouldPublish(getRelativePath(file.FullName)))
                return;

            lock (_padLock)
            {
                if (_buffer.FindIndex(0, f => f.FullName.Equals(file.FullName)) < 0)
                {
                    _buffer.Add(file);
                    reStartTimer();
                }
            }
        }
		
		private string getRelativePath(string path)
		{
			if (path.StartsWith(_ignorePath))
                return path.Substring(_ignorePath.Length, path.Length - _ignorePath.Length);
			return path;
		}

        private void reStartTimer()
        {
            stopTimer();
            startTimer();
        }

        private void startTimer()
        {
            if (!_timerIsRunning)
            {
                _batchTimer.Start();
                _timerIsRunning = true;
            }
        }

        private void stopTimer()
        {
            _batchTimer.Stop();
            _timerIsRunning = false;
        }

        /// <summary>
        /// Performs application-defined tasks associated with freeing, releasing, or resetting unmanaged resources.
        /// </summary>
        public void Dispose()
        {
            _watcher.Dispose();
        }
    }
}
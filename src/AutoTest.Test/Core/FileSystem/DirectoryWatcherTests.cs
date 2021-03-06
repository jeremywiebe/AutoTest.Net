using System.Threading;
using AutoTest.Core.FileSystem;
using AutoTest.Core.Messaging;
using Rhino.Mocks;
using NUnit.Framework;
using System;
using System.IO;
using AutoTest.Core.Configuration;
using AutoTest.Messages;
using AutoTest.Core.Launchers;

namespace AutoTest.Test.Core
{
    [TestFixture]
    public class DirectoryWatcherTests
    {
        private string _file;
        private string _directory;
		private string _localConfig;
		private string _watchDirectory;
        private IMessageBus _messageBus;
        private IWatchValidator _validator;
		private IConfiguration _configuration;
        private DirectoryWatcher _watcher;
        private IWatchPathLocator _pathLocator;
		private IApplicatonLauncher _launcer;

        [SetUp]
        public void SetUp()
        {
			_launcer = MockRepository.GenerateMock<IApplicatonLauncher>();
            _messageBus = MockRepository.GenerateMock<IMessageBus>();
            _validator = MockRepository.GenerateMock<IWatchValidator>();
			_configuration = MockRepository.GenerateMock<IConfiguration>();
            _pathLocator = MockRepository.GenerateMock<IWatchPathLocator>();
            _configuration.Stub(x => x.IgnoreFile).Return("");
			_validator.Stub(v => v.GetIgnorePatterns()).Return("");
			_configuration.Stub(c => c.FileChangeBatchDelay).Return(50);
            _watcher = new DirectoryWatcher(_messageBus, _validator, _configuration, MockRepository.GenerateMock<IHandleDelayedConfiguration>(), _pathLocator, _launcer);
            _file = Path.GetFullPath("watcher_test.txt");
            _directory = Path.GetFullPath("mytestfolder");
			_watchDirectory = Path.GetDirectoryName(_file);
            _pathLocator.Stub(x => x.Locate(_watchDirectory)).Return(_watchDirectory);
			_localConfig = Path.Combine(_watchDirectory, "AutoTest.config");
			File.WriteAllText(_localConfig, "<configuration></configuration>");
            _watcher.Watch(_watchDirectory);
        }

        [TearDown]
        public void TearDown()
        {
            _watcher.Dispose();
            File.Delete(_file);
			File.Delete(_localConfig);
            if (Directory.Exists(_directory))
                Directory.Delete(_directory);
        }

        [Test]
        public void Should_not_start_watch_when_folder_is_invalid()
        {
            var bus = MockRepository.GenerateMock<IMessageBus>();
			var config = MockRepository.GenerateMock<IConfiguration>();
			config.Stub(c => c.FileChangeBatchDelay).Return(50);
            var watcher = new DirectoryWatcher(bus, null, config, MockRepository.GenerateMock<IHandleDelayedConfiguration>(), _pathLocator, _launcer);
            watcher.Watch("");
            bus.AssertWasNotCalled(m => m.Publish<InformationMessage>(null), m => m.IgnoreArguments());
        }

        [Test]
        public void Should_send_message_when_file_changes_once()
        {
			var messageBus = MockRepository.GenerateMock<IMessageBus>();
            var validator = MockRepository.GenerateMock<IWatchValidator>();
			var configuration = MockRepository.GenerateMock<IConfiguration>();
            configuration.Stub(x => x.IgnoreFile).Return("");
			validator.Stub(v => v.GetIgnorePatterns()).Return("");
			configuration.Stub(c => c.FileChangeBatchDelay).Return(50);
            var watcher = new DirectoryWatcher(messageBus, validator, configuration, MockRepository.GenerateMock<IHandleDelayedConfiguration>(), _pathLocator, _launcer);
            var file = Path.GetFullPath("watcher_test_changes_once.txt");
			var watchDirectory = Path.GetDirectoryName(file);
            watcher.Watch(watchDirectory);
			
            validator.Stub(v => v.ShouldPublish(null)).IgnoreArguments().Return(true).Repeat.Any();
            // Write twice
            File.WriteAllText(file, "meh ");
            using (var writer = new StreamWriter(file, true)) { writer.WriteLine("some text"); }
            Thread.Sleep(450);
            
            messageBus.AssertWasCalled(
                m => m.Publish<FileChangeMessage>(
                         Arg<FileChangeMessage>.Matches(
                             f => f.Files.Length >  0 &&
                                  f.Files[0].Extension.Equals(Path.GetExtension(file)) &&
                                  f.Files[0].FullName.Equals(file) &&
                                  f.Files[0].Name.Equals(Path.GetFileName(file)))),
                m => m.Repeat.Once());
			
			File.Delete(file);
        }
        
        [Test]
        public void Should_not_publish_event_when_validator_invalidates_change()
        {
            _validator.Stub(v => v.ShouldPublish(null)).IgnoreArguments().Return(false);
            File.Create(_file).Dispose();
            Thread.Sleep(100);
            _messageBus.AssertWasNotCalled(m => m.Publish<FileChangeMessage>(null), m => m.IgnoreArguments());
        }
		
		[Test]
		public void Should_reload_configuration_with_local_config()
		{
			_configuration.AssertWasCalled(c => c.Reload(Path.Combine(_watchDirectory, "AutoTest.config")));
		}

        [Test]
        public void Should_not_detect_changes_when_paused()
        {
            var messageBus = MockRepository.GenerateMock<IMessageBus>();
            var validator = MockRepository.GenerateMock<IWatchValidator>();
            var configuration = MockRepository.GenerateMock<IConfiguration>();
            configuration.Stub(x => x.IgnoreFile).Return("");
            validator.Stub(v => v.GetIgnorePatterns()).Return("");
            validator.Stub(v => v.ShouldPublish(null)).IgnoreArguments().Return(true).Repeat.Any();
            configuration.Stub(c => c.FileChangeBatchDelay).Return(50);
            var watcher = new DirectoryWatcher(messageBus, validator, configuration, MockRepository.GenerateMock<IHandleDelayedConfiguration>(), _pathLocator, _launcer);
            var file = Path.GetFullPath("not_detection_when_paused.txt");
            var watchDirectory = Path.GetDirectoryName(file);
            watcher.Watch(watchDirectory);
            watcher.Pause();

            File.WriteAllText(file, "meh ");
            Thread.Sleep(450);

            messageBus.AssertWasNotCalled(m => m.Publish<FileChangeMessage>(null), m => m.IgnoreArguments());
        }

        [Test]
        public void Should_detect_changes_when_paused_and_resumed()
        {
            var messageBus = MockRepository.GenerateMock<IMessageBus>();
            var validator = MockRepository.GenerateMock<IWatchValidator>();
            var configuration = MockRepository.GenerateMock<IConfiguration>();
            configuration.Stub(x => x.IgnoreFile).Return("");
            validator.Stub(v => v.GetIgnorePatterns()).Return("");
            validator.Stub(v => v.ShouldPublish(null)).IgnoreArguments().Return(true).Repeat.Any();
            configuration.Stub(c => c.FileChangeBatchDelay).Return(50);
            var watcher = new DirectoryWatcher(messageBus, validator, configuration, MockRepository.GenerateMock<IHandleDelayedConfiguration>(), _pathLocator, _launcer);
            var file = Path.GetFullPath("not_detection_when_paused.txt");
            var watchDirectory = Path.GetDirectoryName(file);
            watcher.Watch(watchDirectory);
            watcher.Pause();
            watcher.Resume();

            File.WriteAllText(file, "meh ");
            Thread.Sleep(450);

            messageBus.AssertWasCalled(m => m.Publish<FileChangeMessage>(null), m => m.IgnoreArguments());
        }

        [Test]
        public void When_config_setting_start_paused_is_set_pause_watcher()
        {
            var messageBus = MockRepository.GenerateMock<IMessageBus>();
            var validator = MockRepository.GenerateMock<IWatchValidator>();
            var configuration = MockRepository.GenerateMock<IConfiguration>();
            configuration.Stub(x => x.IgnoreFile).Return("");
            validator.Stub(v => v.GetIgnorePatterns()).Return("");
            validator.Stub(v => v.ShouldPublish(null)).IgnoreArguments().Return(true).Repeat.Any();
            configuration.Stub(c => c.FileChangeBatchDelay).Return(50);
            configuration.Stub(c => c.StartPaused).Return(true);
            var watcher = new DirectoryWatcher(messageBus, validator, configuration, MockRepository.GenerateMock<IHandleDelayedConfiguration>(), _pathLocator, _launcer);
            var file = Path.GetFullPath("start_as_paused.txt");
            var watchDirectory = Path.GetDirectoryName(file);
            watcher.Watch(watchDirectory);

            File.WriteAllText(file, "meh ");
            Thread.Sleep(450);

            messageBus.AssertWasNotCalled(m => m.Publish<FileChangeMessage>(null), m => m.IgnoreArguments());
        }
		
		[Test]
        public void When_setting_wath_path_it_should_initialize_application_launcher()
        {
            var messageBus = MockRepository.GenerateMock<IMessageBus>();
            var validator = MockRepository.GenerateMock<IWatchValidator>();
            var configuration = MockRepository.GenerateMock<IConfiguration>();
			var launcher = MockRepository.GenerateMock<IApplicatonLauncher>();
            configuration.Stub(x => x.IgnoreFile).Return("");
            validator.Stub(v => v.GetIgnorePatterns()).Return("");
            validator.Stub(v => v.ShouldPublish(null)).IgnoreArguments().Return(true).Repeat.Any();
            configuration.Stub(c => c.FileChangeBatchDelay).Return(50);
            configuration.Stub(c => c.StartPaused).Return(true);
            var watcher = new DirectoryWatcher(messageBus, validator, configuration, MockRepository.GenerateMock<IHandleDelayedConfiguration>(), _pathLocator, launcher);
            var watchDirectory = Path.GetDirectoryName(Path.GetFullPath("somefile.txt"));
            watcher.Watch(watchDirectory);

            launcher.AssertWasCalled(x => x.Initialize(watchDirectory));
        }
    }
}
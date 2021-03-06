﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using AutoTest.Core.Messaging;
using AutoTest.Core.BuildRunners;
using AutoTest.Core.Configuration;
using AutoTest.Core.Caching;
using AutoTest.Core.Caching.Projects;
using AutoTest.Core.TestRunners;
using System.IO;
using AutoTest.Core.TestRunners.TestRunners;
using Castle.Core.Logging;
using AutoTest.Core.DebugLog;
using AutoTest.Messages;
using System.Threading;

namespace AutoTest.Core.Messaging.MessageConsumers
{
    class ProjectChangeConsumer : IOverridingConsumer<ProjectChangeMessage>
    {
        private bool _isRunning = false;
        private bool _exit = false;
        private IMessageBus _bus;
        private IGenerateBuildList _listGenerator;
        private IConfiguration _configuration;
        private IBuildRunner _buildRunner;
        private ITestRunner[] _testRunners;
		private IDetermineIfAssemblyShouldBeTested _testAssemblyValidator;
		private IOptimizeBuildConfiguration _buildOptimizer;
        private IPreProcessBuildruns[] _preBuildProcessors;
        private IPreProcessTestruns[] _preProcessors;
        private ILocateRemovedTests _removedTestLocator;
        private List<RunInfo> _abortedBuilds = new List<RunInfo>();
        private List<RunInfo> _abortedTestRuns = new List<RunInfo>();

        public bool IsRunning { get { return _isRunning; } }

        public ProjectChangeConsumer(IMessageBus bus, IGenerateBuildList listGenerator, IConfiguration configuration, IBuildRunner buildRunner, ITestRunner[] testRunners, IDetermineIfAssemblyShouldBeTested testAssemblyValidator, IOptimizeBuildConfiguration buildOptimizer, IPreProcessTestruns[] preProcessors, ILocateRemovedTests removedTestLocator, IPreProcessBuildruns[] preBuildProcessors)
        {
            _bus = bus;
            _listGenerator = listGenerator;
            _configuration = configuration;
            _buildRunner = buildRunner;
            _testRunners = testRunners;
			_testAssemblyValidator = testAssemblyValidator;
			_buildOptimizer = buildOptimizer;
            _preProcessors = preProcessors;
            _preBuildProcessors = preBuildProcessors;
            _removedTestLocator = removedTestLocator;
        }

        #region IConsumerOf<ProjectChangeMessage> Members

        public void Consume(ProjectChangeMessage message)
        {
            _isRunning = true;
            var now = DateTime.Now;
            Debug.ConsumingProjectChangeMessage(message);
            _bus.Publish(new RunStartedMessage(message.Files));
            var runReport = execute(message);
            runReport.SetTimeSpent(DateTime.Now.Subtract(now));
            _bus.Publish(new RunFinishedMessage(runReport));
            _exit = false;
            _isRunning = false;
        }

        public void Terminate()
        {
            if (!_isRunning)
                return;
            Debug.WriteDebug("Initiating termination of current run");
            _exit = true;
            while (_isRunning)
                Thread.Sleep(10);
        }

        private RunReport execute(ProjectChangeMessage message)
        {
            var runReport = new RunReport();
            try
            {
                Debug.WriteDebug("Starting project change run");
                var list = getPrioritizedList(message);
                if (!buildAll(list, runReport))
                    return runReport;
                if (_exit)
                {
                    _abortedBuilds.Clear();
                    _abortedBuilds.AddRange(list);
                    return runReport;
                }
                else
                    _abortedBuilds.Clear();
                markAllAsBuilt(list);
                testAll(list, runReport);
            }
            catch (Exception ex)
            {
                var result = new TestRunResults("", "", false, TestRunner.Any, new TestResult[] { new TestResult(TestRunner.Any, TestRunStatus.Failed, "AutoTest.Net internal error", ex.ToString()) });
                _bus.Publish(new TestRunMessage(result));
            }
            return runReport;
        }

        private RunInfo[] getPrioritizedList(ProjectChangeMessage message)
        {
            var projectsAndDependencies = _listGenerator.Generate(getListOfChangedProjects(message));
            var list = _buildOptimizer.AssembleBuildConfiguration(projectsAndDependencies);
            list = preProcessBuildRun(list);
            list = mergeWithAbortedBuilds(list);
            return list;
        }

        private RunInfo[] mergeWithAbortedBuilds(RunInfo[] list)
        {
            return new RunInfoMerger(list).MergeWith(_abortedBuilds).ToArray();
        }

        private RunInfo[] preProcessBuildRun(RunInfo[] runInfos)
        {
            foreach (var preProcessor in _preBuildProcessors)
                runInfos = preProcessor.PreProcess(runInfos);
            return runInfos;
        }

        private void markAllAsBuilt(RunInfo[] list)
        {
            foreach (var info in list)
                info.Project.Value.HasBeenBuilt();
        }

        private string[] getListOfChangedProjects(ProjectChangeMessage message)
        {
            var projects = new List<string>();
            foreach (var file in message.Files)
                projects.Add(file.FullName);
            return projects.ToArray();
        }
		
		private bool buildAll(RunInfo[] projectList, RunReport runReport)
		{
            if (projectList.Where(x => x.ShouldBeBuilt).Select(x => x).Count() == 0)
                return true;

            Debug.WriteInfo("Running builds");
            if (_configuration.ShouldBuildSolution)
                return buildSolution(projectList, runReport);
            else
                return buildProjects(projectList, runReport);
		}

        private bool buildProjects(RunInfo[] projectList, RunReport runReport)
        {
            var indirectlyBuilt = new List<string>();
            foreach (var file in projectList)
            {
                if (file.ShouldBeBuilt)
                {
                    Debug.WriteDebug("Set to build project {0}", file.Project.Key);
                    if (!build(file, runReport))
                        return false;
                }
                else
                {
                    Debug.WriteDebug("Not set to build project {0}", file.Project.Key);
                    indirectlyBuilt.Add(file.Project.Key);
                }
            }
            foreach (var project in indirectlyBuilt)
                runReport.AddBuild(project, new TimeSpan(0), true);
            return true;
        }

        private bool buildSolution(RunInfo[] projectList, RunReport runReport)
        {
            var buildExecutable = _configuration.BuildExecutable(new ProjectDocument(ProjectType.None));
            if (File.Exists(buildExecutable))
            {
                _bus.Publish(new RunInformationMessage(InformationType.Build, _configuration.WatchPath, "", typeof(MSBuildRunner)));

                var buildReport = _buildRunner.RunBuild(_configuration.WatchPath, projectList.Where(p => p.Project.Value.RequiresRebuild).Count() > 0, buildExecutable, () => { return _exit; });
                var succeeded = buildReport.ErrorCount == 0;
                runReport.AddBuild(_configuration.WatchPath, buildReport.TimeSpent, succeeded);
                _bus.Publish(new BuildRunMessage(buildReport));
                return succeeded;
            }
            return false;
        }
		
		private void testAll(RunInfo[] projectList, RunReport runReport)
		{
             projectList = preProcessTestRun(projectList);
            projectList = new TestRunInfoMerger(projectList).MergeWith(_abortedTestRuns).ToArray();
            runPreProcessedTestRun(projectList, runReport);
		}
		
		private void runPreProcessedTestRun(RunInfo[] projectList, RunReport runReport)
		{
			foreach (var runner in _testRunners)
            {
                Debug.WriteDebug("Preparing runner " + runner.GetType().ToString());
				var runInfos = new List<TestRunInfo>();
				foreach (var file in projectList)
	            {
					var project = file.Project;
					if (hasInvalidAssembly(file))
						continue;
	            	var assembly = file.Assembly;	
					if (_testAssemblyValidator.ShouldNotTestAssembly(assembly))
					    continue;
                    if (runner.CanHandleTestFor(assembly))
                    {
                        runInfos.Add(file.CloneToTestRunInfo());
                        _bus.Publish(new RunInformationMessage(InformationType.TestRun, project.Key, assembly, runner.GetType()));
                    }
				}
				if (runInfos.Count > 0)
				{
                    Debug.WriteDebug("Running tests for runner " + runner.GetType().ToString());
					runTests(runner, runInfos.ToArray(), runReport);
                    if (_exit)
                    {
                        _abortedTestRuns.Clear();
                        _abortedTestRuns.AddRange(projectList);
                        return;
                    }
					
					var rerunInfos = new List<TestRunInfo>();
					foreach (var info in runInfos)
					{
						if (info.RerunAllTestWhenFinishedForAny())
							rerunInfos.Add(new TestRunInfo(info.Project, info.Assembly));
					}
                    if (rerunInfos.Count > 0)
                    {
                        Debug.WriteDebug("Rerunning all tests for runner " + runner.GetType().ToString());
                        runTests(runner, rerunInfos.ToArray(), runReport);
                        if (_exit)
                        {
                            _abortedTestRuns.Clear();
                            _abortedTestRuns.AddRange(projectList);
                            return;
                        }
                    }
				}
			}
            _abortedTestRuns.Clear();
		}

        private RunInfo[] preProcessTestRun(RunInfo[] runInfos)
        {
            foreach (var preProcessor in _preProcessors)
                runInfos = preProcessor.PreProcess(runInfos);
            return runInfos;
        }
		
		private bool hasInvalidOutputPath(RunInfo info)
		{
			return info.Assembly == null;
		}
		
		private bool hasInvalidAssembly(RunInfo info)
		{
			if (info.Assembly == null)
			{
				_bus.Publish(new ErrorMessage(string.Format("Assembly was unexpectedly set to null for {0}. Skipping assembly", info.Project.Key)));
				return true;
			}
			return false;
		}

        private bool build(RunInfo info, RunReport runReport)
        {
            if (File.Exists(_configuration.BuildExecutable(info.Project.Value)))
            {
                _bus.Publish(new RunInformationMessage(
                                 InformationType.Build,
                                 info.Project.Key,
                                 info.Assembly,
                                 typeof(MSBuildRunner)));
                if (!buildProject(info.Project, runReport))
                    return false;
            }

            return true;
        }

        private bool buildProject(Project project, RunReport report)
        {
            var buildReport = _buildRunner.RunBuild(project, _configuration.BuildExecutable(project.Value), () => { return _exit; });
            var succeeded = buildReport.ErrorCount == 0;
            report.AddBuild(buildReport.Project, buildReport.TimeSpent, succeeded);
            _bus.Publish(new BuildRunMessage(buildReport));
            return succeeded;
        }

        #endregion

        private void runTests(ITestRunner testRunner, TestRunInfo[] runInfos, RunReport runReport)
        {
            var results = runTests(testRunner, runInfos);
            var modifiedResults = new List<TestRunResults>();
			foreach (var result in results)
			{
	            runReport.AddTestRun(
                    result.Project,
                    result.Assembly,
                    result.TimeSpent,
                    result.Passed.Length,
                    result.Ignored.Length,
                    result.Failed.Length);
                var modified = _removedTestLocator.SetRemovedTestsAsPassed(result, runInfos);
                _bus.Publish(new TestRunMessage(modified));
                modifiedResults.Add(modified);
			}
			informPreProcessor(modifiedResults.ToArray());
        }

        private TestRunResults[] runTests(ITestRunner testRunner, TestRunInfo[] runInfos)
        {
            try
            {
                return testRunner.RunTests(runInfos, () => { return _exit; });
            }
            catch (Exception ex)
            {
                return new TestRunResults[] { new TestRunResults("", testRunner.GetType().ToString(), false, TestRunner.Any, new TestResult[] { new TestResult(TestRunner.Any, TestRunStatus.Failed, "AutoTest.Net internal error", ex.ToString()) }) };
            }
        }
		
        private void informPreProcessor(TestRunResults[] results)
        {
            foreach (var preProcess in _preProcessors)
                preProcess.RunFinished(results);
        }
    }
}

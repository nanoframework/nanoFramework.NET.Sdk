//
// Copyright (c) .NET Foundation and Contributors
// See LICENSE file in the project root for full license information.
//

using System;
using System.Collections;
using System.Collections.Generic;
using Microsoft.Build.Framework;

namespace nanoFramework.Tools.BuildTasks.Tests.Infrastructure
{
    /// <summary>
    /// Minimal <see cref="IBuildEngine"/> that captures logged events so tests can
    /// assert on warnings and errors without a real MSBuild host.
    /// </summary>
    internal sealed class TestBuildEngine : IBuildEngine
    {
        public List<string> Warnings { get; } = new List<string>();
        public List<string> Errors { get; } = new List<string>();
        public List<string> Messages { get; } = new List<string>();

        public void LogWarningEvent(BuildWarningEventArgs e) => Warnings.Add(e.Message ?? string.Empty);
        public void LogErrorEvent(BuildErrorEventArgs e) => Errors.Add(e.Message ?? string.Empty);
        public void LogMessageEvent(BuildMessageEventArgs e) => Messages.Add(e.Message ?? string.Empty);
        public void LogCustomEvent(CustomBuildEventArgs e) { }

        public bool BuildProjectFile(string projectFileName, string[] targetNames,
            IDictionary globalProperties, IDictionary targetOutputs) =>
            throw new NotImplementedException();

        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => string.Empty;
    }

    /// <summary>
    /// Minimal <see cref="ITask"/> implementation that wires up a <see cref="TestBuildEngine"/>.
    /// Used to construct a <see cref="TaskLoggingHelper"/> without a real MSBuild host.
    /// </summary>
    internal sealed class TestTask : ITask
    {
        public IBuildEngine BuildEngine { get; set; } = new TestBuildEngine();
        public ITaskHost? HostObject { get; set; }
        public bool Execute() => true;
    }

}

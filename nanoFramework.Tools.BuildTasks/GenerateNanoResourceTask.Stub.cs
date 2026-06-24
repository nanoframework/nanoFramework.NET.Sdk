//
// Copyright (c) .NET Foundation and Contributors
// See LICENSE file in the project root for full license information.
//
// Stub implementation of GenerateNanoResourceTask for .NET 8+.
// The full implementation uses System.Windows.Forms ResXResourceReader and
// AppDomain APIs not available on modern .NET. Resource compilation is
// supported when building with full .NET Framework (msbuild.exe / VS).
//

#if !NETFRAMEWORK

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using System.ComponentModel;

namespace nanoFramework.Tools
{
    [Description("GenerateNanoResourceTaskEntry")]
    public class GenerateNanoResourceTask : Task
    {
        public ITaskItem[] Sources { get; set; }
        public ITaskItem[] References { get; set; }
        public bool UseSourcePath { get; set; }
        public string StateFile { get; set; }

        [Output]
        public ITaskItem[] OutputResources { get; set; }

        [Output]
        public ITaskItem[] FilesWritten { get; set; }

        public override bool Execute()
        {
            if (Sources == null || Sources.Length == 0)
            {
                return true;
            }

            Log.LogWarning(
                "NFSDK0001",
                "NFSDK0001",
                null, null, 0, 0, 0, 0,
                "GenerateNanoResourceTask is not yet supported when building with 'dotnet build'. " +
                "Resource (.resx) compilation requires building with full .NET Framework (msbuild.exe or Visual Studio). " +
                "This limitation will be addressed in a future SDK version.");

            return true;
        }
    }
}

#endif

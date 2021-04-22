#define DEBUG
using Serilog;
using Serilog.Formatting.Compact;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;

namespace updater
{
    class Program2
    {
        private static ILogger _log;

        static void Main()
        {
            Console.OutputEncoding = Encoding.UTF8;

            InitLogger();
            _log = Log.Logger;
            var productVersion = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion;
            _log.Information("Start application '{app}', version: {ver}", "updater2", productVersion);
            Thread.Sleep(3000);

            var exeDir = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppDomain.CurrentDomain.BaseDirectory));
            var exeDirFullName = exeDir.FullName;
            _log.Debug("exeDirFullName = '{exeDirFullName}'", exeDirFullName);

            var curDir = new DirectoryInfo(Directory.GetCurrentDirectory());
            var curDirFullName = curDir.FullName;
            _log.Debug("curDirFullName = '{curDirFullName}'", curDirFullName);

            _log.Debug("? '{curDirFullName}' == '{exeDirParentFullName}'", curDir.FullName, exeDir.Parent.FullName);
            if (curDir.FullName != exeDir.Parent.FullName)
            {
                _log.Fatal("Sanity check #1 failed! Current working directory '{curDirFullName}' must be parent for execution directory '{exeDirFullName}'", curDir.FullName, exeDir.FullName);
                throw new InvalidOperationException($"Sanity check #1 failed! Current working directory '{curDir.FullName}' must be parent for execution directory '{exeDir.FullName}'");
            }
            _log.Debug("? '{exeDirName}' == '{productVersion}'", exeDir.Name, productVersion);
            if (exeDir.Name != productVersion)
            {
                _log.Fatal("Sanity check #2 failed! Execution directory name '{exeDirName}' must be equal assembly version {productVersion}", exeDir.Name, productVersion);
                throw new InvalidOperationException($"Sanity check #2 failed! Execution directory name '{exeDir.Name}' must be equal assembly version {productVersion}");
            }
            var dirSource = exeDir;
            var dirTarget = curDir; // = exeDir.Parent

            try
            {
                // Remove old version of app in directory 'exeDir.Parent.FullName'
                _log.Information("Remove old files in dir '{dirTargetFullName}'", dirTarget.FullName);
                var excludeDirsList = new List<string>() {
                    productVersion,
                    @"Logs"
                };
                foreach (var delDir in dirTarget.GetDirectories())
                    if (excludeDirsList.Contains(delDir.Name))
                    {
                        _log.Debug($"SKIP dir '{delDir.Name}'");
                    }
                    else
                    {
                        _log.Debug($"Delete dir '{delDir.Name}'");
                        delDir.Delete(true);
                    };
                foreach (var file in dirTarget.GetFiles())
                    file.Delete();
                Thread.Sleep(1000);

                // Copy all files and directories from 'sourceDirectory' (./*.*) to 'targetDirectory' (../).
                _log.Information("Copy files from dir '{dirSourceFullName}' into dir '{dirTargetFullName}'", dirSource.FullName, dirTarget.FullName);
                CopyFilesRecursively(dirSource, dirTarget);
                Thread.Sleep(3000);
            }
            catch (Exception e)
            {
                _log.Fatal("Something went wrong: {error}", e.Message);
            }

            _log.Information("Restart application!");
            var processInfo = new ProcessStartInfo
            {
                WorkingDirectory = dirTarget.FullName,
                FileName = Path.Combine(dirTarget.FullName, "updater.exe"),
                Arguments = "",
                CreateNoWindow = false,
                UseShellExecute = false,
                RedirectStandardError = false,
                RedirectStandardOutput = false,
                RedirectStandardInput = false
            };
            Process.Start(processInfo); // Start new version of 'updater.exe'
            Process.GetCurrentProcess().Kill(); // Stop 'updater2.exe'
        }

        private static void InitLogger()
        {
            var formatter = new CompactJsonFormatter();

            string logPath = Path.Combine(Directory.GetCurrentDirectory(), $"Logs{Path.DirectorySeparatorChar}");

            var logger = new LoggerConfiguration()
                .WriteTo.Console()
                .Enrich.WithProperty("Version", FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion)
                .Enrich.WithProperty("ProgramName", "NeoUpdater")
                .WriteTo.File(path: logPath, formatter: formatter, rollingInterval: RollingInterval.Hour);
            logger = logger.WriteTo.Seq("http://127.0.0.1:5341");
            Log.Logger = logger.CreateLogger();
        }

        private static void CopyFilesRecursively(DirectoryInfo source, DirectoryInfo target)
        {
            foreach (var dir in source.GetDirectories())
                if (dir.Name != "Logs") CopyFilesRecursively(dir, target.CreateSubdirectory(dir.Name));
            foreach (var file in source.GetFiles())
                file.CopyTo(Path.Combine(target.FullName, file.Name));
        }

    }
}

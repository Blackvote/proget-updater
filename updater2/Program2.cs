using Serilog;
using Serilog.Formatting.Compact;
using System;
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
            _log.Information("Start application 'updater2', version: {ver}", FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion);
            Thread.Sleep(3000);

            var dir = Directory.GetCurrentDirectory();
_log.Information("Current dir = '{dir}'", dir);
            var dirSource = new DirectoryInfo(dir);
            var curDirName = dirSource.Name;
_log.Information("Current dir = '{curDirName}'", curDirName);
            var dirTarget = new DirectoryInfo(dir).Parent;
_log.Information("Current dirTarget.FullName = '{dirTarget}'", dirTarget.FullName);

            try
            {
                // Remove old version in ../ -Exclude Logs,{packages[0].LatestVersion}
                _log.Information("Remove old files in dir '{dirTarget}'", dirTarget.FullName); // FIXME ! BUG WAS HERE ! Remove all files & dirs in 'Z:\' !
/*
                foreach (var delDir in dirTarget.GetDirectories())
                    if (delDir.Name != "Logs" || delDir.Name != curDirName) delDir.Delete(true);
                foreach (var file in dirTarget.GetFiles())
                    file.Delete();
*/
                Thread.Sleep(1000);

                // Copy all files and directories from 'sourceDirectory' (./*.*) to 'targetDirectory' (../).
                _log.Information("Copy files from dir '{dirSource}' into dir '{dirTarget}'", dirSource.FullName, dirTarget.FullName);
//                CopyFilesRecursively(dirSource, dirTarget);
                Thread.Sleep(3000);
            }
            catch (Exception e)
            {
                _log.Fatal("Something went wrong: {error}", e.Message);
            }

            _log.Information("Restart application!");
            Process.Start(Path.Combine(dirTarget.FullName, "updater.exe"));
            Process.GetCurrentProcess().Kill(); // Stop updater2.exe
        }

        private static void InitLogger()
        {
            var formatter = new CompactJsonFormatter();

            string logPath = $"{Directory.GetCurrentDirectory()}/Logs/";

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

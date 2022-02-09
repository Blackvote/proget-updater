using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace updater
{
    class Program
    {
        private static ILogger _log;
        internal static ConfigReader ConfigReader { get; set; }

        internal static string TargetPlatform { get; set; }
        internal static string ExeFileName { get; set; }
        internal static bool IsLinux { get; set; }

        static async Task Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            InitLogger();
            _log = Log.Logger;
            _log.Information("Start application '{app}', version: {ver}", "updater", FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion);
            _log.Information("Process ID = {Id}", Environment.ProcessId);

            // See https://docs.microsoft.com/en-us/dotnet/api/system.platformid?view=netcore-3.1
            // and https://docs.microsoft.com/en-us/dotnet/api/system.operatingsystem.islinux?view=net-5.0
            // ! System.OperatingSystem.IsLinux - available only from .NET 5.0
            var platformID = Environment.OSVersion.Platform;
            switch (platformID)
            {
                case PlatformID.Win32NT:
                case PlatformID.Win32S:
                case PlatformID.Win32Windows:
                case PlatformID.WinCE:
                    IsLinux = false;
                    TargetPlatform = @"win-x64";
                    ExeFileName = @"updater.exe";
                    break;
                case PlatformID.Unix:
                    IsLinux = true;
                    TargetPlatform = @"linux-x64";
                    ExeFileName = @"updater";
                    break;
                default:
                    // MacOSX (6) - The operating system is Macintosh. This value was returned by Silverlight. On .NET Core, its replacement is Unix.
                    // Xbox (5) - The development platform is Xbox 360. This value is no longer in use.
                    _log.Fatal("Unknown platformID: {platformID}", platformID);
                    Environment.Exit(5);
                    break;
            }
            _log.Information("Run on platform: {targetPlatform}", TargetPlatform);

            _log.Debug("args.Length = {0}", args.Length);
            for (int i = 0; i < args.Length; i++) _log.Debug("args[{0}] = [{1}]", i, args[i]);
            if (args.Length > 0) 
            {
                if (args[0] == "--replace-restart")
                {
                    _log.Information("Found cmd-line option: '--replace-restart'");
                    await ReplaceRestart();
                }
                else
                {
                    _log.Fatal("Unknown option: '{option}'!", args[0]);
                    Environment.Exit(1);
                }
            }

            try
            {
                ConfigReader = new ConfigReader();
                await ConfigReader.ReadConfigAsync();
            }
            catch(Exception e) {
                _log.Error("Error reading configuration: {error}", e.Message);
                Console.ReadLine();
            }
            SelfUpdate selfUpdate = new SelfUpdate();

            Sync sync = new Sync();
            while (true) {
                try
                {
                    sync.CleanUpDirs();
                    await selfUpdate.IsUpdateNeeded();
                    await sync.CheckTask();

                    _log.Information("Waiting 60 second");
                    await Task.Delay(TimeSpan.FromSeconds(60));
                }
                catch (Exception e)
                {
                    _log.Fatal("Something went wrong: {error}", e.Message);
                }
            }
        }
        public static async Task ReplaceRestart()
        {
            var productVersion = FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion;
            Thread.Sleep(3000);

            var exeDir = new DirectoryInfo(Path.TrimEndingDirectorySeparator(AppDomain.CurrentDomain.BaseDirectory));
            var exeDirFullName = exeDir.FullName;
            _log.Debug("exeDirFullName = '{exeDirFullName}'", exeDirFullName);

            var curDir = new DirectoryInfo(Directory.GetCurrentDirectory());
            var curDirFullName = curDir.FullName;
            _log.Debug("curDirFullName = '{curDirFullName}'", curDirFullName);

            _log.Debug("? '{curDirFullName}' == '{exeDirParentFullName}'", curDir.FullName, exeDir.Parent.Parent.FullName);
            if (curDir.FullName == exeDir.Parent.Parent.FullName)
            { }
            else
            {
                _log.Fatal("Sanity check #1 failed! Current working directory '{curDirFullName}' must be grand-parent for execution directory '{exeDirFullName}'", curDir.FullName, exeDir.FullName);
                throw new InvalidOperationException($"Sanity check #1 failed! Current working directory '{curDir.FullName}' must be parent for execution directory '{exeDir.FullName}'");
            }
            _log.Debug("? '{exeDirName}' == '{productVersion}'", exeDir.Parent.Name, productVersion);
            if (exeDir.Parent.Name == productVersion)
            { }
            else
            {
                _log.Fatal("Sanity check #2 failed! Execution directory name '{exeDirName}' must be equal assembly version {productVersion}", exeDir.Parent.Name, productVersion);
                throw new InvalidOperationException($"Sanity check #2 failed! Execution directory name '{exeDir.Parent.Name}' must be equal assembly version {productVersion}");
            }
            var dirSource = exeDir; // {any}/updater/{version}/{platform}/
            var dirTarget = curDir; // {any}/updater/

            try
            {
                _log.Information("Remove old files in dir '{dirTargetFullName}'", dirTarget.FullName);
                var excludeDirsList = new List<string>() {
                    productVersion,
                    @"Logs"
                };

                foreach (var delDir in dirTarget.GetDirectories())
                    if (excludeDirsList.Contains(delDir.Name))
                    {
                        _log.Debug("SKIP dir '{dirName}'", delDir.Name);
                    }
                    else
                    {
                        _log.Debug("Delete dir '{dirName}'", delDir.Name);
                        delDir.Delete(true);
                    };

                foreach (var file in dirTarget.GetFiles())
                    file.Delete();

                _log.Verbose("Delay for 1 second.");
                await Task.Delay(TimeSpan.FromSeconds(1));

                _log.Information("Copy files from dir '{dirSourceFullName}' into dir '{dirTargetFullName}'", dirSource.FullName, dirTarget.FullName);
                CopyFilesRecursively(dirSource, dirTarget);

                _log.Verbose("Delay for 3 seconds");
                await Task.Delay(TimeSpan.FromSeconds(3));
            }
            catch (Exception e)
            {
                _log.Fatal("Something went wrong: {error}", e.Message);
            }

            _log.Information("Restart application!");
            var processInfo = new ProcessStartInfo
            {
                WorkingDirectory = dirTarget.FullName,
                FileName = Path.Combine(dirTarget.FullName, ExeFileName),
                Arguments = "",
                CreateNoWindow = true,
                UseShellExecute = false,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                RedirectStandardInput = true,
                WindowStyle = ProcessWindowStyle.Hidden
            };
            int exitCode;
            var newProcess = Process.Start(processInfo); // Restart as new version of './updater'
            if (newProcess is null)
            {
                _log.Error("Process for 'updater' is not started!");
                exitCode = 2;
            }
            else
            {
                _log.Information("Process for 'updater' was started sucessfull. New process ID = {Id}", newProcess.Id);
                exitCode = 10;
            }
            _log.Information("Finish the process ID = {Id}. ExitCode = {ExitCode}", Environment.ProcessId, exitCode);
            Environment.Exit(exitCode); // Stop updater2.exe
        }

        public static void InitLogger()
        {
            var formatter = new CompactJsonFormatter();

            string logPath = Path.Combine(Directory.GetCurrentDirectory(), $"Logs{Path.DirectorySeparatorChar}");

            var logger = new LoggerConfiguration()
                .WriteTo.Console()
                .Enrich.WithProperty("Version", FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion)
                .Enrich.WithProperty("ProgramName", "NeoUpdater")
                .WriteTo.File(path: logPath, formatter: formatter, rollingInterval: RollingInterval.Hour);
            logger = logger.WriteTo.Seq("http://127.0.0.1:5341", restrictedToMinimumLevel: LogEventLevel.Debug);
            Log.Logger = logger.CreateLogger();
        }

        private static void CopyFilesRecursively(DirectoryInfo source, DirectoryInfo target)
        {
            foreach (var dir in source.GetDirectories())
                if (dir.Name != "Logs") CopyFilesRecursively(dir, target.CreateSubdirectory(dir.Name));
            foreach (var file in source.GetFiles())
                file.CopyTo(Path.Combine(target.FullName, file.Name), true);
        }

    }
}

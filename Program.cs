using Serilog;
using Serilog.Formatting.Compact;
using System;
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

        static void Main()
        {
            Console.OutputEncoding = Encoding.UTF8;
            InitLogger();
            _log = Log.Logger;
            _log.Information("Start application '{app}', version: {ver}", "updater", FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion);

            try
            {
                ConfigReader = new ConfigReader();
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
                    Task.Run(async () => { await selfUpdate.IsUpdateNeeded(); }).GetAwaiter().GetResult();
                    Task.Run(async () => { await sync.CheckTask();}).GetAwaiter().GetResult();
                    _log.Information("Waiting 60 second");
                    Thread.Sleep(60000);
                }
                catch (Exception e)
                {
                    _log.Fatal("Something went wrong: {error}", e.Message);
                }
            }
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
            logger = logger.WriteTo.Seq("http://127.0.0.1:5341");
            Log.Logger = logger.CreateLogger();
        }

    }
}

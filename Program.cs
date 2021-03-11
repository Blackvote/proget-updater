using Serilog;
using System;
using System.IO;
using System.Threading.Tasks;
using Serilog.Core;
using Logger = Serilog.Core.Logger;
using System.Threading;
using Serilog.Formatting.Compact;
using System.Diagnostics;
using System.Reflection;
using System.Text;

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
            _log.Information("Start application, version: {ver}", FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion);
            if (!Directory.Exists(@"C:\temp"))
                Directory.CreateDirectory(@"C:\temp");

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

            string logPath = $"{Directory.GetCurrentDirectory()}/Logs/";

            var logger = new LoggerConfiguration()
                .WriteTo.Console()
                .Enrich.WithProperty("Version",
                    FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion)
                .Enrich.WithProperty("ProgramName", "NeoUpdater")
                .WriteTo.File(path: logPath,
                    formatter: formatter, rollingInterval: RollingInterval.Hour);
            logger = logger.WriteTo.Seq("http://127.0.0.1:5341");
            Log.Logger = logger.CreateLogger();
        }

    }
}

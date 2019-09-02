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

namespace updater
{
    class Program
    {

        public static ProgramConfig programConfig;
        public static ILogger log;

        static void Main(string[] args)
        {

            InitLogger();

            log.Information("Старт приложения, версия: {ver}", FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion);
            if (!Directory.Exists(@"C:\temp"))
                Directory.CreateDirectory(@"C:\temp");

            programConfig = new ProgramConfig();

            ConfigReader configReader = new ConfigReader(programConfig, log);

            SelfUpdate selfUpdate = new SelfUpdate(programConfig, log);

            
            Sync sync = new Sync(programConfig, log);

            //sync.check();
            while (true) {
                Task.Run(async () => { await selfUpdate.IsUpdateNeeded(); }).GetAwaiter().GetResult();
                Task.Run(async () => { await sync.check(); }).GetAwaiter().GetResult();
                log.Information("Жду 60 секунд до следующей проверки");
                Thread.Sleep(60000);
            }
        }



        public static void InitLogger()
        {
            var formatter = new CompactJsonFormatter();

            string LogPath = $"{Directory.GetCurrentDirectory()}/Logs/";

            log = new LoggerConfiguration()
            .WriteTo.Console()
            .Enrich.WithProperty("Version", FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion)
            .WriteTo.File(path: LogPath,
                formatter: formatter, rollingInterval: RollingInterval.Hour)
            .CreateLogger();
        }

    }
}

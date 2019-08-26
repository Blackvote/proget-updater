using Serilog;
using System;
using System.IO;
using System.Threading.Tasks;
using Serilog.Core;
using Logger = Serilog.Core.Logger;
using System.Threading;

namespace updater
{
    class Program
    {

        public static ProgramConfig programConfig;
        public static ILogger log;

        static void Main(string[] args)
        {

            InitLogger();

            if (!Directory.Exists(@"C:\temp"))
                Directory.CreateDirectory(@"C:\temp");

            programConfig = new ProgramConfig();

            ConfigReader configReader = new ConfigReader(programConfig, log);

            Sync sync = new Sync(programConfig, log);

            //sync.check();
            while (true) { 
                Task.Run(async () => { await sync.check(); }).GetAwaiter().GetResult();
                log.Information("Жду 60 секунд до следующей проверки");
                Thread.Sleep(60000);
            }
        }



        public static void InitLogger()
        {
            log = new LoggerConfiguration()
            .WriteTo.Console()
            .CreateLogger();
        }

    }
}

using Inedo.UPack.Net;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security;
using System.Text;
using System.Threading.Tasks;
using Serilog.Core;
using Logger = Serilog.Core.Logger;
using Serilog;
using System.IO;
using Inedo.UPack;
using Inedo.UPack.Packaging;
using Microsoft.Win32.TaskScheduler;
using System.Threading;

namespace updater
{
    public class SelfUpdate
    {
        public ProgramConfig _config;
        public ILogger _log;


        public SelfUpdate(ProgramConfig config, ILogger log)
        {
            _config = config;
            _log = log;
                       
        }


        public async System.Threading.Tasks.Task IsUpdateNeeded()
        {

            SecureString apiKey = new NetworkCredential("", _config.SourceProGetApiKey).SecurePassword;

            var endpoint = new UniversalFeedEndpoint(new Uri($"{_config.SourceProGetUrl}/upack/Updater"), "api", apiKey);

            var feed = new UniversalFeedClient(endpoint);

            var packages = await feed.ListPackagesAsync("", null);

            if ((packages[0].LatestVersion).ToString() != FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion)
            {
                _log.Information("Нашел новую версию: {newVersion}, скачиваю и обновляюсь", packages[0].LatestVersion);

                try
                {
                    using (var packageStream = await feed.GetPackageStreamAsync(packages[0].FullName, packages[0].LatestVersion))
                    using (var fileStream = File.Create($"{Directory.GetCurrentDirectory()}/{packages[0].LatestVersion}.upack"))
                    {
                        await packageStream.CopyToAsync(fileStream);
                    }
                }
                catch (Exception e)
                {
                    _log.Error("Свалился с ошибкой: {reason}", e.Message);
                }

                _log.Information("Успешно скачал пакет версии {ver}, устанавливаю", packages[0].LatestVersion);
                try
                {
                    using (var package = new UniversalPackage($"{packages[0].LatestVersion}.upack"))
                    {
                        await package.ExtractContentItemsAsync($"{Directory.GetCurrentDirectory()}/{packages[0].LatestVersion}");
                    }
                }
                catch (Exception e)
                {
                    _log.Information("Не удалось распаковать архив по причине: {reason}", e.Message);
                }
                _log.Information("Успешно распаковал версию {ver}, обновляюсь", packages[0].LatestVersion);

                _log.Information("Создаю файл для self-update(update.bat)");
                try
                {

                    string BatTxt = $"taskkill /im updater.exe\r\n" +
                        $"cd {Directory.GetCurrentDirectory()}\r\n" +
                        $"sleep 10\r\n" +
                        $"powershell -Command Remove-Item ./*.* -Exclude config.json,update.bat,{packages[0].LatestVersion} \r\n" +
                        $"sleep 5\r\n" +
                        $"powershell -Command Copy-Item {packages[0].LatestVersion}\\*.* .\\ -Exclude config.json\r\n" +
                        $"sleep 10\r\n" +
                        $"start updater.exe";
                    using (StreamWriter sw = new StreamWriter("update.bat"))
                    {
                        sw.Write(BatTxt);
                    }

                    _log.Information("Успешно создал файл update.bat");
                }
                catch (Exception e)
                {
                    _log.Error("Не получилось записать файл для self-update, по причиние: {reason}", e.Message);
                }

                _log.Information("Создаю задание в планировщике задач");
                try
                {
                    using (TaskService ts = new TaskService())
                    {
                        // Create a new task definition and assign properties
                        TaskDefinition td = ts.NewTask();
                        td.RegistrationInfo.Description = "Self-update for updater";

                        // Create a trigger that will fire the task at this time every other day
                        td.Triggers.Add(new TimeTrigger(DateTime.Now + TimeSpan.FromMinutes(1)));

                        // Create an action that will launch Notepad whenever the trigger fires
                        td.Actions.Add(new ExecAction(@"start /D F:\publish\ update.bat", null));

                        // Register the task in the root folder
                        ts.RootFolder.RegisterTaskDefinition(@"Self-update", td);
                    }
                }
                catch (Exception e)
                {
                    _log.Error("Не получилось создать задание в планировщике задач, по причине: {reason}", e.Message);
                }

                _log.Information("Ожидаю выполнения schedule-task для обновления");
                Thread.Sleep(180000);
            }
            else
            {
                _log.Information("Установлена последняя версия");
            }

        }

    }
}

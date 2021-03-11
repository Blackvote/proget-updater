using Inedo.UPack.Net;
using System;
using System.Diagnostics;
using System.Net;
using System.Reflection;
using System.Security;
using Serilog;
using System.IO;
using Inedo.UPack.Packaging;
using Microsoft.Win32.TaskScheduler;
using System.Threading;

namespace updater
{
    public class SelfUpdate
    {
        public ProgramConfig _config;
        public ILogger _log;


        public SelfUpdate()
        {
            _config = ProgramConfig.Instance;
            _log = Log.Logger.ForContext("ClassType", GetType());
        }

        public async System.Threading.Tasks.Task IsUpdateNeeded()
        {

            SecureString apiKey = new NetworkCredential("", _config.ProGetConfigs[0].SourceProGetApiKey).SecurePassword;

            var endpoint = new UniversalFeedEndpoint(new Uri($"{_config.ProGetConfigs[0].SourceProGetUrl}/upack/Updater"), "api", apiKey);

            var feed = new UniversalFeedClient(endpoint);

            var packages = await feed.ListPackagesAsync("", null);

            _log.Information("Current version: {currentVersion}", FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion);
            _log.Information("Latest version in repository: {latestVersion}", packages[0].LatestVersion);
            Version currentVersion = new Version(FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion);
            Version latestVersion = new Version(packages[0].LatestVersion.ToString());

            if (currentVersion.CompareTo(latestVersion) < 0) // currentVersion is less than latestVersion. See https://docs.microsoft.com/en-us/dotnet/api/system.version.compareto?view=netcore-3.1
            {
                _log.Information("Found new version: {newVersion}, download and update", packages[0].LatestVersion);

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
                    _log.Error("Got error while save new version: {reason}", e.Message);
                }

                _log.Information("Successfully download {ver}, installing", packages[0].LatestVersion);
                try
                {
                    using (var package = new UniversalPackage($"{packages[0].LatestVersion}.upack"))
                    {
                        await package.ExtractContentItemsAsync($"{Directory.GetCurrentDirectory()}/{packages[0].LatestVersion}");
                    }
                }
                catch (Exception e)
                {
                    _log.Information("Unable to unzip the archive due to: {reason}", e.Message);
                }
                _log.Information("Successfully unzip archive {ver}, updating", packages[0].LatestVersion);

                _log.Information("Create file to self-update(update.bat)");
                try
                {

                    string BatTxt = $"taskkill /im updater.exe\r\n" +
                        $"cd {Directory.GetCurrentDirectory()}\r\n" +
                        $"sleep 10\r\n" +
                        $"powershell -Command Remove-Item ./{FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion} -Recurse \r\n" +
                        $"powershell -Command Remove-Item ./*.* -Exclude config.json,update.bat,{packages[0].LatestVersion} \r\n" +
                        $"sleep 5\r\n" +
                        $"powershell -Command Copy-Item {packages[0].LatestVersion}\\*.* .\\ -Exclude config.json\r\n" +
                        $"sleep 10\r\n" +
                        $"start updater.exe";
                    using (StreamWriter sw = new StreamWriter("update.bat"))
                    {
                        sw.Write(BatTxt);
                    }

                    _log.Information("Successfully create file update.bat");
                }
                catch (Exception e)
                {
                    _log.Error("Error writing file for selfupdate, reason: {reason}", e.Message);
                }

                _log.Information("Create task in Schedule(Task Manager)");
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
                        td.Actions.Add(new ExecAction($@"start /D {Directory.GetCurrentDirectory()}\ update.bat", null));

                        // Register the task in the root folder
                        ts.RootFolder.RegisterTaskDefinition(@"Self-update", td);
                    }
                }
                catch (Exception e)
                {
                    _log.Error("Error create task in Schedule, reason: {reason}", e.Message);
                }

                _log.Information("Wait for schedule-task for update");
                Thread.Sleep(180000);
            }
            else
            {
                _log.Information("Latest version is already installed");
            }

        }

    }
}

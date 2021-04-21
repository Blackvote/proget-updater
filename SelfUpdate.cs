using Inedo.UPack.Net;
using Inedo.UPack.Packaging;
using Serilog;
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security;
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
                var dir = Directory.GetCurrentDirectory();

                try
                {
                    using (var packageStream = await feed.GetPackageStreamAsync(packages[0].FullName, packages[0].LatestVersion))
                    using (var fileStream = File.Create($"{dir}/{packages[0].LatestVersion}.upack"))
                    {
                        await packageStream.CopyToAsync(fileStream);
                    }
                }
                catch (Exception e)
                {
                    _log.Error("Got error while save new version: {reason}", e.Message);
                }

                _log.Information("Successfully download {ver}, installing", packages[0].LatestVersion);
                var newdir = $"{dir}/{packages[0].LatestVersion}";
                try
                {
                    using (var package = new UniversalPackage($"{packages[0].LatestVersion}.upack"))
                    {
                        await package.ExtractContentItemsAsync(newdir);
                    }
                    _log.Information("Successfully unzip archive {ver}, updating", packages[0].LatestVersion);
                }
                catch (Exception e)
                {
                    _log.Error("Unable to unzip the archive due to: {reason}", e.Message);
                }

                _log.Information("Copy 'config.json' into dir '{newdir}/'", newdir);
                try
                {
                    File.Copy($"{dir}/config.json", $"{newdir}/config.json");
                    _log.Information($"Successfully copied file 'config.json' ");
                }
                catch (Exception e)
                {
                    _log.Error("Unable to copy config.json due to: {reason}", e.Message);
                }

                var olddir = $"{dir}/{FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion}";
                if (Directory.Exists(olddir))
                {
                    _log.Verbose($"Cleanup: remove old directory '{olddir}'");
                    try
                    {
                        Directory.Delete(olddir, true);
                    }
                    catch (Exception e)
                    {
                        _log.Warning(e, $"Can not delete directory '{olddir}'!");
                    }
                }
                Thread.Sleep(1000);

                _log.Information("Start application updater2.exe ...");
                Process.Start($"{newdir}/updater2.exe");
                Process.GetCurrentProcess().Kill(); // Stop updater.exe for replace files
            }
            else
            {
                _log.Information("Latest version is already installed");
            }

        }

    }
}

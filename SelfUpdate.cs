using Inedo.UPack.Net;
using Inedo.UPack.Packaging;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
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
            var apiKey = new NetworkCredential("", _config.ProGetConfigs[0].SourceProGetApiKey).SecurePassword;

            var endpoint = new UniversalFeedEndpoint(new Uri($"{_config.ProGetConfigs[0].SourceProGetUrl}/upack/Updater"), "api", apiKey);

            var feed = new UniversalFeedClient(endpoint);

            IReadOnlyList<RemoteUniversalPackage> packages;
            try
            {
                packages = await feed.ListPackagesAsync("", null);
            }
            catch(Exception ex)
            {
                _log.Error("Feed upack/Updater error access. {exception}", ex);
                return;
            }

            if (packages.Count == 0)
            {
                _log.Information("There aren't Updater's packages.");
                return;
            }

            var currentVersion = new Version(FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion);
            var latestVersion = new Version(packages[0].LatestVersion.ToString());
            _log.Information("Current version: {currentVersion}", currentVersion.ToString());
            _log.Information("Latest version in repository: {latestVersion}", latestVersion.ToString());

            var dir = Directory.GetCurrentDirectory();
            var olddir = Path.Combine(dir, FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion);
            if (currentVersion.CompareTo(latestVersion) < 0) // currentVersion is less than latestVersion. See https://docs.microsoft.com/en-us/dotnet/api/system.version.compareto?view=netcore-3.1
            {
                _log.Information("Found new version: {newVersion}, download and update", packages[0].LatestVersion);

                try
                {
                    using var packageStream = await feed.GetPackageStreamAsync(packages[0].FullName, packages[0].LatestVersion);
                    using var fileStream = File.Create(Path.Combine(dir, $"{packages[0].LatestVersion}.upack"));
                    await packageStream.CopyToAsync(fileStream);
                }
                catch (Exception e)
                {
                    _log.Error("Got error while save new version: {reason}", e.Message);
                }

                _log.Information("Successfully download {ver}, installing", packages[0].LatestVersion);
                var newdir = Path.Combine(dir, $"{packages[0].LatestVersion}");
                if (Directory.Exists(newdir))
                {
                    _log.Verbose("Cleanup: remove existing directory '{newdir}'", newdir);
                    try
                    {
                        Directory.Delete(newdir, true);
                    }
                    catch (Exception e)
                    {
                        _log.Warning(e, "Can not delete directory '{newdir}'!", newdir);
                    }
                }
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

                newdir = Path.Combine(newdir, Program.TargetPlatform);
                _log.Information("Copy 'config.json' into dir '{newdir}'", newdir);
                try
                {
                    File.Copy(
                        Path.Combine(dir, "config.json"),
                        Path.Combine(newdir, "config.json"),
                        true
                        );
                    _log.Information("Successfully copied file 'config.json'");
                }
                catch (Exception e)
                {
                    _log.Error("Unable to copy 'config.json' due to: {reason}", e.Message);
                }

                if (Program.IsLinux)
                    Chmod("0750", Path.Combine(newdir, Program.ExeFileName));

                if (Directory.Exists(olddir))
                {
                    _log.Verbose("Cleanup: remove old directory '{olddir}'", olddir);
                    try
                    {
                        Directory.Delete(olddir, true);
                    }
                    catch (Exception e)
                    {
                        _log.Warning(e, "Can not delete directory '{olddir}'!", olddir);
                    }
                }
                Thread.Sleep(1000);

                _log.Information("Start application {app} ...", "updater --replace-restart");
                var processInfo = new ProcessStartInfo
                {
                    WorkingDirectory = dir,
                    FileName = Path.Combine(newdir, Program.ExeFileName),
                    Arguments = "--replace-restart",
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    RedirectStandardInput = true,
                    WindowStyle = ProcessWindowStyle.Hidden
                };
                int exitCode;
                var newProcess = Process.Start(processInfo); // Start new version of './{version}/{targetPlatform}/updater --replace-restart'
                if (newProcess is null)
                {
                    _log.Error("Process for 'updater --replace-restart' is not started!");
                    exitCode = 1;
                }
                else
                {
                    _log.Information("Process for 'updater --replace-restart' was started sucessfull. New process ID = {Id}", newProcess.Id);
                    exitCode = 10;
                }
                _log.Information("Finish the process ID = {Id}. ExitCode = {ExitCode}", Process.GetCurrentProcess().Id, exitCode);
                Environment.Exit(exitCode); // Stop updater
            }
            else
            {
                _log.Information("Latest version is already installed");
                if (Directory.Exists(olddir))
                {
                    _log.Verbose("Cleanup: remove old directory '{olddir}'", olddir);
                    try
                    {
                        Directory.Delete(olddir, true);
                    }
                    catch (Exception e)
                    {
                        _log.Warning(e, "Can not delete directory '{olddir}'!", olddir);
                    }
                }
            }
        }

        private void Chmod(string permission, string fullFileName)
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                    FileName = "chmod", // /usr/bin/chmod
                    Arguments = $" {permission} \"{fullFileName}\""
                }
            };
            process.Start();
            process.WaitForExit();
        }

    }
}

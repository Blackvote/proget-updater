﻿using Inedo.UPack.Net;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using updater.DataModels;

namespace updater
{
    public class Sync
    {
        // https://docs.microsoft.com/en-us/dotnet/api/system.io.path.gettemppath?view=netcore-3.1&tabs=windows
        // Windows:
        //   - The path specified by the TMP environment variable.
        //   - The path specified by the TEMP environment variable.
        //   - The path specified by the USERPROFILE environment variable.
        //   - The Windows directory.
        // Linux
        //   - The path specified by the TMPDIR environment variable.
        //   - If the path is not specified in the TMPDIR environment variable, the default path /tmp/ is used.
        private readonly string TempDir;

        private readonly ProGet _proGet;

        private readonly ProgramConfig _programConfig;
        private readonly ILogger _log;
        public Sync()
        {
            _programConfig = ProgramConfig.Instance;
            _log = Log.Logger.ForContext("ClassType", GetType());

            TempDir = Path.Combine(Path.GetTempPath(), $@"updater{Path.DirectorySeparatorChar}");

            _proGet = new ProGet();
        }

        public async Task CheckTask()
        {
            foreach (var feedConfig in _programConfig.ProGetConfigs)
            {
                _log.Information("Синхронизируем фид {DestinationFeed} прогета {DestinationProGet} с фидом {SourceFeedName} прогета {SourceProGet}", feedConfig.DestProGetFeedName, feedConfig.DestProGetUrl, feedConfig.SourceProGetFeedName, feedConfig.SourceProGetUrl);

                switch (feedConfig.FeedType)
                {
                    case FeedType.Upack:
                        await SyncUniversalFeedsTask(feedConfig);
                        break;
                    case FeedType.Nuget:
                        await SyncNuGetFeedsTask(feedConfig);
                        break;
                    case FeedType.Vsix:
                        await SyncVsixFeedsTask(feedConfig);
                        break;
                }
            }
        }
        
        private async Task SyncUniversalFeedsTask(ProGetConfig proGetConfig)
        {
            _log.Information("Start syncing Upack-feeds");

            SecureString sourceApiKey = new NetworkCredential("", proGetConfig.SourceProGetApiKey).SecurePassword;
            var sourceUri = new Uri(proGetConfig.SourceProGetUrl);
            sourceUri = new Uri(sourceUri, $"Upack/{proGetConfig.SourceProGetFeedName}");
            var sourceEndpoint = new UniversalFeedEndpoint(sourceUri, "api", sourceApiKey);
            var sourceFeed = new UniversalFeedClient(sourceEndpoint);

            SecureString destApiKey = new NetworkCredential("", proGetConfig.DestProGetApiKey).SecurePassword;
            var destUri = new Uri(proGetConfig.DestProGetUrl);
            destUri = new Uri(destUri, $"Upack/{proGetConfig.DestProGetFeedName}");
            var destEndpoint = new UniversalFeedEndpoint(destUri, "api", destApiKey);
            var destFeed = new UniversalFeedClient(destEndpoint);

            var packages = await sourceFeed.ListPackagesAsync("", null);
            var dir = $"{TempDir}";
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = 10
            };
            await Parallel.ForEachAsync(packages, parallelOptions, async (p, token) =>
            {
                _log.Verbose("Target package {Group}/{name}", p.Group, p.Name);
                var search = await destFeed.SearchPackagesAsync(p.Name);
                if (search.All(x => x.FullName != p.FullName))
                {
                    _log.Information("Not found {Group}/{Name} in {dProGetFeed}, copy from {sProGetFeed}", p.Group, p.Name,
                        $"{proGetConfig.DestProGetUrl}feeds/{proGetConfig.DestProGetFeedName}",
                        $"{proGetConfig.SourceProGetUrl}feeds/{proGetConfig.SourceProGetFeedName}");
                    foreach (var ver in p.AllVersions)
                    {
                        var file = Path.Combine(dir, $"{p.Group}_{p.Name}_{ver}.Upack");
                        await using (var packageStream = await sourceFeed.GetPackageStreamAsync(p.FullName, ver))
                        await using (var fileStream = File.Create(file))
                        {
                            await packageStream.CopyToAsync(fileStream);
                        }
                        await using (var fileStream = File.OpenRead(file))
                        {
                            _log.Information("Copying package {Group}/{Name} {Version} to {ProGetUrl}", p.Group, p.Name, ver, proGetConfig.DestProGetUrl);
                            await destFeed.UploadPackageAsync(fileStream);
                        }

                        _log.Information("Start delete {file}, first foreach", file);
                        try
                        {
                            File.Delete(file);
                            _log.Information("File {file} was deleted", file);
                        }
                        catch
                        {
                            _log.Warning("Can not delete file {file}", file);
                        }
                    }
                }

                foreach (var pack in search)
                {
                    if (p.FullName == pack.FullName)
                    {
                        _log.Verbose("Found package {Group}/{Name}, in local ProGet, checking versions", p.Group, p.Name);
                        foreach (var ver in p.AllVersions)
                        {
                            if (!pack.AllVersions.Contains(ver))
                            {
                                var file = Path.Combine(dir, $"{p.Group}_{p.Name}_{ver}.Upack");
                                await using (var packageStream = await sourceFeed.GetPackageStreamAsync(p.FullName, ver))
                                await using (var fileStream = File.Create(file))
                                {
                                    await packageStream.CopyToAsync(fileStream);
                                }
                                await using (var fileStream = File.OpenRead(file))
                                {
                                    _log.Information("Copying package {Group}/{Package} to {ProGetUrl}", p.Group, p.Name, proGetConfig.DestProGetUrl);
                                    await destFeed.UploadPackageAsync(fileStream);
                                }
                                //_log.Information("Start delete {file}, second foreach", file);
                                //try
                                //{
                                //    File.Delete(file);
                                //     _log.Information("File {file} was deleted", file);
                                //}
                                //catch
                                //{
                                //    _log.Warning("Can not delete file {file}", file);
                                //}
                            }
                        }
                        _log.Verbose("Not found new version {Group}/{Name} in {SourceProGetFeed}", p.Group, p.Name,
                            $"{proGetConfig.SourceProGetUrl}feeds/{proGetConfig.SourceProGetFeedName}");
                    }
                }
            });
            _log.Information("Finish syncing Upack-feeds");
        }

        private async Task SyncNuGetFeedsTask(ProGetConfig proGetConfig)
        {
            var sourcePackageList = new Dictionary<string, PackageData>();
            var destPackageList = new Dictionary<string, PackageData>();

            try
            {
                _log.Information("Пытаюсь получить список Nuget-пакетов из прогета источника {SourceProGetFeed}", 
                    $"{proGetConfig.SourceProGetUrl}feeds/{proGetConfig.SourceProGetFeedName}");
                sourcePackageList = await _proGet.GetNugetFeedPackageListAsync(proGetConfig.SourceProGetUrl, proGetConfig.SourceProGetFeedName, proGetConfig.SourceProGetApiKey);
            }
            catch (Exception e)
            {
                _log.Error(e, "Не смог получить список Nuget-пакетов из прогета источника {SourceProGetFeed}", 
                    $"{proGetConfig.SourceProGetUrl}feeds/{proGetConfig.SourceProGetFeedName}");
            }

            try
            {
                _log.Information("Пытаюсь получить список Nuget-пакетов из прогета назначения для сравнения {DestinationProGetFeed}", 
                    $"{proGetConfig.DestProGetUrl}feeds/{proGetConfig.DestProGetFeedName}");
                destPackageList = await _proGet.GetNugetFeedPackageListAsync(proGetConfig.DestProGetUrl, proGetConfig.DestProGetFeedName, proGetConfig.DestProGetApiKey);
            }
            catch (Exception e)
            {
                _log.Error(e, "Не смог получить список Nuget-пакетов из прогета назначения {DestProGetFeed}", 
                    $"{proGetConfig.DestProGetUrl}feeds/{proGetConfig.DestProGetFeedName}");
            }

            _log.Information("Приступаю к сравнению Nuget-фидов");

            var packagesForSync = sourcePackageList.Where(p => !destPackageList.ContainsKey(p.Key)).ToDictionary(k => k.Key, v => v.Value);
            if (packagesForSync.Count == 0)
            {
                _log.Information($"Нет Nuget-пакетов для синхронизации в фидах {proGetConfig.SourceProGetFeedName} и {proGetConfig.DestProGetFeedName}.");
                return;
            }

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = 10
            };

            await Parallel.ForEachAsync(packagesForSync, parallelOptions, async (package, token) =>
            {
                string id = package.Value.Id;
                string version = package.Value.Version;

                _log.Information("Не нашел Nuget-пакет {PackageName} версии {PackageVersion} в {DestProGetFeed}}, выкачиваю и выкладываю.",
                    id, version, $"{proGetConfig.DestProGetUrl}feeds/{proGetConfig.DestProGetFeedName}");
                await _proGet.GetNugetPackageAsync(proGetConfig.SourceProGetUrl, proGetConfig.SourceProGetFeedName, proGetConfig.SourceProGetApiKey, id, version, TempDir);
                await _proGet.PushNugetPackageAsync(proGetConfig.DestProGetUrl, proGetConfig.DestProGetFeedName, proGetConfig.DestProGetApiKey, id, version, TempDir);
            });
            _log.Information($"Закончил сравнение Nuget-фидов {proGetConfig.SourceProGetFeedName} и {proGetConfig.DestProGetFeedName}");
        }

        private async Task SyncVsixFeedsTask(ProGetConfig proGetConfig)
        {
            var sourcePackageList = await _proGet.GetVsixFeedPackageListAsync("источника", proGetConfig.SourceProGetUrl, proGetConfig.SourceProGetFeedName, proGetConfig.SourceProGetApiKey);
            var destPackageList = await _proGet.GetVsixFeedPackageListAsync("назначения", proGetConfig.DestProGetUrl, proGetConfig.DestProGetFeedName, proGetConfig.DestProGetApiKey);
            _log.Information("Приступаю к сравнению Vsix-фидов");

            var parallelOptions = new ParallelOptions
            {
                MaxDegreeOfParallelism = 5
            };
            await Parallel.ForEachAsync(sourcePackageList, parallelOptions, async (package, token) =>
            {
                dynamic packageDynamic = JObject.Parse(package.Value);
                if (!destPackageList.ContainsKey(packageDynamic.Package_Id.ToString() + "_" + packageDynamic.Version.ToString()))
                {
                    _log.Information("Не нашел Vsix-пакет {PackageId} версии {PackageVersion} в {DestProGetFeed}, выкачиваю и выкладываю.",
                        packageDynamic.Package_Id.ToString(), packageDynamic.Version.ToString(),
                        $"{proGetConfig.DestProGetUrl}feeds/{proGetConfig.DestProGetFeedName}");

                    await _proGet.GetVsixPackageAsync(proGetConfig.SourceProGetUrl, proGetConfig.SourceProGetFeedName, proGetConfig.SourceProGetApiKey,
                        packageDynamic.DisplayName_Text.ToString(), packageDynamic.Package_Id.ToString(), packageDynamic.Version.ToString(), TempDir);

                    await _proGet.PushVsixPackageAsync(proGetConfig.DestProGetUrl, proGetConfig.DestProGetFeedName, proGetConfig.DestProGetApiKey,
                        packageDynamic.DisplayName_Text.ToString(), packageDynamic.Package_Id.ToString(), packageDynamic.Version.ToString(), TempDir);
                }
            });
            _log.Information("Закончил сравнение Vsix-фидов");
        }

        public void CleanUpDirs()
        {
            var dirsList = new List<string>() {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"packages"), // old place for temporary Nuget-packages
                @"C:\temp\updater\", // old place, Windows preffered
                $"{TempDir}" // current place for temporary packages (Upack, Nuget, Vsix)
            };
            foreach (var dir in dirsList)
            {
                if (Directory.Exists(dir))
                {
                    _log.Information("Cleanup: remove directory '{dir}'", dir);
                    try
                    {
                        Directory.Delete(dir, true);
                    }
                    catch (Exception e)
                    {
                        _log.Error(e, "Can not delete directory '{dir}'!", dir);
                    }
                }
            }
        }
    }
}

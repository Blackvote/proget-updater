using Inedo.UPack.Net;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RestSharp;
using RestSharp.Authenticators;
using RestSharp.Extensions;

namespace updater
{
    public class Sync
    {
        private const string TempDir = @"C:\temp\";
        private readonly ProgramConfig _programConfig;
        private readonly ILogger _log;
        public Sync()
        {
            _programConfig = ProgramConfig.Instance;
            _log = Log.Logger.ForContext("ClassType", GetType());
        }

        public async Task CheckTask()
        {

            foreach (var feedConfig in _programConfig.ProGetConfigs)
            {
                _log.Information("Синхронизируем фид {DestinationFeed} прогета {DestinationProGet} с фидом {SourceFeedName} прогета {SourceProGet}", feedConfig.DestProGetFeedName, feedConfig.DestProGetUrl, feedConfig.SourceProGetFeedName, feedConfig.SourceProGetUrl);
                var sourceType = GetFeedType(feedConfig.SourceProGetUrl, feedConfig.SourceProGetFeedName, feedConfig.SourceProGetApiKey);
                var destType = GetFeedType(feedConfig.DestProGetUrl, feedConfig.DestProGetFeedName, feedConfig.DestProGetApiKey);
                if(sourceType.ToLower() == destType.ToLower()) { 
                    switch (sourceType.ToLower())
                    {
                        case "universal":
                            await SyncUniversalFeedsTask(feedConfig);
                            break;
                        case "nuget":
                            SyncNuGetFeeds(feedConfig);
                            break;
                    }
                }
                else
                {
                    _log.Error("Фиды имеют разный тип ('{sourceType}' != '{destType}'), синхронизация невозможна!", sourceType.ToLower(), destType.ToLower());
                }
            }
        }
        
        private async Task SyncUniversalFeedsTask(ProGetConfig proGetConfig)
        {
            _log.Information("Start matching versions in local and remote Proget's");
            SecureString sourceApiKey = new NetworkCredential("", proGetConfig.SourceProGetApiKey).SecurePassword;

            var sourceEndpoint = new UniversalFeedEndpoint(new Uri($"{proGetConfig.SourceProGetUrl}/upack/{proGetConfig.SourceProGetFeedName}"), "api", sourceApiKey);

            var sourceFeed = new UniversalFeedClient(sourceEndpoint);

            SecureString destApiKey = new NetworkCredential("", proGetConfig.DestProGetApiKey).SecurePassword;

            var destEndpoint = new UniversalFeedEndpoint(new Uri($"{proGetConfig.DestProGetUrl}/upack/{proGetConfig.DestProGetFeedName}"), "api", destApiKey);

            var destFeed = new UniversalFeedClient(destEndpoint);

            var packages = await sourceFeed.ListPackagesAsync("", null);
            foreach (var p in packages)
            {
                _log.Information("Target package {Group}/{name}", p.Group, p.Name);
                var search = await destFeed.SearchPackagesAsync(p.Name);
                if (!search.Any(x => x.FullName == p.FullName))
                {
                    _log.Information("Not found {Group}/{Name} in {dProGetUrl}feeds/{dFeedName}, copy from {sProGetUrl}feeds/{sFeedName}", p.Group, p.Name, destFeed.Endpoint.Uri, proGetConfig.DestProGetFeedName, proGetConfig.SourceProGetUrl, proGetConfig.SourceProGetFeedName);

                    foreach (var ver in p.AllVersions)
                    {
                        string file = $@"{TempDir}{p.Group}_{p.Name}_{ver}.upack";
                        using (var packageStream = await sourceFeed.GetPackageStreamAsync(p.FullName, ver))
                        using (var fileStream = File.Create(file))
                        {
                            await packageStream.CopyToAsync(fileStream);
                        }
                        using (var fileStream = File.OpenRead(file))
                        {
                            _log.Information("Copying package {Group}/{Name} {Version} to {ProGetUrl}", p.Group, p.Name, ver, proGetConfig.DestProGetUrl);
                            await destFeed.UploadPackageAsync(fileStream);
                        }

                        _log.Information("start delete {file}, first foreach", file);
                        try
                        {
                            File.Delete(file);
                            _log.Information("смогли удалить файл {upack}", file);
                        }
                        catch
                        {
                            _log.Information("не смогли удалить файл {upack}", file);
                        }
                    }
                }

                foreach (var pack in search)
                {
                    if (p.FullName == pack.FullName)
                    {
                        _log.Information("Found package {Group}/{Name}, in local ProGet, cheking versions", p.Group, p.Name);
                        foreach (var ver in p.AllVersions)
                        {
                            if (!pack.AllVersions.Contains(ver))
                            {
                                string file = $@"{TempDir}{p.Group}_{p.Name}_{ver}.upack";
                                using (var packageStream = await sourceFeed.GetPackageStreamAsync(p.FullName, ver))
                                using (var fileStream = File.Create(file))
                                {
                                    await packageStream.CopyToAsync(fileStream);

                                }
                                using (var fileStream = File.OpenRead(file))
                                {
                                    _log.Information("Copying package {Group}/{Package} to {ProGetUrl}", p.Group, p.Name, proGetConfig.DestProGetUrl);
                                    await destFeed.UploadPackageAsync(fileStream);
                                }
                                //_log.Information("start delete {file}, second foreach", file);
                                //try
                                //{
                                //    File.Delete(file);
                                //    _log.Information("смогли удалить файл {upack}", file);
                                //}
                                //catch
                                //{
                                //    _log.Information("не смогли удалить файл {upack}", file);
                                //}
                            }
                        }
                        _log.Information("Not found new version {Group}/{Name} in {ProGetUrl}feeds/{FeedName}", p.Group, p.Name, proGetConfig.SourceProGetUrl, proGetConfig.SourceProGetFeedName);
                    }
                }
            }
        }

        private void SyncNuGetFeeds(ProGetConfig proGetConfig)
        {
            var sourcePackageList = new Dictionary<string, string>();
            var destPackageList = new Dictionary<string, string>();

            try
            {
                _log.Information("Пытаюсь получить список nuget-пакетов из прогета {SourceProGet}feeds/{SourceFeed}", proGetConfig.SourceProGetUrl, proGetConfig.SourceProGetFeedName);
                sourcePackageList = GetNugetFeedPackageList(proGetConfig.SourceProGetUrl, proGetConfig.SourceProGetFeedName, proGetConfig.SourceProGetApiKey);
                _log.Information("Получил список пакетов из {SourceProGet}feeds/{SourceFeed}", proGetConfig.SourceProGetUrl, proGetConfig.SourceProGetFeedName);
            }
            catch (Exception e)
            {
                _log.Error(e, "Не смог получить список nuget-пакетов из прогета источника {SourceProGet}feeds/{Sourcefeed}", proGetConfig.SourceProGetUrl, proGetConfig.SourceProGetFeedName);
            }

            try
            {
                _log.Information(
                    "Пытаюсь получить список nuget-пакетов из прогета назначения для сравнения {DestinationProGet}feeds/{DestinationFeed}",
                    proGetConfig.DestProGetUrl, proGetConfig.DestProGetFeedName);
                destPackageList = GetNugetFeedPackageList(proGetConfig.DestProGetUrl, proGetConfig.DestProGetFeedName, proGetConfig.DestProGetApiKey);
            }
            catch (Exception e)
            {
                _log.Error(e, "Не смог получить список nuget-пакетов из прогета источника {DestinationProGet}feeds/{Destinationfeed}", proGetConfig.DestProGetUrl, proGetConfig.DestProGetFeedName);
            }

            _log.Information("Приступаю к сравнению nuget-фидов");
            foreach (var package in sourcePackageList)
            {
                dynamic packageDynamic = JObject.Parse(package.Value);
                if (!destPackageList.ContainsKey(packageDynamic.Id.ToString() + "_" + packageDynamic.Version.ToString()))
                {
                    _log.Information("Не нашел nuget-пакет {PackageName} версии {PackageVersion} в {DestinationProGet}feeds/{DestinationFeed}, выкачиваю и выкладываю.", packageDynamic.Id.ToString(), packageDynamic.Version.ToString(), proGetConfig.DestProGetUrl, proGetConfig.DestProGetFeedName);
                    GetNugetPackage(proGetConfig.SourceProGetUrl, proGetConfig.SourceProGetFeedName, proGetConfig.SourceProGetApiKey, 
                        packageDynamic.Id.ToString(), packageDynamic.Version.ToString());
                    PushNugetPackage(proGetConfig.DestProGetUrl, proGetConfig.DestProGetFeedName, proGetConfig.DestProGetApiKey, 
                        packageDynamic.Id.ToString(), packageDynamic.Version.ToString());
                }
            }
        }

        private Dictionary<string, string> GetNugetFeedPackageList(string progetUrl, string feedName, string apiKey)
        {
            Dictionary<string,string> packageList = new Dictionary<string, string>();
            var client = new RestClient(progetUrl);
            // ODATA (v2), used: https://proget.netsrv.it:38443/nuget/seqplug/Packages?$format=json
            // JSON-LD (v3) API, disabled for feed by-default: https://proget.netsrv.it:38443/nuget/seqplug/v3/index.json
            var request = new RestRequest($"nuget/{feedName}/Packages?$format=json", Method.GET);
            client.Authenticator = new HttpBasicAuthenticator("api", apiKey);
            var response = client.Execute(request);
            _log.Verbose("response.Content = '{0}'", response.Content);
            dynamic resp = JObject.Parse(response.Content);
            foreach (var package in resp.d.results)
            {
                _log.Information("Нашел nuget-пакет {PackageName} версии {PackageVersion} в {ProGetUrl}feeds/{ProGetFeed}", package.Id.ToString(), package.Version.ToString(), progetUrl, feedName);
                var packageName = package.Id.ToString() + "_" + package.Version.ToString();
                packageList.Add(packageName, package.ToString());
            }
            return packageList;
        }

        private void GetNugetPackage(string progetUrl, string feedName, string apiKey, string packageName, string packageVersion)
        {
            // http://proget-server/api/v2/package/{feedName}/{packageName}/{optional-version}
            // https://proget.netsrv.it:38443/nuget/seqplug/package/Seq.App.Exporter/1.2.3
            var client = new RestClient(progetUrl);
            var request = new RestRequest($"nuget/{feedName}/package/{packageName}/{packageVersion}", Method.GET);
            client.Authenticator = new HttpBasicAuthenticator("api", apiKey);
            try
            {
                string dir = AppDomain.CurrentDomain.BaseDirectory + "packages/"; // TODO: use constant TempDir
                // FIXME string dir = $"{TempDir}nuget-packages/";
                string fileName = $"{packageName}_{packageVersion}.nupkg";
                if (!Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                var tempFile = Path.GetFullPath(dir + fileName);
                var writer = File.OpenWrite(tempFile);
                request.ResponseWriter = responseStream =>
                {
                    using (responseStream)
                    {
                        responseStream.CopyTo(writer);
                    }
                };
                var response = client.DownloadData(request);
            }
            catch (Exception e)
            {
                _log.Error(e, "Не получилось скачать nuget-пакет {PackageName}/{PackageVersion}", packageName, packageVersion);
            }
        }

        private void PushNugetPackage(string progetUrl, string feedName, string apiKey, string packageName, string packageVersion)
        {
            try
            {
                var stringToFeed = progetUrl + $"nuget/{feedName}";
                // TODO: use constant TempDir
                var stringToPackage = AppDomain.CurrentDomain.BaseDirectory + "packages/" + packageName + "_" +
                                      packageVersion + ".nupkg";
                ExecuteDotnetCommand(stringToPackage, stringToFeed, apiKey);
            }
            catch (Exception e)
            {
                _log.Error(e, "Не получилось загрузить nuget-пакет {PackageName}/{PackageVersion} в {ProGetUrl}feeds/{ProGetFeed}", packageName, packageVersion, progetUrl, feedName);
            }
        }

        private void ExecuteDotnetCommand(string packagePath, string progetUrl, string apiKey)
        {
            ProcessStartInfo psiutil = new ProcessStartInfo
            {
                FileName = "dotnet.exe",
                Arguments = $"nuget push {packagePath} -k {apiKey} -s {progetUrl}"
            };
            var pUtil = new Process
            {
                StartInfo = psiutil
            };
            _ = pUtil.Start();


        }

        private string GetFeedType(string progetUrl, string feedName, string apiKey)
        {
            var client = new RestClient(progetUrl);
            // Feed Management API: https://proget.netsrv.it:38443/api/management/feeds/get/Neo
            // Native API: https://proget.netsrv.it:38443/api/json/Feeds_GetFeed?Feed_Name=Neo
            // FeedType_Name
            var request = new RestRequest($"api/management/feeds/get/{feedName}", Method.GET); // TODO Replace RestClient+RestRequest to HttpClient+PostAsync
            request.AddHeader("X-ApiKey", apiKey);
            var response = client.Execute(request);
            _log.Verbose("response.Content = '{0}'", response.Content);
            try
            {
                dynamic resp = JObject.Parse(response.Content);
                var feedType = resp.feedType.ToString();
                _log.Information($"Определили тип фида {progetUrl}feeds/{feedName} как {feedType.ToString().ToLower()}");
                return feedType;
            }
            catch (Exception e)
            {
                _log.Error(e, $"Не смогли определить тип фида {progetUrl}feeds/{feedName}! Возможно из-за отсутствия привелегии \"Feed Management API\" у API-Key");
                return default;
            }

        }

    }
}
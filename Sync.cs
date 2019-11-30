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
                if(sourceType == destType) { 
                    switch (sourceType)
                    {
                        case "Universal":
                            await SyncUniversalFeedsTask(feedConfig);
                            break;
                        case "NuGet":
                            SyncNuGetFeeds(feedConfig);
                            break;
                    }
                }
                else
                {
                    _log.Error("Фиды имею разный тип, синхронизация невозможна!!");
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
                    _log.Information("Not found {Group}/{Name} in {ProGetUrl}/{FeedName}, copy from {ProGetUrl}/{ProGetUrl}", p.Group, p.Name, destFeed.Endpoint.Uri, proGetConfig.DestProGetFeedName, proGetConfig.SourceProGetUrl, proGetConfig.SourceProGetFeedName);


                    foreach (var ver in p.AllVersions)
                    {
                        using (var packageStream = await sourceFeed.GetPackageStreamAsync(p.FullName, ver))
                        using (var fileStream = File.Create($@"C:\temp\{p.Group}_{p.Name}_{ver}.upack"))
                        {
                            await packageStream.CopyToAsync(fileStream);
                        }
                        using (var fileStream = File.OpenRead($@"C:\temp\{p.Group}_{p.Name}_{ver}.upack"))
                        {
                            _log.Information("Copying package {Group}/{Name} {Version} to {ProGetUrl}", p.Group, p.Name, ver, proGetConfig.DestProGetUrl);
                            await destFeed.UploadPackageAsync(fileStream);
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
                                using (var packageStream = await sourceFeed.GetPackageStreamAsync(p.FullName, ver))
                                using (var fileStream = File.Create($@"C:\temp\{p.Group}_{p.Name}_{ver}.upack"))
                                {
                                    await packageStream.CopyToAsync(fileStream);

                                }
                                using (var fileStream = File.OpenRead($@"C:\temp\{p.Group}_{p.Name}_{ver}.upack"))
                                {
                                    _log.Information("Copying package {Group}/{Package} to {ProGetUrl}", p.Group, p.Name, proGetConfig.DestProGetUrl);
                                    await destFeed.UploadPackageAsync(fileStream);
                                }

                            }
                        }
                        _log.Information("Not found new version {Group}/{Name} in {ProGetUrl}/{FeedName}", p.Group, p.Name, proGetConfig.SourceProGetUrl, proGetConfig.SourceProGetFeedName);

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
                _log.Information("Пытаюсь получить список пакетов из прогета {SourceProGet}/{SourceFeed}", proGetConfig.SourceProGetUrl, proGetConfig.SourceProGetFeedName);
                sourcePackageList = GetNugetFeedPackageList(proGetConfig.SourceProGetUrl, proGetConfig.SourceProGetFeedName, proGetConfig.SourceProGetApiKey);
                _log.Information("Получил список пакетов из {SourceProGet}/{SourceFeed}", proGetConfig.SourceProGetUrl, proGetConfig.SourceProGetFeedName);
            }
            catch (Exception e)
            {
                _log.Error(e, "Не смог получить список пакетов из прогета источника {SourceProGet}/{Sourcefeed}", proGetConfig.SourceProGetUrl, proGetConfig.SourceProGetFeedName);
            }

            try
            {
                _log.Information(
                    "Пытаюсь получить список пакетов из прогета назначения для сравнения {DestinationProGet}/{DestinationFeed}",
                    proGetConfig.DestProGetUrl, proGetConfig.DestProGetFeedName);
                destPackageList = GetNugetFeedPackageList(proGetConfig.DestProGetUrl, proGetConfig.DestProGetFeedName, proGetConfig.DestProGetApiKey);

            }
            catch (Exception e)
            {
                _log.Error(e, "Не смог получить список пакетов из прогета источника {DestinationProGet}/{Destinationfeed}", proGetConfig.DestProGetUrl, proGetConfig.DestProGetFeedName);
            }
            _log.Information("Приступаю к сравнению фидов");
            foreach (var package in sourcePackageList)
            {
                dynamic packageDynamic = JObject.Parse(package.Value);
                if (!destPackageList.ContainsKey(packageDynamic.Id.ToString() + "_" + packageDynamic.Version.ToString()))
                {
                    _log.Information("Не нашел пакет {PackageName} версии {PackageVersion} в {DestinationProGet}/{DestinationFeed}, выкачиваю и выкладываю.", packageDynamic.Id.ToString(), packageDynamic.Version.ToString(), proGetConfig.DestProGetUrl, proGetConfig.DestProGetFeedName);
                    GetNugetPackage(proGetConfig.SourceProGetUrl, proGetConfig.SourceProGetFeedName, proGetConfig.SourceProGetApiKey, packageDynamic.Id.ToString(), packageDynamic.Version.ToString());
                    PushNugetPackage(proGetConfig.DestProGetUrl, proGetConfig.DestProGetFeedName,
                        proGetConfig.DestProGetApiKey, packageDynamic.Id.ToString(),
                        packageDynamic.Version.ToString());

                }
            }



        }
        private Dictionary<string, string> GetNugetFeedPackageList(string progetUrl, string feedName, string apiKey)
        {
            Dictionary<string,string> packageList = new Dictionary<string, string>();
            var client = new RestClient(progetUrl);
            var request = new RestRequest($"nuget/{feedName}/Packages?$format=json", Method.GET);
            client.Authenticator = new HttpBasicAuthenticator("api", apiKey);
            var response = client.Execute(request);
            dynamic resp = JObject.Parse(response.Content);
            foreach (var package in resp.d.results)
            {
                _log.Information("Нашел пакет {PackageName} версии {PackageVersion} в {ProGetUrl}/{ProGetFeed}", package.Id.ToString(), package.Version.ToString(), progetUrl, feedName);
                var packageName = package.Id.ToString() + "_" + package.Version.ToString();
                packageList.Add(packageName, package.ToString());
            }

            return packageList;
        }

        private void GetNugetPackage(string progetUrl, string feedName, string apiKey, string packageName, string packageVersion)
        {
            //http://proget-server/api/v2/package/{feedName}/{packageName}/{optional-version}
            //https://proget.netsrv.it:38443/nuget/seqplug/package/Seq.App.Exporter/1.2.3
            var client = new RestClient(progetUrl);
            var request = new RestRequest($"nuget/{feedName}/package/{packageName}/{packageVersion}", Method.GET);
            client.Authenticator = new HttpBasicAuthenticator("api", apiKey);
            try
            {
                if (!Directory.Exists(AppDomain.CurrentDomain.BaseDirectory + "packages/"))
                    Directory.CreateDirectory(AppDomain.CurrentDomain.BaseDirectory + "packages/");
                client.DownloadData(request).SaveAs(AppDomain.CurrentDomain.BaseDirectory + "packages/" + packageName + "_" + packageVersion + ".nupkg");
            }
            catch (Exception e)
            {
                _log.Error(e, "Не получилось скачать пакет {PackageName}/{PackageVersion}", packageName, packageVersion);
            }
        }

        private void PushNugetPackage(string progetUrl, string feedName, string apiKey, string packageName, string packageVersion)
        {
            try
            {
                var stringToFeed = progetUrl + $"nuget/{feedName}";
                var stringToPackage = AppDomain.CurrentDomain.BaseDirectory + "packages/" + packageName + "_" +
                                      packageVersion + ".nupkg";
                ExecuteDotnetCommand(stringToPackage,stringToFeed, apiKey);
            }
            catch (Exception e)
            {
                _log.Error(e, "Не получилось загрузить пакет {PackageName}/{PackageVersion} в {ProGetUrl}{ProGetFeed}", packageName, packageVersion, progetUrl, feedName);
            }
        }

        private void ExecuteDotnetCommand(string packagePath, string progetUrl, string apiKey)
        {
            ProcessStartInfo psiutil = new ProcessStartInfo();
            psiutil.FileName = "dotnet.exe";

            psiutil.Arguments = $"nuget push {packagePath} -k {apiKey} -s {progetUrl}";

            var pUtil = new Process();
            pUtil.StartInfo = psiutil;
            var result = pUtil.Start();
        }

        private string GetFeedType(string progetUrl, string feedName, string apiKey)
        {
            var client = new RestClient(progetUrl);
            var request = new RestRequest($"api/management/feeds/get/{feedName}", Method.GET);
            request.AddHeader("X-ApiKey", apiKey);
            var response = client.Execute(request);
            try
            {
                dynamic resp = JObject.Parse(response.Content);
                var feedType = resp.feedType.ToString();
                _log.Information("Определили тип фида {ProGetUrl}/{FeedName} как {FeedType}", progetUrl, feedName, feedType);
                return feedType;
            }
            catch (Exception e)
            {
                _log.Error(e, "Не смогли определить тип фида {ProGetUrl}{FeedName} из-за отсутствия привелегии \"Feed Management API\" у API-Key", progetUrl,feedName);
                return default;
            }

        }

    }
}
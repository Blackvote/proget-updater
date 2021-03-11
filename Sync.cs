using Inedo.UPack.Net;
using Serilog;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
using RestSharp;
using RestSharp.Authenticators;

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
                        case "vsix":
                            await SyncVsixFeedsAsync(feedConfig);
                            break;
                        default:
                            _log.Error("Фид имеет неизвестный тип '{sourceType}', синхронизация невозможна!", sourceType.ToLower());
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
            // ODATA (v2), used: https://proget.netsrv.it:38443/nuget/seqplug/Packages?$format=json
            // JSON-LD (v3) API, disabled for feed by-default: https://proget.netsrv.it:38443/nuget/seqplug/v3/index.json
            Dictionary<string,string> packageList = new Dictionary<string, string>();
            var client = new RestClient(progetUrl);
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


        private async Task SyncVsixFeedsAsync(ProGetConfig proGetConfig)
        {
            var sourcePackageList = await GetVsixFeedPackageListAsync(@"источника", proGetConfig.SourceProGetUrl, proGetConfig.SourceProGetFeedName, proGetConfig.SourceProGetApiKey);
            var destPackageList = await GetVsixFeedPackageListAsync(@"назначения", proGetConfig.DestProGetUrl, proGetConfig.DestProGetFeedName, proGetConfig.DestProGetApiKey);
            _log.Information("Приступаю к сравнению vsix-фидов");
            foreach (var package in sourcePackageList)
            {
                dynamic packageDynamic = JObject.Parse(package.Value);
                if (!destPackageList.ContainsKey(packageDynamic.Package_Id.ToString() + "_" + packageDynamic.Version.ToString()))
                {
                    _log.Information("Не нашел vsix-пакет {PackageId} версии {PackageVersion} в {DestinationProGet}feeds/{DestinationFeed}, выкачиваю и выкладываю.",
                        packageDynamic.Package_Id.ToString(), packageDynamic.Version.ToString(), proGetConfig.DestProGetUrl, proGetConfig.DestProGetFeedName);

                    await GetVsixPackageAsync(proGetConfig.SourceProGetUrl, proGetConfig.SourceProGetFeedName, proGetConfig.SourceProGetApiKey,
                        packageDynamic.DisplayName_Text.ToString(), packageDynamic.Package_Id.ToString(), packageDynamic.Version.ToString());

                    await PushVsixPackageAsync(proGetConfig.DestProGetUrl, proGetConfig.DestProGetFeedName, proGetConfig.DestProGetApiKey,
                        packageDynamic.DisplayName_Text.ToString(), packageDynamic.Package_Id.ToString(), packageDynamic.Version.ToString());
                }
            }
        }

        private async Task<Dictionary<string, string>> GetVsixFeedPackageListAsync(string side, string progetUrl, string feedName, string apiKey)
        {
            // Invoke-RestMethod -Method POST -Uri https://proget.netsrv.it:38443/api/json/VsixPackages_GetPackages?Feed_Id=2046 -ContentType "application/json" -Headers @{"X-ApiKey" = "XXXXXXXXX"; "charset" = "utf-8"}
            // Invoke-RestMethod -Method POST -Uri https://proget.netsrv.it:38443/api/json/VsixPackages_GetPackages -ContentType "application/json" -Headers @{"X-ApiKey" = "XXXXXXXXX"; "charset" = "utf-8"} -Body (@{"Feed_Id" = 2046}|ConvertTo-Json)
            // Use 'Native API'. See https://proget.netsrv.it:38443/reference/api and https://docs.inedo.com/docs/proget/reference/api/native
            _log.Information($"Пытаюсь получить список пакетов из прогета {side} {progetUrl}feeds/{feedName}");
            Dictionary<string, string> packageList = new Dictionary<string, string>();
            var feedId = await GetFeedIdAsync(progetUrl, feedName, apiKey);

            var client = new HttpClient
            {
                BaseAddress = new Uri(progetUrl)
            };
            client.DefaultRequestHeaders.Add("X-ApiKey", apiKey);
            dynamic jsonObj = new JObject();
            jsonObj.Feed_Id = feedId;
            _log.Verbose("request.body = '{0}'", jsonObj.ToString());
            var content = new StringContent(jsonObj.ToString(), System.Text.Encoding.UTF8, "application/json");
            try
            {
                var response = await client.PostAsync(@"/api/json/VsixPackages_GetPackages", content); // Native API
                response.EnsureSuccessStatusCode();
                var strBody = await response.Content.ReadAsStringAsync();
                _log.Verbose("response.Content = '{0}'", strBody);
                dynamic resp = JArray.Parse(strBody);
                foreach (var package in resp)
                {
                    // DisplayName_Text, Package_Id
                    // Major_Number, Minor_Number, Build_Number, Revision_Number
                    string version = CombineVersion(package.Major_Number.ToString(), package.Minor_Number.ToString(), package.Build_Number.ToString(), package.Revision_Number.ToString());
                    package.Add("Version", version.ToString());
                    _log.Information("Нашел vsix-пакет {PackageId} версии {PackageVersion} в {ProGetUrl}feeds/{ProGetFeed}", package.Package_Id.ToString(), package.Version.ToString(), progetUrl, feedName);
                    var packageName = package.Package_Id.ToString() + "_" + package.Version.ToString();
                    packageList.Add(packageName, package.ToString());
                }
            }
            catch (Exception e)
            {
                _log.Error(e, $"Не смогли получить список vsix-пакетов из прогета {side} {progetUrl}feeds/{feedName}! Возможно из-за отсутствия привелегии \"Native API\" у API-Key");
                throw;
            }
            _log.Information($"Получил список vsix-пакетов из прогета {side} {progetUrl}feeds/{feedName}");
            return packageList;
        }

        private async Task GetVsixPackageAsync(string progetUrl, string feedName, string apiKey, string packageName, string Package_Id, string packageVersion)
        {
            // http://proget-server/vsix/{feedName}/downloads/{Package_Id}/{packageVersion}
            // https://proget.netsrv.it:38443/vsix/NeoGallery/downloads/MobiTemplateWizard.cae77667-8ddc-4040-acf7-f7491071af30/1.0.1
            string dir = $"{TempDir}{Package_Id}/{packageVersion}/";
            string fileName = $"{packageName}.vsix";
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            var fullFileName = Path.GetFullPath(dir + fileName);
            try
            {
                var client = new HttpClient
                {
                    BaseAddress = new Uri(progetUrl)
                };
                client.DefaultRequestHeaders.Add("X-ApiKey", apiKey);
                using (var response = await client.GetAsync($"vsix/{feedName}/downloads/{Package_Id}/{packageVersion}")) // Feed API
                using (var fs = File.Create(fullFileName))
                {
                    await response.Content.CopyToAsync(fs);
                    response.EnsureSuccessStatusCode();
                }
            }
            catch (Exception e)
            {
                _log.Error(e, "Не получилось скачать vsix-пакет {Package_Id}/{PackageVersion}", Package_Id, packageVersion);
            }
        }

        private async Task PushVsixPackageAsync(string progetUrl, string feedName, string apiKey, string packageName, string Package_Id, string packageVersion)
        {
            // Invoke-RestMethod -Method POST -Uri https://proget.netsrv.it:38443/vsix/NeoGallery -InFile.\MobiTemplateWizard.vsix - Headers @{ "X-ApiKey" = "XXXXXXXXXXXXXX"}
            string dir = $"{TempDir}{Package_Id}/{packageVersion}/";
            string fileName = $"{packageName}.vsix";
            string fullFileName = dir + fileName;
            FileInfo fileInfo = new FileInfo(fullFileName);
            long fileSize = fileInfo.Length;
            _log.Information($"Выкладываю vsix-пакет {Package_Id} версии {packageVersion} в {progetUrl}feed/{feedName}");
            try
            {
                var client = new HttpClient
                {
                    BaseAddress = new Uri(progetUrl)
                };
                client.DefaultRequestHeaders.Add("X-ApiKey", apiKey);
                client.DefaultRequestHeaders.TryAddWithoutValidation("Content-Length", $"{fileSize}");
                using (Stream stream = File.OpenRead(fullFileName))
                using (var content = new StreamContent(stream))
                {
                    var response = await client.PostAsync($"vsix/{feedName}", content); // Feed API?
                    _log.Verbose($"response.StatusCode = '{response.StatusCode}', ReasonPhrase = '{response.ReasonPhrase}'");
                    response.EnsureSuccessStatusCode();
                }
                try
                {
                    File.Delete(fullFileName);
                    _log.Verbose("Удалили временный файл {fullFileName}", fullFileName);
                }
                catch (Exception e)
                {
                    _log.Information(e, "Не получилось удалить временный файл '{fullFileName}'", fullFileName);
                }
                try
                {
                    Directory.Delete(dir);
                    _log.Verbose("Удалили временный каталог {dir}", dir);
                }
                catch (Exception e)
                {
                    _log.Information(e, "Не получилось удалить временный каталог '{dir}'", dir);
                }
            }
            catch (Exception e)
            {
                _log.Error(e, "Не получилось загрузить vsix-пакет {PackageId}/{PackageVersion} в {ProGetUrl}feeds/{ProGetFeed}", Package_Id, packageVersion, progetUrl, feedName);
            }
        }

        private string GetFeedType(string progetUrl, string feedName, string apiKey)
        {
            // Feed Management API: https://proget.netsrv.it:38443/api/management/feeds/get/Neo
            // Native API: https://proget.netsrv.it:38443/api/json/Feeds_GetFeed?Feed_Name=Neo
            // FeedType_Name
            var client = new RestClient(progetUrl);
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

        private async Task<string> GetFeedIdAsync(string progetUrl, string feedName, string apiKey)
        {
            // Native API: https://proget.netsrv.it:38443/api/json/Feeds_GetFeed?Feed_Name={feedName}
            //   or '{\"Feed_Name\": \"{feedName}\"}' as JsonBody in POST request
            var client = new HttpClient
            {
                BaseAddress = new Uri(progetUrl)
            };
            client.DefaultRequestHeaders.Add("X-ApiKey", apiKey);
            dynamic jsonObj = new JObject();
            jsonObj.Feed_Name = feedName;
            var content = new StringContent(jsonObj.ToString(), System.Text.Encoding.UTF8, "application/json");
            try
            {
                var response = await client.PostAsync(@"/api/json/Feeds_GetFeed", content); // Native API
                response.EnsureSuccessStatusCode();
                var strBody = await response.Content.ReadAsStringAsync();
                _log.Verbose("response.Content = '{0}'", strBody);
                dynamic resp = JObject.Parse(strBody);
                var feedId = resp.Feed_Id.ToString();
                _log.Information($"Определили для фида {progetUrl}feeds/{feedName} Feed_Id = {feedId}");
                return feedId;
            }
            catch (Exception e)
            {
                _log.Error(e, $"Не смогли определить ид фида {progetUrl}feeds/{feedName}! Возможно из-за отсутствия привелегии \"Native API\" у API-Key");
                return default;
            }
        }

        private string CombineVersion(string majorNumber, string minorNumber, string buildNumber, string revisionNumber)
        {
            if (string.IsNullOrEmpty(majorNumber))
            {
                throw new ArgumentException($"'{nameof(majorNumber)}' cannot be null or empty", nameof(majorNumber));
            }
            string version = majorNumber;
            if (!string.IsNullOrEmpty(minorNumber) && minorNumber != "-1")
            {
                version = $"{version}.{minorNumber}";
            }
            if (!string.IsNullOrEmpty(buildNumber) && buildNumber != "-1")
            {
                version = $"{version}.{buildNumber}";
            }
            if (!string.IsNullOrEmpty(revisionNumber) && revisionNumber != "-1")
            {
                version = $"{version}.{revisionNumber}";
            }
            return version;
        }
    }
}

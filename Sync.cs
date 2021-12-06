using Inedo.UPack.Net;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Security;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;

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
        private static readonly string TempDir = Path.Combine(Path.GetTempPath(), $@"updater{Path.DirectorySeparatorChar}");

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
                var sourceType = await GetFeedTypeAsync(feedConfig.SourceProGetUrl, feedConfig.SourceProGetFeedName, feedConfig.SourceProGetApiKey);
                var destType = await GetFeedTypeAsync(feedConfig.DestProGetUrl, feedConfig.DestProGetFeedName, feedConfig.DestProGetApiKey);
                if(sourceType.ToLower() == destType.ToLower()) { 
                    switch (sourceType.ToLower())
                    {
                        case "universal":
                            await SyncUniversalFeedsTask(feedConfig);
                            break;
                        case "nuget":
                            await SyncNuGetFeedsTask(feedConfig);
                            break;
                        case "vsix":
                            await SyncVsixFeedsTask(feedConfig);
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
            _log.Information("Start syncing upack-feeds");
            SecureString sourceApiKey = new NetworkCredential("", proGetConfig.SourceProGetApiKey).SecurePassword;

            var sourceEndpoint = new UniversalFeedEndpoint(new Uri($"{proGetConfig.SourceProGetUrl}/upack/{proGetConfig.SourceProGetFeedName}"), "api", sourceApiKey);

            var sourceFeed = new UniversalFeedClient(sourceEndpoint);

            SecureString destApiKey = new NetworkCredential("", proGetConfig.DestProGetApiKey).SecurePassword;

            var destEndpoint = new UniversalFeedEndpoint(new Uri($"{proGetConfig.DestProGetUrl}/upack/{proGetConfig.DestProGetFeedName}"), "api", destApiKey);

            var destFeed = new UniversalFeedClient(destEndpoint);

            var packages = await sourceFeed.ListPackagesAsync("", null);
            var dir = $"{TempDir}";
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            foreach (var p in packages)
            {
                _log.Information("Target package {Group}/{name}", p.Group, p.Name);
                var search = await destFeed.SearchPackagesAsync(p.Name);
                if (!search.Any(x => x.FullName == p.FullName))
                {
                    _log.Information("Not found {Group}/{Name} in {dProGetFeed}, copy from {sProGetFeed}", p.Group, p.Name, 
                        $"{proGetConfig.DestProGetUrl}feeds/{proGetConfig.DestProGetFeedName}",
                        $"{proGetConfig.SourceProGetUrl}feeds/{proGetConfig.SourceProGetFeedName}");
                    foreach (var ver in p.AllVersions)
                    {
                        var file = Path.Combine(dir, $"{p.Group}_{p.Name}_{ver}.upack");
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
                        _log.Information("Found package {Group}/{Name}, in local ProGet, cheking versions", p.Group, p.Name);
                        foreach (var ver in p.AllVersions)
                        {
                            if (!pack.AllVersions.Contains(ver))
                            {
                                var file = Path.Combine(dir, $"{p.Group}_{p.Name}_{ver}.upack");
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
                        _log.Information("Not found new version {Group}/{Name} in {SourceProGetFeed}", p.Group, p.Name, 
                            $"{proGetConfig.SourceProGetUrl}feeds/{proGetConfig.SourceProGetFeedName}");
                    }
                }
            }
            _log.Information("Finish syncing upack-feeds");
        }

        private async Task SyncNuGetFeedsTask(ProGetConfig proGetConfig)
        {
            var sourcePackageList = new Dictionary<string, string>();
            var destPackageList = new Dictionary<string, string>();

            try
            {
                _log.Information("Пытаюсь получить список nuget-пакетов из прогета источника {SourceProGetFeed}", 
                    $"{proGetConfig.SourceProGetUrl}feeds/{proGetConfig.SourceProGetFeedName}");
                sourcePackageList = await GetNugetFeedPackageListAsync(proGetConfig.SourceProGetUrl, proGetConfig.SourceProGetFeedName, proGetConfig.SourceProGetApiKey);
            }
            catch (Exception e)
            {
                _log.Error(e, "Не смог получить список nuget-пакетов из прогета источника {SourceProGetFeed}", 
                    $"{proGetConfig.SourceProGetUrl}feeds/{proGetConfig.SourceProGetFeedName}");
            }

            try
            {
                _log.Information("Пытаюсь получить список nuget-пакетов из прогета назначения для сравнения {DestinationProGetFeed}", 
                    $"{proGetConfig.DestProGetUrl}feeds/{proGetConfig.DestProGetFeedName}");
                destPackageList = await GetNugetFeedPackageListAsync(proGetConfig.DestProGetUrl, proGetConfig.DestProGetFeedName, proGetConfig.DestProGetApiKey);
            }
            catch (Exception e)
            {
                _log.Error(e, "Не смог получить список nuget-пакетов из прогета назначения {DestProGetFeed}", 
                    $"{proGetConfig.DestProGetUrl}feeds/{proGetConfig.DestProGetFeedName}");
            }

            _log.Information("Приступаю к сравнению nuget-фидов");
            foreach (var package in sourcePackageList)
            {
                dynamic packageDynamic = JObject.Parse(package.Value);
                if (!destPackageList.ContainsKey(packageDynamic.Id.ToString() + "_" + packageDynamic.Version.ToString()))
                {
                    _log.Information("Не нашел nuget-пакет {PackageName} версии {PackageVersion} в {DestProGetFeed}}, выкачиваю и выкладываю.", 
                        packageDynamic.Id.ToString(), packageDynamic.Version.ToString(), 
                        $"{proGetConfig.DestProGetUrl}feeds/{proGetConfig.DestProGetFeedName}");
                    await GetNugetPackageAsync(proGetConfig.SourceProGetUrl, proGetConfig.SourceProGetFeedName, proGetConfig.SourceProGetApiKey, 
                        packageDynamic.Id.ToString(), packageDynamic.Version.ToString());
                    await PushNugetPackageAsync(proGetConfig.DestProGetUrl, proGetConfig.DestProGetFeedName, proGetConfig.DestProGetApiKey, 
                        packageDynamic.Id.ToString(), packageDynamic.Version.ToString());
                }
            }
            _log.Information("Закончил сравнение nuget-фидов");
        }

        private async Task<Dictionary<string, string>> GetNugetFeedPackageListAsync(string progetUrl, string feedName, string apiKey)
        {
            // ODATA (v2), used: https://proget.netsrv.it:38443/nuget/seqplug/Packages?$format=json
            // JSON-LD (v3) API, disabled for feed by-default: https://proget.netsrv.it:38443/nuget/seqplug/v3/index.json
            Dictionary<string, string> packageList = new Dictionary<string, string>();
            var client = new HttpClient
            {
                BaseAddress = new Uri(progetUrl)
            };
            client.DefaultRequestHeaders.Add("X-ApiKey", apiKey);
            var response = await client.GetAsync($@"nuget/{feedName}/v3/search"); // Feed API
            response.EnsureSuccessStatusCode();
            var strBody = await response.Content.ReadAsStringAsync();
            _log.Debug("response.Content = '{0}'", strBody);
            dynamic resp = JObject.Parse(strBody);
            foreach (var package in resp.data)
            {
                string id = package.id.ToString();

                _log.Information("Нашёл семейство nuget-пакетов {PackageName}", id);

                foreach (var ver in package.versions)
                {
                    string version = ver.version.ToString();

                    _log.Information("Нашел nuget-пакет {PackageName} версии {PackageVersion} в {ProGetFeed}", 
                        id, version,
                        $"{progetUrl}feeds/{feedName}");

                    var packageName = id + "_" + version;

                    packageList.Add(packageName, package.ToString());
                }
            }
            return packageList;
        }

        private async Task GetNugetPackageAsync(string progetUrl, string feedName, string apiKey, string packageName, string packageVersion)
        {
            // http://proget-server/api/v2/package/{feedName}/{packageName}/{optional-version}
            // https://proget.netsrv.it:38443/nuget/seqplug/package/Seq.App.Exporter/1.2.3
            var dir = $"{TempDir}";
            var fileName = $"{packageName}_{packageVersion}.nupkg";
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var fullFileName = Path.GetFullPath(Path.Combine(dir, fileName));
            try
            {
                var client = new HttpClient
                {
                    BaseAddress = new Uri(progetUrl)
                };
                client.DefaultRequestHeaders.Add("X-ApiKey", apiKey);
                using var response = await client.GetAsync($"nuget/{feedName}/package/{packageName}/{packageVersion}"); // Feed API
                using var fileStream = File.Create(fullFileName);
                await response.Content.CopyToAsync(fileStream);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception e)
            {
                _log.Error(e, "Не получилось скачать nuget-пакет {packageName}/{packageVersion}! Возможно из-за отсутствия привелегии \"Feed API\" у API-Key", 
                    packageName, packageVersion);
            }
        }
        private async Task PushNugetPackageAsync(string progetUrl, string feedName, string apiKey, string packageName, string packageVersion)
        {
            var dir = $"{TempDir}";
            var fileName = $"{packageName}_{packageVersion}.nupkg";
            var fullFileName = Path.GetFullPath(Path.Combine(dir, fileName));
            var fileInfo = new FileInfo(fullFileName);
            long fileSize = fileInfo.Length;
            _log.Information("Выкладываю nuget-пакет {packageName} версии {packageVersion} в {ProGetFeed}", packageName, packageVersion, $"{progetUrl}feed/{feedName}");
            try
            {
                var client = new HttpClient
                {
                    BaseAddress = new Uri(progetUrl)
                };
                client.DefaultRequestHeaders.Add("X-ApiKey", apiKey);
                using var content = new MultipartFormDataContent();
                using var fileStream = File.OpenRead(fullFileName);
                using var streamContent = new StreamContent(fileStream);
                using (var fileContent = new ByteArrayContent(await streamContent.ReadAsByteArrayAsync()))
                {
                    fileContent.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue(@"application/octet-stream");
                    fileContent.Headers.ContentLength = fileSize;
                    content.Add(fileContent, "filename", fileName);
                    var response = await client.PutAsync($"nuget/{feedName}/", content); // Feed API?
                    _log.Debug("response: StatusCode = '{StatusCode}', ReasonPhrase = '{ReasonPhrase}'", response.StatusCode, response.ReasonPhrase);
                    response.EnsureSuccessStatusCode();
                }
                fileStream.Close();
                try
                {
                    File.Delete(fullFileName);
                    _log.Verbose("Удалили временный файл '{fullFileName}'", fullFileName);
                }
                catch (Exception e)
                {
                    _log.Warning(e, "Не получилось удалить временный файл '{fullFileName}'", fullFileName);
                }
            }
            catch (Exception e)
            {
                _log.Error(e, "Не получилось загрузить nuget-пакет {packageName}/{packageVersion} в {ProGetFeed}! Возможно из-за отсутствия привелегии \"Feed API\" у API-Key",
                    packageName, packageVersion, $"{progetUrl}feeds/{feedName}");
            }
        }

        private async Task SyncVsixFeedsTask(ProGetConfig proGetConfig)
        {
            var sourcePackageList = await GetVsixFeedPackageListAsync("источника", proGetConfig.SourceProGetUrl, proGetConfig.SourceProGetFeedName, proGetConfig.SourceProGetApiKey);
            var destPackageList = await GetVsixFeedPackageListAsync("назначения", proGetConfig.DestProGetUrl, proGetConfig.DestProGetFeedName, proGetConfig.DestProGetApiKey);
            _log.Information("Приступаю к сравнению vsix-фидов");
            foreach (var package in sourcePackageList)
            {
                dynamic packageDynamic = JObject.Parse(package.Value);
                if (!destPackageList.ContainsKey(packageDynamic.Package_Id.ToString() + "_" + packageDynamic.Version.ToString()))
                {
                    _log.Information("Не нашел vsix-пакет {PackageId} версии {PackageVersion} в {DestProGetFeed}, выкачиваю и выкладываю.",
                        packageDynamic.Package_Id.ToString(), packageDynamic.Version.ToString(), 
                        $"{proGetConfig.DestProGetUrl}feeds/{proGetConfig.DestProGetFeedName}");

                    await GetVsixPackageAsync(proGetConfig.SourceProGetUrl, proGetConfig.SourceProGetFeedName, proGetConfig.SourceProGetApiKey,
                        packageDynamic.DisplayName_Text.ToString(), packageDynamic.Package_Id.ToString(), packageDynamic.Version.ToString());

                    await PushVsixPackageAsync(proGetConfig.DestProGetUrl, proGetConfig.DestProGetFeedName, proGetConfig.DestProGetApiKey,
                        packageDynamic.DisplayName_Text.ToString(), packageDynamic.Package_Id.ToString(), packageDynamic.Version.ToString());
                }
            }
            _log.Information("Закончил сравнение vsix-фидов");
        }

        private async Task<Dictionary<string, string>> GetVsixFeedPackageListAsync(string side, string progetUrl, string feedName, string apiKey)
        {
            // Invoke-RestMethod -Method POST -Uri https://proget.netsrv.it:38443/api/json/VsixPackages_GetPackages?Feed_Id=2046 -ContentType "application/json" -Headers @{"X-ApiKey" = "XXXXXXXXX"; "charset" = "utf-8"}
            // Invoke-RestMethod -Method POST -Uri https://proget.netsrv.it:38443/api/json/VsixPackages_GetPackages -ContentType "application/json" -Headers @{"X-ApiKey" = "XXXXXXXXX"; "charset" = "utf-8"} -Body (@{"Feed_Id" = 2046}|ConvertTo-Json)
            // Use 'Native API'. See https://proget.netsrv.it:38443/reference/api and https://docs.inedo.com/docs/proget/reference/api/native
            _log.Information("Пытаюсь получить список vsix-пакетов из прогета {side} {ProGetFeed}", side, $"{progetUrl}feeds/{feedName}");
            Dictionary<string, string> packageList = new Dictionary<string, string>();
            var feedId = await GetFeedIdAsync(progetUrl, feedName, apiKey);

            var client = new HttpClient
            {
                BaseAddress = new Uri(progetUrl)
            };
            client.DefaultRequestHeaders.Add("X-ApiKey", apiKey);
            dynamic jsonObj = new JObject();
            jsonObj.Feed_Id = feedId;
            _log.Debug("request.body = '{0}'", jsonObj.ToString());
            var content = new StringContent(jsonObj.ToString(), System.Text.Encoding.UTF8, "application/json");
            try
            {
                var response = await client.PostAsync(@"/api/json/VsixPackages_GetPackages", content); // Native API
                response.EnsureSuccessStatusCode();
                var strBody = await response.Content.ReadAsStringAsync();
                _log.Debug("response.Content = '{0}'", strBody);
                dynamic resp = JArray.Parse(strBody);
                foreach (var package in resp)
                {
                    // DisplayName_Text, Package_Id
                    // Major_Number, Minor_Number, Build_Number, Revision_Number
                    string version = CombineVersion(
                        package.Major_Number.ToString(), 
                        package.Minor_Number.ToString(), 
                        package.Build_Number.ToString(), 
                        package.Revision_Number.ToString()
                        );
                    package.Add("Version", version.ToString());
                    _log.Information("Нашел vsix-пакет {PackageId} версии {PackageVersion} в {ProGetFeed}", 
                        package.Package_Id.ToString(), package.Version.ToString(), 
                        $"{progetUrl}feeds/{feedName}");
                    var packageName = package.Package_Id.ToString() + "_" + package.Version.ToString();
                    packageList.Add(packageName, package.ToString());
                }
            }
            catch (Exception e)
            {
                _log.Error(e, "Не смогли получить список vsix-пакетов из прогета {side} {ProGetFeed}! Возможно из-за отсутствия привелегии \"Native API\" у API-Key", 
                    side, $"{progetUrl}feeds/{feedName}");
                throw;
            }
            _log.Information("Получил список vsix-пакетов из прогета {side} {ProGetFeed}", side, $"{progetUrl}feeds/{feedName}");
            return packageList;
        }

        private async Task GetVsixPackageAsync(string progetUrl, string feedName, string apiKey, string packageName, string Package_Id, string packageVersion)
        {
            // http://proget-server/vsix/{feedName}/downloads/{Package_Id}/{packageVersion}
            // https://proget.netsrv.it:38443/vsix/NeoGallery/downloads/MobiTemplateWizard.cae77667-8ddc-4040-acf7-f7491071af30/1.0.1
            var dir = Path.Combine(TempDir, Package_Id, packageVersion);
            var fileName = $"{packageName}.vsix";
            if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
            var fullFileName = Path.GetFullPath(Path.Combine(dir, fileName));
            try
            {
                var client = new HttpClient
                {
                    BaseAddress = new Uri(progetUrl)
                };
                client.DefaultRequestHeaders.Add("X-ApiKey", apiKey);
                using var response = await client.GetAsync($"vsix/{feedName}/downloads/{Package_Id}/{packageVersion}"); // Feed API
                using var fileStream = File.Create(fullFileName);
                await response.Content.CopyToAsync(fileStream);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception e)
            {
                _log.Error(e, "Не получилось скачать vsix-пакет {Package_Id}/{PackageVersion}", Package_Id, packageVersion);
            }
        }

        private async Task PushVsixPackageAsync(string progetUrl, string feedName, string apiKey, string packageName, string Package_Id, string packageVersion)
        {
            // Invoke-RestMethod -Method POST -Uri https://proget.netsrv.it:38443/vsix/NeoGallery -InFile.\MobiTemplateWizard.vsix - Headers @{ "X-ApiKey" = "XXXXXXXXXXXXXX"}
            var dir = $"{TempDir}{Package_Id}/{packageVersion}/";
            var fileName = $"{packageName}.vsix";
            var fullFileName = dir + fileName;
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
                    _log.Debug("response: StatusCode = '{StatusCode}', ReasonPhrase = '{ReasonPhrase}'", response.StatusCode, response.ReasonPhrase);
                    response.EnsureSuccessStatusCode();
                }
                try
                {
                    File.Delete(fullFileName);
                    _log.Verbose("Удалили временный файл '{fullFileName}'", fullFileName);
                }
                catch (Exception e)
                {
                    _log.Warning(e, "Не получилось удалить временный файл '{fullFileName}'", fullFileName);
                }
                try
                {
                    Directory.Delete(dir);
                    _log.Verbose("Удалили временный каталог '{dir}'", dir);
                }
                catch (Exception e)
                {
                    _log.Warning(e, "Не получилось удалить временный каталог '{dir}'", dir);
                }
            }
            catch (Exception e)
            {
                _log.Error(e, "Не получилось загрузить vsix-пакет {PackageId}/{PackageVersion} в {ProGetFeed}", 
                    Package_Id, packageVersion, $"{progetUrl}feeds/{feedName}");
            }
        }

        private async Task<string> GetFeedTypeAsync(string progetUrl, string feedName, string apiKey)
        {
            // Feed Management API: https://proget.netsrv.it:38443/api/management/feeds/get/Neo
            // Native API: https://proget.netsrv.it:38443/api/json/Feeds_GetFeed?Feed_Name=Neo
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
                _log.Debug($"response.Content = '{strBody}'");
                dynamic resp = JObject.Parse(strBody);
                var feedType = resp.FeedType_Name.ToString();
                _log.Information("Определили тип фида {ProGetFeed} как {feedType}", $"{progetUrl}feeds/{feedName}", feedType.ToString().ToLower());
                return feedType;
            }
            catch (Exception e)
            {
                _log.Error(e, "Не смогли определить тип фида {ProGetFeed}! Возможно из-за отсутствия привелегии \"Native API\" у API-Key", $"{progetUrl}feeds/{feedName}");
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
                _log.Debug("response.Content = '{0}'", strBody);
                dynamic resp = JObject.Parse(strBody);
                var feedId = resp.Feed_Id.ToString();
                _log.Information("Определили для фида {ProGetFeed} Feed_Id = {feedId}", $"{progetUrl}feeds/{feedName}", feedId);
                return feedId;
            }
            catch (Exception e)
            {
                _log.Error(e, "Не смогли определить ид фида {ProGetFeed}! Возможно из-за отсутствия привелегии \"Native API\" у API-Key", 
                    $"{progetUrl}feeds/{feedName}");
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

        public void CleanUpDirs()
        {
            var dirsList = new List<string>() {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory,"packages"), // old place for temporary nuget-packages
                @"C:\temp\updater\", // old place, Windows preffered
                $"{TempDir}" // current place for temporary packages (upack, nuget, vsix)
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

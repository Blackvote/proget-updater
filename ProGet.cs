using Newtonsoft.Json.Linq;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using updater.DataModels;

namespace updater
{
    internal class ProGet
    {
        private readonly ILogger _log;

        public ProGet()
        {
            _log = Log.Logger.ForContext("ClassType", GetType());
        }

        public async Task<Dictionary<string, PackageData>> GetNugetFeedPackageListAsync(string progetUrl, string feedName, string apiKey)
        {
            // ODATA (v2), used: https://proget.netsrv.it:38443/nuget/seqplug/Packages?$format=json
            // JSON-LD (v3) API, disabled for feed by-default: https://proget.netsrv.it:38443/nuget/seqplug/v3/index.json
            Dictionary<string, PackageData> packageList = new Dictionary<string, PackageData>();
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

                    var data = new PackageData
                    {
                        Id = id,
                        Version = version
                    };

                    packageList.Add(packageName, data);
                }
            }
            return packageList;
        }

        public async Task GetNugetPackageAsync(string progetUrl, string feedName, string apiKey, string packageName, string packageVersion, string downloadToDirectory)
        {
            // http://proget-server/api/v2/package/{feedName}/{packageName}/{optional-version}
            // https://proget.netsrv.it:38443/nuget/seqplug/package/Seq.App.Exporter/1.2.3
            var dir = $"{downloadToDirectory}";
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

        public async Task PushNugetPackageAsync(string progetUrl, string feedName, string apiKey, string packageName, string packageVersion, string uploadFromDirectory)
        {
            var dir = $"{uploadFromDirectory}";
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

        public async Task<Dictionary<string, string>> GetVsixFeedPackageListAsync(string side, string progetUrl, string feedName, string apiKey)
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
            var content = new StringContent(jsonObj.ToString(), Encoding.UTF8, "application/json");
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

        public async Task GetVsixPackageAsync(string progetUrl, string feedName, string apiKey, string packageName, string Package_Id, string packageVersion, string downloadToDirectory)
        {
            // http://proget-server/vsix/{feedName}/downloads/{Package_Id}/{packageVersion}
            // https://proget.netsrv.it:38443/vsix/NeoGallery/downloads/MobiTemplateWizard.cae77667-8ddc-4040-acf7-f7491071af30/1.0.1
            var dir = Path.Combine(downloadToDirectory, Package_Id, packageVersion);
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

        public async Task PushVsixPackageAsync(string progetUrl, string feedName, string apiKey, string packageName, string Package_Id, string packageVersion, string uploadFromDirectory)
        {
            // Invoke-RestMethod -Method POST -Uri https://proget.netsrv.it:38443/vsix/NeoGallery -InFile.\MobiTemplateWizard.vsix - Headers @{ "X-ApiKey" = "XXXXXXXXXXXXXX"}
            var dir = $"{uploadFromDirectory}{Package_Id}/{packageVersion}/";
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

        public async Task<string> GetFeedTypeAsync(string progetUrl, string feedName, string apiKey)
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
            var content = new StringContent(jsonObj.ToString(), Encoding.UTF8, "application/json");
            try
            {
                var response = await client.PostAsync(@"/api/json/Feeds_GetFeed", content); // Native API
                response.EnsureSuccessStatusCode();
                var strBody = await response.Content.ReadAsStringAsync();
                _log.Debug($"response.Content = '{strBody}'");
                dynamic resp = JObject.Parse(strBody);
                var feedType = resp.FeedType_Name.ToString();
                _log.Information("Определили тип фида {ProGetFeed} как {feedType}", $"{progetUrl}/feeds/{feedName}", feedType.ToString().ToLower());
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
            var content = new StringContent(jsonObj.ToString(), Encoding.UTF8, "application/json");
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
    }
}

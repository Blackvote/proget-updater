using Inedo.UPack;
using Inedo.UPack.Net;
using Newtonsoft.Json.Linq;
using RestSharp;
using RestSharp.Authenticators;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace updater
{
    public class Sync
    {
        private readonly string _fromApiKey;
        private readonly string _fromUrl;
        private readonly string _toApiKey;
        private readonly string _toUrl;
        private readonly ProgramConfig _programConfig;
        private readonly ILogger _log;


        public Sync(ProgramConfig programConfig, ILogger log)
        {
            _programConfig = programConfig;
            _log = log;

        }

        public async Task check()
        {
            _log.Information("Начинаю сравнение пакетов в локальном и удаленном Proget'ах");
            SecureString SourceApiKey = new NetworkCredential("", _programConfig.SourceProGetApiKey).SecurePassword;

            var SourceEndpoint = new UniversalFeedEndpoint(new Uri($"{_programConfig.SourceProGetUrl}/upack/{_programConfig.SourceProGetFeedName}"), "api", SourceApiKey);

            var SourceFeed = new UniversalFeedClient(SourceEndpoint);


            SecureString DestApiKey = new NetworkCredential("", _programConfig.DestProGetApiKey).SecurePassword;

            var DestEndpoint = new UniversalFeedEndpoint(new Uri($"{_programConfig.DestProGetUrl}/upack/{_programConfig.DestProGetFeedName}"), "api", DestApiKey);

            var DestFeed = new UniversalFeedClient(DestEndpoint);


            var packages = await SourceFeed.ListPackagesAsync("", null);
            foreach (var p in packages)
            {
                _log.Information($"Целевой пакет {p.Name} {p.LatestVersion}");

                var search = await DestFeed.SearchPackagesAsync(p.Name);
                //var search = await DestFeed.SearchPackagesAsync("master/pokermaster");
                if (search.Count == 0)
                {
                    _log.Information($"Не нашел пакет {p.Group}/{p.Name} в {_programConfig.DestProGetUrl}/{_programConfig.DestProGetFeedName}, копирую из {_programConfig.SourceProGetUrl}/{_programConfig.SourceProGetFeedName}");


                    foreach (var ver in p.AllVersions)
                    {
                        using (var packageStream = await SourceFeed.GetPackageStreamAsync(p.FullName, ver))
                        using (var fileStream = File.Create($@"C:\temp\{p.Group}_{p.Name}_{ver}.upack"))
                        {
                            await packageStream.CopyToAsync(fileStream);
                            
                        }
                        using (var fileStream = File.OpenRead($@"C:\temp\{p.Group}_{p.Name}_{ver}.upack"))
                        {
                            _log.Information($"Копирую пакет {p.FullName} {ver} в {_programConfig.DestProGetUrl}");
                            await DestFeed.UploadPackageAsync(fileStream);
                        }
                        
                    }

                }

                _log.Information("Нашел пакет {0}, в локальном прогете, проверяю версии", p.Name);
                foreach (var pack in search)
                {
                    if (p.Group == pack.Group)
                        if (p.LatestVersion != pack.LatestVersion)
                        {
                            foreach (var ver in p.AllVersions)
                            {
                                //bool s = Array.Exists(pack.AllVersions, vers => vers == ver);
                                //if (Array.Exists(pack.AllVersions, v => v == ver.ToString()))
                                if (!pack.AllVersions.Contains(ver))
                                {
                                    using (var packageStream = await SourceFeed.GetPackageStreamAsync(p.FullName, ver))
                                    using (var fileStream = File.Create($@"C:\temp\{p.Group}_{p.Name}_{ver}.upack"))
                                    {
                                        await packageStream.CopyToAsync(fileStream);

                                    }
                                    using (var fileStream = File.OpenRead($@"C:\temp\{p.Group}_{p.Name}_{ver}.upack"))
                                    {
                                        _log.Information($"Копирую пакет {p.FullName} в {_programConfig.DestProGetUrl}");
                                        await DestFeed.UploadPackageAsync(fileStream);
                                    }
                                }
                            }
                        }


                }
                _log.Information("Не нашел свежих версий в {0}", _programConfig.SourceProGetUrl);
            }

        }
    }
}
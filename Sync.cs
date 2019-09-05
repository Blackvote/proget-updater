using Inedo.UPack;
using Inedo.UPack.Net;
using Serilog;
using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Security;
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
                _log.Information("Целевой пакет {group}/{name}", p.Group, p.Name);
                var search = await DestFeed.SearchPackagesAsync(p.Name);
                if (!search.Any(x=>x.FullName == p.FullName))
                {
                    _log.Information("Не нашел пакет {group}/{name} в {progetur}/{feedname}, копирую из {progeturl}/{progetfeed}", p.Group, p.Name, _programConfig.DestProGetUrl, _programConfig.DestProGetFeedName, _programConfig.SourceProGetUrl, _programConfig.SourceProGetFeedName);


                    foreach (var ver in p.AllVersions)
                    {
                        using (var packageStream = await SourceFeed.GetPackageStreamAsync(p.FullName, ver))
                        using (var fileStream = File.Create($@"C:\temp\{p.Group}_{p.Name}_{ver}.upack"))
                        {
                            await packageStream.CopyToAsync(fileStream);
                            
                        }
                        using (var fileStream = File.OpenRead($@"C:\temp\{p.Group}_{p.Name}_{ver}.upack"))
                        {
                            _log.Information("Копирую пакет {group}/{name} {version} в {progeturl}", p.Group, p.Name, ver, _programConfig.DestProGetUrl);
                            await DestFeed.UploadPackageAsync(fileStream);
                        }

                    }

                }

                foreach (var pack in search)
                {
                    if (p.FullName == pack.FullName)
                    {
                        _log.Information("Нашел пакет {group}/{name}, в локальном прогете, проверяю версии", p.Group, p.Name);
                        foreach (var ver in p.AllVersions)
                        {
                            if (!pack.AllVersions.Contains(ver))
                            {
                                using (var packageStream = await SourceFeed.GetPackageStreamAsync(p.FullName, ver))
                                using (var fileStream = File.Create($@"C:\temp\{p.Group}_{p.Name}_{ver}.upack"))
                                {
                                    await packageStream.CopyToAsync(fileStream);

                                }
                                using (var fileStream = File.OpenRead($@"C:\temp\{p.Group}_{p.Name}_{ver}.upack"))
                                {
                                    _log.Information("Копирую пакет {group}/{package} в {progeturl}", p.Group, p.Name, _programConfig.DestProGetUrl);
                                    await DestFeed.UploadPackageAsync(fileStream);
                                }

                            }
                        }
                        _log.Information("Нет новых версий {group}/{name} в {progeturl}/{geedname}", p.Group, p.Name, _programConfig.SourceProGetUrl, _programConfig.SourceProGetFeedName);

                    }
                }

            }

        }
    }
}
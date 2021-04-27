using Newtonsoft.Json.Linq;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;

namespace updater
{
    class ConfigReader
    {
        private readonly ILogger _log;
        private readonly string _configPath = AppDomain.CurrentDomain.BaseDirectory + "config.json";
        private readonly JArray jArray;

        public ConfigReader()
        {
            _log = Log.Logger.ForContext("ClassType", GetType());

            try
            {
                _log.Information("Пытаюсь найти файл конфигурации {ConfigFilePath}", _configPath);
                if (File.Exists(_configPath))
                {
                    StreamReader config = new StreamReader(_configPath);
                    var readedConfig = config.ReadToEnd();
                    List<ProGetConfig> tempList = new List<ProGetConfig>();
                    try
                    {
                        jArray = JArray.Parse(readedConfig);
                        _log.Information("Нашел {ConfigurationCount} конфигураций синхронизации фидов", jArray.Count);

                        foreach (var conf in jArray)
                        {
                            var progetConfig = new ProGetConfig
                            {
                                SourceProGetUrl = conf["SourceProget"]["Address"].ToString(),
                                SourceProGetFeedName = conf["SourceProget"]["FeedName"].ToString(),
                                SourceProGetApiKey = conf["SourceProget"]["ApiKey"].ToString(),
                                DestProGetUrl = conf["DestProget"]["Address"].ToString(),
                                DestProGetFeedName = conf["DestProget"]["FeedName"].ToString(),
                                DestProGetApiKey = conf["DestProget"]["ApiKey"].ToString()
                            };
                            tempList.Add(progetConfig);
                        }
                    }
                    catch (Exception e)
                    {
                        _log.Information(e, "Конфигурация имеет тип object, для синхронизации нескольких фидов необходимо отредактировать 'config.json', смотри README.md");

                        JObject jsonConfig = JObject.Parse(readedConfig);
                        var progetConfig = new ProGetConfig
                        {
                            SourceProGetUrl = jsonConfig["SourceProget"]["Address"].ToString(),
                            SourceProGetFeedName = jsonConfig["SourceProget"]["FeedName"].ToString(),
                            SourceProGetApiKey = jsonConfig["SourceProget"]["ApiKey"].ToString(),
                            DestProGetUrl = jsonConfig["DestProget"]["Address"].ToString(),
                            DestProGetFeedName = jsonConfig["DestProget"]["FeedName"].ToString(),
                            DestProGetApiKey = jsonConfig["DestProget"]["ApiKey"].ToString()
                        };

                        tempList.Add(progetConfig);
                    }
                    ProgramConfig.Instance.ProGetConfigs = tempList.ToArray();
                }
                else
                {
                    _log.Error("Файл config.json, в папке {FolderPath} не существует", AppDomain.CurrentDomain.BaseDirectory);
                    throw new Exception("File doesn't exists!!!!");
                }
            }
            catch (Exception e)
            {
                _log.Fatal(e.Message);
                throw;
            }
        }
    }
}

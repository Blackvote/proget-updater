using Newtonsoft.Json.Linq;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace updater
{
    class ConfigReader
    {
        private ILogger _log;
        private readonly string _configPath = AppDomain.CurrentDomain.BaseDirectory + "config.json";
        private JArray jArray;
        private JObject jObject;

        public ConfigReader()
        {
            _log = Log.Logger.ForContext("ClassType", GetType());

            try
            {
                _log.Information("Пытаюсь найти файл конфигурации {ConfigFilePath}", _configPath);
                if (File.Exists(_configPath))
                {
                    using (StreamReader config = new StreamReader(_configPath))
                    {
                        var readedConfig = config.ReadToEnd();
                        List<ProGetConfig> tempList = new List<ProGetConfig>();
                        try
                        {
                            jArray = JArray.Parse(readedConfig);
                            _log.Information("Нашел {ConfigurationCount} конфигураций синхронизации фидов",
                                jArray.Count);

                            foreach (var conf in jArray)
                            {
                                var progetConfig = new ProGetConfig();
                                progetConfig.SourceProGetUrl = conf["SourceProget"]["Address"].ToString();
                                progetConfig.SourceProGetFeedName = conf["SourceProget"]["FeedName"].ToString();
                                progetConfig.SourceProGetApiKey = conf["SourceProget"]["ApiKey"].ToString();
                                progetConfig.DestProGetUrl = conf["DestProget"]["Address"].ToString();
                                progetConfig.DestProGetFeedName = conf["DestProget"]["FeedName"].ToString();
                                progetConfig.DestProGetApiKey = conf["DestProget"]["ApiKey"].ToString();
                                tempList.Add(progetConfig);
                            }

                            
                        }
                        catch (Exception e)
                        {
                            _log.Information("Конфигурация имеет тип object, для синхронизации нескльких фидов необходимо отредактировать конфиг, смотри readme");

                            JObject jsonConfig = JObject.Parse(readedConfig);
                            var progetConfig = new ProGetConfig();
                            progetConfig.SourceProGetUrl = jsonConfig["SourceProget"]["Address"].ToString();
                            progetConfig.SourceProGetFeedName = jsonConfig["SourceProget"]["FeedName"].ToString();
                            progetConfig.SourceProGetApiKey = jsonConfig["SourceProget"]["ApiKey"].ToString();

                            progetConfig.DestProGetUrl = jsonConfig["DestProget"]["Address"].ToString();
                            progetConfig.DestProGetFeedName = jsonConfig["DestProget"]["FeedName"].ToString();
                            progetConfig.DestProGetApiKey = jsonConfig["DestProget"]["ApiKey"].ToString();

                            tempList.Add(progetConfig);
                        }
                        ProgramConfig.Instance.ProGetConfigs = tempList.ToArray();
                    }

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

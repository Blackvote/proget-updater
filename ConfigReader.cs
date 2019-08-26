using Newtonsoft.Json.Linq;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace updater
{
    class ConfigReader
    {
        public ConfigReader(ProgramConfig programConfig, ILogger log)
        {
            try
            {
                using (StreamReader config = new StreamReader(@"config.json"))
                {
                    dynamic readedConfig = config.ReadToEnd();

                    JObject jsonConfig = JObject.Parse(readedConfig);

                    programConfig.SourceProGetUrl = jsonConfig["SourceProget"]["Address"].ToString();
                    programConfig.SourceProGetFeedName = jsonConfig["SourceProget"]["FeedName"].ToString();
                    programConfig.SourceProGetApiKey = jsonConfig["SourceProget"]["ApiKey"].ToString();

                    programConfig.DestProGetUrl = jsonConfig["DestProget"]["Address"].ToString();
                    programConfig.DestProGetFeedName = jsonConfig["DestProget"]["FeedName"].ToString();
                    programConfig.DestProGetApiKey = jsonConfig["DestProget"]["ApiKey"].ToString();

                }
            }
            catch (Exception e)
            {
                log.Fatal(e.Message);
                throw;
            }


        }

    }
}

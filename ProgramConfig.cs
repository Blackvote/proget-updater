using System;
using System.Collections.Generic;
using System.Text;

namespace updater
{
    public class ProgramConfig
    {

        //ProGetArea FROM
        /// <summary>
        /// Url to ProGet
        /// </summary>
        public string SourceProGetUrl { get; internal set; }

        /// <summary>
        /// ApiKey to ProGet
        /// </summary>
        public string SourceProGetApiKey { get; internal set; }

        /// <summary>
        /// ProGet feed name(teamName)
        /// </summary>
        public string SourceProGetFeedName { get; internal set; }




        //ProGetArea TO
        /// <summary>
        /// Url to ProGet
        /// </summary>
        public string DestProGetUrl { get; internal set; }

        /// <summary>
        /// ApiKey to ProGet
        /// </summary>
        public string DestProGetApiKey { get; internal set; }

        /// <summary>
        /// ProGet feed name(teamName)
        /// </summary>
        public string DestProGetFeedName { get; internal set; }





    }
}

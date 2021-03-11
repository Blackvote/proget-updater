using System;
using System.Collections.Generic;
using System.Text;

namespace updater
{
    public class ProGetConfig
    {

        // ProGetArea FROM (source)
        /// <summary>
        /// Url to source ProGet
        /// </summary>
        public string SourceProGetUrl { get; internal set; }

        /// <summary>
        /// ApiKey to source ProGet
        /// </summary>
        public string SourceProGetApiKey { get; internal set; }

        /// <summary>
        /// Feed name (teamName) in source ProGet
        /// </summary>
        public string SourceProGetFeedName { get; internal set; }

        // ProGetArea TO (destination)
        /// <summary>
        /// Url to destination ProGet
        /// </summary>
        public string DestProGetUrl { get; internal set; }

        /// <summary>
        /// ApiKey to destination ProGet
        /// </summary>
        public string DestProGetApiKey { get; internal set; }

        /// <summary>
        /// Feed name (teamName) in destination ProGet
        /// </summary>
        public string DestProGetFeedName { get; internal set; }

    }
}

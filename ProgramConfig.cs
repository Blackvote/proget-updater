using System;
using System.Collections.Generic;
using System.Text;

namespace updater
{
    public class ProgramConfig
    {
        public ProGetConfig[] ProGetConfigs { get; set; }

        public static ProgramConfig Instance { get; set; } = new ProgramConfig();
    }

}

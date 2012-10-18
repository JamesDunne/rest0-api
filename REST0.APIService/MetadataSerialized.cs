using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

using Newtonsoft.Json;

namespace REST0.APIService
{
    class MetadataSerialized
    {
        public string configHash;
        public string serviceName;
        public string methodName;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public string deprecated;
        public MetadataTimingsSerialized timings;
    }
}

using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace REST0.APIService.Descriptors
{
    class Query
    {
        [JsonProperty("xmlns", NullValueHandling = NullValueHandling.Ignore)]
        public Dictionary<string, string> XMLNamespaces { get; set; }

        [JsonProperty("withCTEidentifier", NullValueHandling = NullValueHandling.Ignore)]
        public string WithCTEidentifier { get; set; }
        [JsonProperty("withCTEexpression", NullValueHandling = NullValueHandling.Ignore)]
        public string WithCTEexpression { get; set; }

        [JsonProperty("select", NullValueHandling = NullValueHandling.Ignore)]
        public string Select { get; set; }
        [JsonProperty("from", NullValueHandling = NullValueHandling.Ignore)]
        public string From { get; set; }
        [JsonProperty("where", NullValueHandling = NullValueHandling.Ignore)]
        public string Where { get; set; }
        [JsonProperty("groupBy", NullValueHandling = NullValueHandling.Ignore)]
        public string GroupBy { get; set; }
        [JsonProperty("having", NullValueHandling = NullValueHandling.Ignore)]
        public string Having { get; set; }
        [JsonProperty("orderBy", NullValueHandling = NullValueHandling.Ignore)]
        public string OrderBy { get; set; }

        /// <summary>
        /// The final composed SQL query.
        /// </summary>
        [JsonIgnore]
        public string SQL { get; internal set; }
    }
}

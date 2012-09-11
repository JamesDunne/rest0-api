using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace REST0.APIService.Descriptors
{
    class Parameter
    {
        public string Name { get; set; }
        public bool IsOptional { get; set; }
        public string SqlName { get; set; }
        // Mutually exclusive:
        public string SqlType { get; set; }
        public ParameterType Type { get; set; }
    }

    class ParameterSerialized
    {
        [JsonIgnore()]
        readonly Parameter desc;

        internal ParameterSerialized(Parameter desc)
        {
            this.desc = desc;
        }

        [JsonProperty("optional", NullValueHandling = NullValueHandling.Ignore)]
        public bool? IsOptional { get { return desc.IsOptional ? true : (bool?)null; } }

        [JsonProperty("sqlName")]
        public string SqlName { get { return desc.SqlName; } }

        [JsonProperty("type", NullValueHandling = NullValueHandling.Ignore)]
        public string Type { get { return desc.Type != null ? desc.Type.Name : null; } }
        [JsonProperty("sqlType")]
        public string SqlType { get { return desc.Type != null ? desc.Type.Type : desc.SqlType; } }
    }
}

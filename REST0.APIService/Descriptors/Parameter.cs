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
        public string SqlName { get; set; }
        public string Description { get; set; }
        public bool IsOptional { get; set; }
        // `DBNull.Value` or a `System.Data.SqlTypes` object representing the default value.
        public object DefaultValue { get; set; }
        // Mutually exclusive:
        public ParameterType SqlType { get; set; }
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
        [JsonProperty("sqlType", NullValueHandling = NullValueHandling.Include)]
        public string SqlType
        {
            get
            {
                var type = desc.Type ?? desc.SqlType;
                if (type == null) return null;

                return type.FullType;
            }
        }

        [JsonProperty("type", NullValueHandling = NullValueHandling.Ignore)]
        public string Type { get { return desc.Type != null ? desc.Type.Name : null; } }

        [JsonProperty("description", NullValueHandling = NullValueHandling.Ignore)]
        public string Description { get { return desc.Description; } }
    }
}

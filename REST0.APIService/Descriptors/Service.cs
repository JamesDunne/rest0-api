using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace REST0.APIService.Descriptors
{
    class Service
    {
        [JsonProperty("name")]
        public string Name { get; set; }
        [JsonIgnore()]
        public Service BaseService { get; set; }
        [JsonProperty("$")]
        public IDictionary<string, string> Tokens { get; set; }
        [JsonProperty("connection")]
        public string ConnectionString { get; set; }
        [JsonProperty("parameterTypes")]
        public IDictionary<string, ParameterType> ParameterTypes { get; set; }
        [JsonProperty("methods")]
        public IDictionary<string, Method> Methods { get; set; }
    }

    class ServiceSerialized
    {
        [JsonIgnore]
        readonly Service desc;

        internal ServiceSerialized(Service desc)
        {
            this.desc = desc;
        }

        [JsonProperty("base", NullValueHandling = NullValueHandling.Ignore)]
        public string BaseService { get { return desc.BaseService == null ? null : desc.BaseService.Name; } }
        [JsonProperty("$", NullValueHandling = NullValueHandling.Ignore)]
        public IDictionary<string, string> Tokens { get { return desc.Tokens; } }
        [JsonProperty("connection", NullValueHandling = NullValueHandling.Ignore)]
        public string ConnectionString { get { return desc.ConnectionString; } }
        [JsonProperty("parameterTypes", NullValueHandling = NullValueHandling.Ignore)]
        public IDictionary<string, ParameterType> ParameterTypes
        {
            get
            {
                // TODO(jsd): Make the parser do copy-on-write instead of copy-on-inherit so that this will work.
                //if (desc.BaseService == null) return desc.ParameterTypes;
                //if (desc.ParameterTypes == desc.BaseService.ParameterTypes) return null;
                return desc.ParameterTypes;
            }
        }
        [JsonProperty("methods", NullValueHandling = NullValueHandling.Ignore)]
        public IDictionary<string, MethodSerialized> Methods { get { return desc.Methods.ToDictionary(m => m.Key, m => new MethodSerialized(m.Value), StringComparer.OrdinalIgnoreCase); } }
    }
}

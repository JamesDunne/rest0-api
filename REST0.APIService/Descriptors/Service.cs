using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace REST0.APIService.Descriptors
{
    class Service
    {
        public string Name { get; set; }
        public Service BaseService { get; set; }
        public IDictionary<string, string> Tokens { get; set; }
        public string ConnectionString { get; set; }
        public IDictionary<string, ParameterType> ParameterTypes { get; set; }
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
        public IDictionary<string, string> Tokens
        {
            get
            {
                if (desc.Tokens == null || desc.Tokens.Count == 0)
                    return null;
                return desc.Tokens;
            }
        }

        [JsonProperty("connection", NullValueHandling = NullValueHandling.Ignore)]
        public string ConnectionString { get { return desc.ConnectionString; } }

        [JsonProperty("parameterTypes", NullValueHandling = NullValueHandling.Ignore)]
        public IDictionary<string, ParameterType> ParameterTypes
        {
            get
            {
                if (desc.ParameterTypes == null || desc.ParameterTypes.Count == 0)
                    return null;

                // TODO(jsd): Make the parser do copy-on-write instead of copy-on-inherit so that this will work.
                //if (desc.BaseService == null) return desc.ParameterTypes;
                //if (desc.ParameterTypes == desc.BaseService.ParameterTypes) return null;

                return desc.ParameterTypes;
            }
        }

        [JsonProperty("methods", NullValueHandling = NullValueHandling.Ignore)]
        public IDictionary<string, MethodSerialized> Methods
        {
            get
            {
                if (desc.Methods == null || desc.Methods.Count == 0)
                    return null;
                return desc.Methods.ToDictionary(m => m.Key, m => new MethodSerialized(m.Value), StringComparer.OrdinalIgnoreCase);
            }
        }
    }
}

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
        [JsonIgnore]
        readonly bool inclName;
        [JsonIgnore]
        readonly bool inclMethods;
        [JsonIgnore]
        readonly bool onlyMethodNames;

        internal ServiceSerialized(Service desc, bool inclName = false, bool inclMethods = true, bool onlyMethodNames = false)
        {
            this.desc = desc;
            this.inclName = inclName;
            this.inclMethods = inclMethods;
            this.onlyMethodNames = onlyMethodNames;
        }

        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
        public string Name
        {
            get
            {
                if (!inclName) return null;
                return desc.Name;
            }
        }

        [JsonProperty("base", NullValueHandling = NullValueHandling.Ignore)]
        public string BaseService
        {
            get
            {
                return desc.BaseService == null ? null : desc.BaseService.Name;
            }
        }

        [JsonProperty("$", NullValueHandling = NullValueHandling.Ignore)]
        public IDictionary<string, string> Tokens
        {
            get
            {
                if (desc.Tokens == null || desc.Tokens.Count == 0) return null;
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
                if (desc.ParameterTypes == null || desc.ParameterTypes.Count == 0) return null;

                // TODO(jsd): Make the parser do copy-on-write instead of copy-on-inherit so that this will work.
                //if (desc.BaseService == null) return desc.ParameterTypes;
                //if (desc.ParameterTypes == desc.BaseService.ParameterTypes) return null;

                return desc.ParameterTypes;
            }
        }

        [JsonProperty("methods", NullValueHandling = NullValueHandling.Ignore)]
        public IDictionary<string, RestfulLink> Methods
        {
            get
            {
                if (!inclMethods) return null;

                if (onlyMethodNames)
                {
                    return desc.Methods.ToDictionary(
                        m => m.Key,
                        m => RestfulLink.Create("child", "/meta/{0}/{1}".F(desc.Name, m.Value.Name))
                    );
                }

                if (desc.Methods == null || desc.Methods.Count == 0) return null;
                return desc.Methods.ToDictionary(
                    m => m.Key,
                    m => (RestfulLink) RestfulLink.Create(
                        "child",
                        "/meta/{0}/{1}".F(desc.Name, m.Value.Name),
                        new MethodSerialized(m.Value)
                    ),
                    StringComparer.OrdinalIgnoreCase
                );
            }
        }
    }
}

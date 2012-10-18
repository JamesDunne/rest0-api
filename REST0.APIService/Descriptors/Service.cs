using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;

namespace REST0.APIService.Descriptors
{
    class ServiceCollection
    {
        public IDictionary<string, Service> Services { get; set; }
        public List<string> Errors { get; set; }
    }

    class Service
    {
        public string Name { get; set; }
        public Service BaseService { get; set; }
        public IDictionary<string, string> Tokens { get; set; }
        public string ConnectionString { get; set; }
        public IDictionary<string, ParameterType> ParameterTypes { get; set; }
        public IDictionary<string, Method> Methods { get; set; }
        public List<string> Errors { get; set; }
    }

    class ServiceMetadata
    {
        [JsonIgnore]
        protected readonly Service desc;

        internal ServiceMetadata(Service desc)
        {
            this.desc = desc;
        }

        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
        public string Name
        {
            get
            {
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

        [JsonProperty("connection", NullValueHandling = NullValueHandling.Ignore)]
        public ConnectionMetadata Connection { get { return new ConnectionMetadata(desc.ConnectionString); } }

        [JsonProperty("methodLinks", NullValueHandling = NullValueHandling.Ignore)]
        public IDictionary<string, RestfulLink> MethodLinks
        {
            get
            {
                return desc.Methods.ToDictionary(
                    m => m.Key,
                    m => RestfulLink.Create("child", "/meta/{0}/{1}".F(m.Value.Service.Name, m.Value.Name))
                );
            }
        }
    }

    class ServiceDebug
    {
        [JsonIgnore]
        readonly Service desc;

        internal ServiceDebug(Service desc)
        {
            this.desc = desc;
        }

        [JsonProperty("name", NullValueHandling = NullValueHandling.Ignore)]
        public string Name
        {
            get
            {
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

        [JsonProperty("connection", NullValueHandling = NullValueHandling.Ignore)]
        public ConnectionDebug Connection { get { return new ConnectionDebug(desc.ConnectionString); } }

        [JsonProperty("methodLinks", NullValueHandling = NullValueHandling.Ignore)]
        public RestfulLink[] MethodLinks
        {
            get
            {
                return desc.Methods.Select(p => RestfulLink.Create(p.Key, "/debug/{0}/{1}".F(p.Value.Service.Name, p.Value.Name))).ToArray();
            }
        }
    }
}

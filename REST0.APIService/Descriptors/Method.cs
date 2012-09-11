using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace REST0.APIService.Descriptors
{
    class Method
    {
        [JsonProperty("name")]
        public string Name { get; set; }
        [JsonProperty("deprecated", NullValueHandling = NullValueHandling.Ignore)]
        public string DeprecatedMessage { get; set; }
        [JsonProperty("parameters")]
        public IDictionary<string, Parameter> Parameters { get; set; }
        [JsonProperty("connection")]
        public Connection Connection { get; set; }
        [JsonProperty("query")]
        public Query Query { get; set; }

        internal Method Clone()
        {
            return new Method()
            {
                Name = this.Name,
                Parameters = new Dictionary<string, Parameter>(this.Parameters, StringComparer.OrdinalIgnoreCase),
                Connection = this.Connection,
                Query = this.Query
            };
        }
    }

    class MethodSerialized
    {
        [JsonIgnore()]
        readonly Method desc;

        internal MethodSerialized(Method desc)
        {
            this.desc = desc;
        }

        [JsonProperty("deprecated", NullValueHandling = NullValueHandling.Ignore)]
        public string DeprecatedMessage { get { return desc.DeprecatedMessage; } }
        [JsonProperty("parameters")]
        public IDictionary<string, ParameterSerialized> Parameters { get { return desc.Parameters.ToDictionary(p => p.Key, p => new ParameterSerialized(p.Value), StringComparer.OrdinalIgnoreCase); } }
        [JsonProperty("connection")]
        public string Connection { get { return desc.Connection.ConnectionString; } }
        [JsonProperty("sql", NullValueHandling = NullValueHandling.Ignore)]
        public string SQL { get { return desc.Query.SQL; } }
        [JsonProperty("errors", NullValueHandling = NullValueHandling.Ignore)]
        public List<string> Errors { get { return desc.Query.Errors; } }
    }
}

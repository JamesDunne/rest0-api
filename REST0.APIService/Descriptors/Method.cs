using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace REST0.APIService.Descriptors
{
    class Method
    {
        public Service Service { get; set; }
        public string Name { get; set; }
        public string Description { get; set; }
        public string DeprecatedMessage { get; set; }
        public IDictionary<string, Parameter> Parameters { get; set; }
        public string ConnectionString { get; set; }
        public Query Query { get; set; }
        public Dictionary<string, ColumnMapping> Mapping { get; set; }
        public List<string> Errors { get; set; }

        internal Method Clone()
        {
            return new Method()
            {
                Name = this.Name,
                Parameters = new Dictionary<string, Parameter>(this.Parameters, StringComparer.OrdinalIgnoreCase),
                ConnectionString = this.ConnectionString,
                Query = this.Query,
                // Don't clone the errors:
                Errors = new List<string>(5)
            };
        }
    }

    class MethodMetadata
    {
        [JsonIgnore]
        readonly Method desc;

        internal MethodMetadata(Method desc)
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

        [JsonProperty("description", NullValueHandling = NullValueHandling.Ignore)]
        public string Description { get { return desc.Description; } }

        [JsonProperty("deprecated", NullValueHandling = NullValueHandling.Ignore)]
        public string DeprecatedMessage { get { return desc.DeprecatedMessage; } }

        [JsonProperty("parameterTypes", NullValueHandling = NullValueHandling.Ignore)]
        public IDictionary<string, ParameterType> ParameterTypes
        {
            get
            {
                if (desc.Parameters == null || desc.Parameters.Count == 0) return null;

                // Find the set of used parameter types:
                var usedParameterTypes =
                    from pm in desc.Parameters
                    where pm.Value.Type != null
                    select pm.Value.Type;

                if (!usedParameterTypes.Any()) return null;

                return usedParameterTypes.Distinct().ToDictionary(p => p.Name);
            }
        }

        [JsonProperty("parameters", NullValueHandling = NullValueHandling.Ignore)]
        public IDictionary<string, ParameterSerialized> Parameters
        {
            get
            {
                if (desc.Parameters == null || desc.Parameters.Count == 0) return null;
                return desc.Parameters.ToDictionary(p => p.Key, p => new ParameterSerialized(p.Value), StringComparer.OrdinalIgnoreCase);
            }
        }

        [JsonProperty("connection", NullValueHandling = NullValueHandling.Ignore)]
        public ConnectionMetadata Connection { get { return new ConnectionMetadata(desc.ConnectionString); } }

        [JsonProperty("query", NullValueHandling = NullValueHandling.Ignore)]
        public Query Query { get { return desc.Query; } }
    }

    class MethodDebug
    {
        [JsonIgnore]
        readonly Method desc;

        internal MethodDebug(Method desc)
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

        [JsonProperty("description", NullValueHandling = NullValueHandling.Ignore)]
        public string Description { get { return desc.Description; } }

        [JsonProperty("deprecated", NullValueHandling = NullValueHandling.Ignore)]
        public string DeprecatedMessage { get { return desc.DeprecatedMessage; } }

        [JsonProperty("parameterTypes", NullValueHandling = NullValueHandling.Ignore)]
        public IDictionary<string, ParameterType> ParameterTypes
        {
            get
            {
                if (desc.Parameters == null || desc.Parameters.Count == 0) return null;

                // Find the set of used parameter types:
                var usedParameterTypes =
                    from pm in desc.Parameters
                    where pm.Value.Type != null
                    select pm.Value.Type;

                if (!usedParameterTypes.Any()) return null;

                return usedParameterTypes.Distinct().ToDictionary(p => p.Name);
            }
        }

        [JsonProperty("parameters", NullValueHandling = NullValueHandling.Ignore)]
        public IDictionary<string, ParameterSerialized> Parameters
        {
            get
            {
                if (desc.Parameters == null || desc.Parameters.Count == 0) return null;
                return desc.Parameters.ToDictionary(p => p.Key, p => new ParameterSerialized(p.Value), StringComparer.OrdinalIgnoreCase);
            }
        }

        [JsonProperty("connection", NullValueHandling = NullValueHandling.Ignore)]
        public ConnectionDebug Connection { get { return new ConnectionDebug(desc.ConnectionString); } }

        [JsonProperty("query", NullValueHandling = NullValueHandling.Ignore)]
        public Query Query { get { return desc.Query; } }

        [JsonProperty("sql", NullValueHandling = NullValueHandling.Ignore)]
        public string SQL
        {
            get
            {
                if (desc.Query == null)
                    return null;
                return desc.Query.SQL;
            }
        }
    }
}

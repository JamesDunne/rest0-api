using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace REST0.APIService.Descriptors
{
    class RestfulLink
    {
        readonly protected string rel;
        readonly protected string href;

        internal RestfulLink(string rel, string href)
        {
            this.rel = rel;
            this.href = href;
        }

        [JsonProperty("rel")]
        public string Rel { get { return rel; } }

        [JsonProperty("href")]
        public string Href { get { return href; } }

        internal static RestfulLink Create(string rel, string href)
        {
            return new RestfulLink(rel, href);
        }

        internal static RestfulLink<T> Create<T>(string rel, string href, T value) where T : class
        {
            return new RestfulLink<T>(rel, href, value);
        }
    }

    sealed class RestfulLink<T> : RestfulLink where T : class
    {
        readonly T value;

        internal RestfulLink(string rel, string href, T value)
            : base(rel, href)
        {
            this.value = value;
        }

        [JsonProperty("value", NullValueHandling = NullValueHandling.Include)]
        public T Value { get { return value; } }
    }
}

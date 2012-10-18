using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Newtonsoft.Json;

namespace REST0.APIService.Descriptors
{
    class ParameterType
    {
        [JsonProperty("name")]
        public string Name { get; set; }

        [JsonProperty("type")]
        public string TypeBase { get; set; }
        [JsonIgnore]
        public System.Data.SqlDbType SqlDbType { get; set; }

        [JsonProperty("length", NullValueHandling = NullValueHandling.Ignore)]
        public int? Length { get; set; }
        [JsonProperty("scale", NullValueHandling = NullValueHandling.Ignore)]
        public int? Scale { get; set; }

        [JsonProperty("description", NullValueHandling = NullValueHandling.Ignore)]
        public string Description { get; set; }

        [JsonIgnore]
        public string FullType
        {
            get
            {
                return "{0}{1}".F(
                    TypeBase,
                    // TODO: (max)
                    Length.HasValue ? "({0}{1})".F(Length.Value, Scale.HasValue ? ",{0}".F(Scale.Value) : String.Empty) : String.Empty
                );
            }
        }
    }
}

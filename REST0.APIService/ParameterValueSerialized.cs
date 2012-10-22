using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace REST0.APIService
{
    class ParameterValue
    {
        public readonly bool isValid;
        public readonly object value;
        [JsonProperty(NullValueHandling=NullValueHandling.Ignore)]
        public readonly string attemptedValue;
        [JsonProperty(NullValueHandling = NullValueHandling.Ignore)]
        public readonly string message;

        public ParameterValue(object value)
        {
            this.isValid = true;
            this.value = value;
            this.message = null;
        }

        public ParameterValue(string message, string attemptedValue)
        {
            this.isValid = false;
            this.value = null;
            this.attemptedValue = attemptedValue;
            this.message = message;
        }
    }
}

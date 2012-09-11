using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace REST0.APIService.Descriptors
{
    class Query
    {
        public Dictionary<string, string> XMLNamespaces { get; set; }
        public string WithCTEidentifier { get; set; }
        public string WithCTEexpression { get; set; }
        public string From { get; set; }
        public string Where { get; set; }
        public string Select { get; set; }
        public string GroupBy { get; set; }
        public string Having { get; set; }
        public string OrderBy { get; set; }

        /// <summary>
        /// The final composed SQL query.
        /// </summary>
        public string SQL { get; internal set; }
    }
}

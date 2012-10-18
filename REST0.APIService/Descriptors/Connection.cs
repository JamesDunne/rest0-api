using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace REST0.APIService.Descriptors
{
    class ConnectionMetadata
    {
        readonly System.Data.SqlClient.SqlConnectionStringBuilder csb;

        internal ConnectionMetadata(string connectionString)
        {
            csb = new System.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
        }

        [JsonProperty("dataSource", NullValueHandling = NullValueHandling.Ignore)]
        public string DataSource { get { return csb.DataSource.AsNullIfEmpty(); } }

        [JsonProperty("initialCatalog", NullValueHandling = NullValueHandling.Ignore)]
        public string InitialCatalog { get { return csb.InitialCatalog.AsNullIfEmpty(); } }
    }

    class ConnectionDebug
    {
        readonly System.Data.SqlClient.SqlConnectionStringBuilder csb;

        internal ConnectionDebug(string connectionString)
        {
            csb = new System.Data.SqlClient.SqlConnectionStringBuilder(connectionString);
        }

        [JsonProperty("dataSource", NullValueHandling = NullValueHandling.Ignore)]
        public string DataSource { get { return csb.DataSource.AsNullIfEmpty(); } }

        [JsonProperty("initialCatalog", NullValueHandling = NullValueHandling.Ignore)]
        public string InitialCatalog { get { return csb.InitialCatalog.AsNullIfEmpty(); } }

        [JsonProperty("userID", NullValueHandling = NullValueHandling.Ignore)]
        public string UserID { get { return csb.UserID.AsNullIfEmpty(); } }

        [JsonProperty("password", NullValueHandling = NullValueHandling.Ignore)]
        public string Password { get { return csb.Password.AsNullIfEmpty(); } }

        [JsonProperty("connectTimeout")]
        public int ConnectTimeout { get { return csb.ConnectTimeout; } }
    }
}

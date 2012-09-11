using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.Data.SqlClient;
using System.Diagnostics;
using System.Hson;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using REST0.APIService.Descriptors;
using System.Data;

#pragma warning disable 1998

namespace REST0.APIService
{
    public sealed class APIHttpAsyncHandler : IHttpAsyncHandler, IInitializationTrait, IConfigurationTrait
    {
        ConfigurationDictionary localConfig;
        SHA1Hashed<IDictionary<string, Service>> services;

        #region Handler configure and initialization

        public async Task<bool> Configure(IHttpAsyncHostHandlerContext hostContext, ConfigurationDictionary configValues)
        {
            // Configure gets called first.
            localConfig = configValues;
            return true;
        }

        /// <summary>
        /// Refresh configuration on every N-second mark on the clock.
        /// </summary>
        const int refreshInterval = 10;

        public async Task<bool> Initialize(IHttpAsyncHostHandlerContext context)
        {
            // Initialize gets called after Configure.
            if (!await RefreshConfigData())
                return false;

            // Let a background task refresh the config data every N seconds:
#pragma warning disable 4014
            Task.Run(async () =>
            {
                while (true)
                {
                    // Wait until the next even N-second mark on the clock:
                    const long secN = TimeSpan.TicksPerSecond * refreshInterval;
                    var now = DateTime.UtcNow;
                    var nextN = new DateTime(((now.Ticks + secN) / secN) * secN, DateTimeKind.Utc);
                    await Task.Delay(nextN.Subtract(now));

                    // Refresh config data:
                    await RefreshConfigData();
                }
            });
#pragma warning restore 4014

            return true;
        }

        #endregion

        #region Parsing JSON configuration

        static string getString(JProperty prop)
        {
            if (prop == null) return null;
            if (prop.Value.Type == JTokenType.Null) return null;
            return (string)((JValue)prop.Value).Value;
        }

        static bool? getBool(JProperty prop)
        {
            if (prop == null) return null;
            if (prop.Value.Type == JTokenType.Null) return null;
            return (bool?)((JValue)prop.Value).Value;
        }

        static int? getInt(JProperty prop)
        {
            if (prop == null) return null;
            if (prop.Value.Type == JTokenType.Null) return null;
            return (int?)((JValue)prop.Value).Value;
        }

        async Task<bool> RefreshConfigData()
        {
            // Get the latest config data:
            var config = await FetchConfigData();
            if (config == null) return false;

            // Parse the config document:
            var doc = config.Value;

            var tmpServices = new Dictionary<string, Service>(StringComparer.OrdinalIgnoreCase);

            // Parse the root token dictionary first:
            var rootTokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var jpTokens = doc.Property("$");
            if (jpTokens != null)
            {
                // Extract the key/value pairs onto a copy of the token dictionary:
                foreach (var prop in ((JObject)jpTokens.Value).Properties())
                    rootTokens[prop.Name] = getString(prop);
            }

            // Parse root parameter types:
            var rootParameterTypes = new Dictionary<string, ParameterType>(StringComparer.OrdinalIgnoreCase);
            var jpParameterTypes = doc.Property("parameterTypes");
            if (jpParameterTypes != null)
            {
                var errors = new List<string>(5);
                parseParameterTypes((JObject)jpParameterTypes.Value, errors, rootParameterTypes, (s) => s);
                if (errors.Count > 0)
                {
                    // TODO: what now??
                    throw new Exception();
                }
            }

            // 'services' section is not optional:
            JToken jtServices;
            if (!doc.TryGetValue("services", out jtServices))
                return false;
            var joServices = (JObject)jtServices;

            // Parse each service descriptor:
            foreach (var jpService in joServices.Properties())
            {
                if (jpService.Name == "$") continue;
                var joService = (JObject)jpService.Value;

                // This property is a service:
                var svcErrors = new List<string>(5);

                Service baseService = null;

                IDictionary<string, string> tokens;
                string connectionString;
                IDictionary<string, ParameterType> parameterTypes;
                IDictionary<string, Method> methods;

                // Go through the properties of the named service object:
                var jpBase = joService.Property("base");
                if (jpBase != null)
                {
                    // NOTE(jsd): Forward references are not allowed. Base service
                    // must be defined before the current service in document order.
                    baseService = tmpServices[getString(jpBase)];

                    // Create copies of what's inherited from the base service to mutate:
                    tokens = new Dictionary<string, string>(baseService.Tokens);
                    parameterTypes = new Dictionary<string, ParameterType>(baseService.ParameterTypes, StringComparer.OrdinalIgnoreCase);
                    methods = new Dictionary<string, Method>(baseService.Methods, StringComparer.OrdinalIgnoreCase);

                    connectionString = baseService.ConnectionString;
                }
                else
                {
                    // Nothing inherited:
                    connectionString = null;
                    tokens = new Dictionary<string, string>(rootTokens, StringComparer.OrdinalIgnoreCase);
                    parameterTypes = new Dictionary<string, ParameterType>(rootParameterTypes, StringComparer.OrdinalIgnoreCase);
                    methods = new Dictionary<string, Method>(StringComparer.OrdinalIgnoreCase);
                }

                // Parse tokens:
                jpTokens = joService.Property("$");
                if (jpTokens != null)
                {
                    // Assigning a `null`?
                    if (jpTokens.Value.Type == JTokenType.Null)
                    {
                        // Clear out all inherited tokens:
                        tokens.Clear();
                    }
                    else
                    {
                        // Extract the key/value pairs onto our token dictionary:
                        foreach (var prop in ((JObject)jpTokens.Value).Properties())
                            // NOTE(jsd): No interpolation over tokens themselves.
                            tokens[prop.Name] = getString(prop);
                    }
                }

                // A lookup-or-null function used with `Interpolate`:
                Func<string, string> tokenLookup = (key) =>
                {
                    string value;
                    // TODO: add to a Warnings collection!
                    if (!tokens.TryGetValue(key, out value))
                        return null;
                    return value;
                };

                // Parse connection:
                var jpConnection = joService.Property("connection");
                if (jpConnection != null)
                {
                    var joConnection = (JObject)jpConnection.Value;
                    connectionString = parseConnection(joConnection, svcErrors, (s) => s.Interpolate(tokenLookup));
                }

                // Parse the parameter types:
                jpParameterTypes = joService.Property("parameterTypes");
                if (jpParameterTypes != null)
                {
                    // Assigning a `null`?
                    if (jpParameterTypes.Value.Type == JTokenType.Null)
                    {
                        // Clear out all inherited parameter types:
                        parameterTypes.Clear();
                    }
                    else
                    {
                        parseParameterTypes((JObject)jpParameterTypes.Value, svcErrors, parameterTypes, (s) => s.Interpolate(tokenLookup));
                    }
                }

                // Create the service descriptor:
                Service svc = new Service()
                {
                    Name = jpService.Name,
                    BaseService = baseService,
                    ConnectionString = connectionString,
                    ParameterTypes = parameterTypes,
                    Methods = methods,
                    Tokens = tokens
                };

                // Parse the methods:
                var jpMethods = joService.Property("methods");
                if (jpMethods != null)
                {
                    var joMethods = (JObject)jpMethods.Value;

                    // Parse each method:
                    foreach (var jpMethod in joMethods.Properties())
                    {
                        // Is the method set to null?
                        if (jpMethod.Value.Type == JTokenType.Null)
                        {
                            // Remove it:
                            methods.Remove(jpMethod.Name);
                            continue;
                        }

                        var joMethod = ((JObject)jpMethod.Value);

                        // Create a clone of the inherited descriptor or a new descriptor:
                        Method method;
                        if (methods.TryGetValue(jpMethod.Name, out method))
                            method = method.Clone();
                        else
                        {
                            method = new Method()
                            {
                                Name = jpMethod.Name,
                                ParameterTypes = new Dictionary<string, ParameterType>(parameterTypes, StringComparer.OrdinalIgnoreCase),
                                ConnectionString = connectionString,
                                Errors = new List<string>(5)
                            };
                        }
                        methods[jpMethod.Name] = method;
                        method.Service = svc;

                        Debug.Assert(method.Errors != null);

                        // Parse the definition:

                        method.DeprecatedMessage = getString(joMethod.Property("deprecated")).Interpolate(tokenLookup);

                        // Parse connection:
                        jpConnection = joMethod.Property("connection");
                        if (jpConnection != null)
                        {
                            var joConnection = (JObject)jpConnection.Value;
                            connectionString = parseConnection(joConnection, method.Errors, (s) => s.Interpolate(tokenLookup));
                        }

                        // Parse parameter types:
                        jpParameterTypes = joMethod.Property("parameterTypes");
                        if (jpParameterTypes != null)
                        {
                            // Assigning a `null`?
                            if (jpParameterTypes.Value.Type == JTokenType.Null)
                            {
                                // Clear out all inherited parameter types:
                                method.ParameterTypes.Clear();
                            }
                            else
                            {
                                parseParameterTypes((JObject)jpParameterTypes.Value, method.Errors, method.ParameterTypes, (s) => s.Interpolate(tokenLookup));
                            }
                        }

                        // Parse the parameters:
                        var jpParameters = joMethod.Property("parameters");
                        if (jpParameters != null)
                        {
                            var joParameters = (JObject)jpParameters.Value;

                            // Keep track of unique SQL parameter names:
                            var sqlNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                            // Parse parameter properties:
                            method.Parameters = new Dictionary<string, Parameter>(joParameters.Count, StringComparer.OrdinalIgnoreCase);
                            foreach (var jpParam in joParameters.Properties())
                            {
                                var joParam = (JObject)jpParam.Value;
                                var sqlName = getString(joParam.Property("sqlName")).Interpolate(tokenLookup);
                                var sqlType = getString(joParam.Property("sqlType")).Interpolate(tokenLookup);
                                var typeName = getString(joParam.Property("type")).Interpolate(tokenLookup);
                                var isOptional = getBool(joParam.Property("optional")) ?? false;
                                object defaultValue = DBNull.Value;

                                // Assign a default `sqlName` if null:
                                if (sqlName == null) sqlName = "@" + jpParam.Name;
                                // TODO: validate sqlName is valid SQL parameter identifier!

                                if (sqlNames.Contains(sqlName))
                                {
                                    method.Errors.Add("Duplicate SQL parameter name (`sqlName`): '{0}'".F(sqlName));
                                }

                                var param = new Parameter()
                                {
                                    Name = jpParam.Name,
                                    SqlName = sqlName,
                                    IsOptional = isOptional
                                };

                                if (sqlType != null)
                                {
                                    int? length;
                                    int? scale;
                                    var typeBase = parseSqlType(sqlType, out length, out scale);
                                    var sqlDbType = getSqlType(typeBase);
                                    if (!sqlDbType.HasValue)
                                    {
                                        method.Errors.Add("Unknown SQL type name '{0}' for parameter '{1}'".F(typeBase, jpParam.Name));
                                    }
                                    else
                                    {
                                        param.SqlType = new ParameterType()
                                        {
                                            TypeBase = typeBase,
                                            SqlDbType = sqlDbType.Value,
                                            Length = length,
                                            Scale = scale
                                        };
                                    }
                                }
                                else
                                {
                                    ParameterType paramType;
                                    if (!parameterTypes.TryGetValue(typeName, out paramType))
                                    {
                                        method.Errors.Add("Could not find parameter type '{0}' for parameter '{1}'".F(typeName, jpParam.Name));
                                        continue;
                                    }
                                    param.Type = paramType;
                                }

                                var jpDefault = joParam.Property("default");
                                if (jpDefault != null && isOptional)
                                {
                                    // Parse the default value into a SqlValue:
                                    param.DefaultValue = jsonToSqlValue(jpDefault.Value, param.SqlType ?? param.Type);
                                }

                                method.Parameters.Add(jpParam.Name, param);
                            }
                        }

                        // Check what type of query descriptor this is:
                        var sql = getString(joMethod.Property("sql")).Interpolate(tokenLookup);
                        if (sql != null)
                        {
                            // Raw SQL query; it must be a SELECT query but we can't validate that without some nasty parsing.

                            // Remove comments from the code and trim leading and trailing whitespace:
                            sql = stripSQLComments(sql).Trim();

                            // Crude attempts to verify a SELECT form:
                            // A better approach would be to parse the root-level keywords (ignoring subqueries)
                            // and skipping WITH.
                            if (sql.StartsWith("UPDATE", StringComparison.OrdinalIgnoreCase) ||
                                sql.StartsWith("INSERT", StringComparison.OrdinalIgnoreCase) ||
                                sql.StartsWith("DELETE", StringComparison.OrdinalIgnoreCase) ||
                                sql.StartsWith("MERGE", StringComparison.OrdinalIgnoreCase) ||
                                sql.StartsWith("DROP", StringComparison.OrdinalIgnoreCase) ||
                                sql.StartsWith("CREATE", StringComparison.OrdinalIgnoreCase) ||
                                sql.StartsWith("ALTER", StringComparison.OrdinalIgnoreCase))
                            {
                                method.Errors.Add("Query must be a SELECT query.");
                            }
                            else
                            {
                                method.Query = new Query
                                {
                                    SQL = sql
                                };
                            }
                        }
                        else
                        {
                            // Parse query:
                            var jpQuery = joMethod.Property("query");
                            if (jpQuery != null)
                            {
                                var joQuery = (JObject)jpQuery.Value;
                                method.Query = new Query();

                                // Parse the separated form of a query; this ensures that a SELECT query form is constructed.

                                // 'select' is required:
                                method.Query.Select = getString(joQuery.Property("select")).Interpolate(tokenLookup);
                                if (String.IsNullOrEmpty(method.Query.Select))
                                {
                                    method.Errors.Add("A `select` clause is required");
                                }

                                // The rest are optional:
                                method.Query.From = getString(joQuery.Property("from")).Interpolate(tokenLookup);
                                method.Query.Where = getString(joQuery.Property("where")).Interpolate(tokenLookup);
                                method.Query.GroupBy = getString(joQuery.Property("groupBy")).Interpolate(tokenLookup);
                                method.Query.Having = getString(joQuery.Property("having")).Interpolate(tokenLookup);
                                method.Query.OrderBy = getString(joQuery.Property("orderBy")).Interpolate(tokenLookup);
                                method.Query.WithCTEidentifier = getString(joQuery.Property("withCTEidentifier")).Interpolate(tokenLookup);
                                method.Query.WithCTEexpression = getString(joQuery.Property("withCTEexpression")).Interpolate(tokenLookup);

                                // Parse "xmlns:prefix": "http://uri.example.org/namespace" properties for WITH XMLNAMESPACES:
                                // TODO: Are xmlns namespace prefixes case-insensitive?
                                method.Query.XMLNamespaces = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                                foreach (var jpXmlns in joQuery.Properties())
                                {
                                    if (!jpXmlns.Name.StartsWith("xmlns:")) continue;
                                    var prefix = jpXmlns.Name.Substring(6);
                                    var ns = getString(jpXmlns).Interpolate(tokenLookup);
                                    method.Query.XMLNamespaces.Add(prefix, ns);
                                }

                                // Strip out all SQL comments:
                                string withCTEidentifier = stripSQLComments(method.Query.WithCTEidentifier);
                                string withCTEexpression = stripSQLComments(method.Query.WithCTEexpression);
                                string select = stripSQLComments(method.Query.Select);
                                string from = stripSQLComments(method.Query.From);
                                string where = stripSQLComments(method.Query.Where);
                                string groupBy = stripSQLComments(method.Query.GroupBy);
                                string having = stripSQLComments(method.Query.Having);
                                string orderBy = stripSQLComments(method.Query.OrderBy);

                                // Allocate a StringBuilder with enough space to construct the query:
                                StringBuilder qb = new StringBuilder(
                                    (withCTEidentifier ?? String.Empty).Length + (withCTEexpression ?? String.Empty).Length + ";WITH  AS ()\r\n".Length
                                  + (select ?? String.Empty).Length + "SELECT ".Length
                                  + (from ?? String.Empty).Length + "\r\nFROM ".Length
                                  + (where ?? String.Empty).Length + "\r\nWHERE ".Length
                                  + (groupBy ?? String.Empty).Length + "\r\nGROUP BY ".Length
                                  + (having ?? String.Empty).Length + "\r\nHAVING ".Length
                                  + (orderBy ?? String.Empty).Length + "\r\nORDER BY ".Length
                                );

                                // This is a very conservative approach and will lead to false-positives for things like EXISTS() and sub-queries:
                                if (containsSQLkeywords(select, "from", "into", "where", "group", "having", "order", "for"))
                                    method.Errors.Add("SELECT clause cannot contain FROM, INTO, WHERE, GROUP BY, HAVING, ORDER BY, or FOR");
                                if (containsSQLkeywords(from, "where", "group", "having", "order", "for"))
                                    method.Errors.Add("FROM clause cannot contain WHERE, GROUP BY, HAVING, ORDER BY, or FOR");
                                if (containsSQLkeywords(where, "group", "having", "order", "for"))
                                    method.Errors.Add("WHERE clause cannot contain GROUP BY, HAVING, ORDER BY, or FOR");
                                if (containsSQLkeywords(groupBy, "having", "order", "for"))
                                    method.Errors.Add("GROUP BY clause cannot contain HAVING, ORDER BY, or FOR");
                                if (containsSQLkeywords(having, "order", "for"))
                                    method.Errors.Add("HAVING clause cannot contain ORDER BY or FOR");
                                if (containsSQLkeywords(orderBy, "for"))
                                    method.Errors.Add("ORDER BY clause cannot contain FOR");

                                if (method.Errors.Count == 0)
                                {
                                    // Construct the query:
                                    bool didSemi = false;
                                    if (method.Query.XMLNamespaces.Count > 0)
                                    {
                                        didSemi = true;
                                        qb.AppendLine(";WITH XMLNAMESPACES (");
                                        using (var en = method.Query.XMLNamespaces.GetEnumerator())
                                            for (int i = 0; en.MoveNext(); ++i)
                                            {
                                                var xmlns = en.Current;
                                                qb.AppendFormat("  '{0}' AS {1}", xmlns.Value.Replace("\'", "\'\'"), xmlns.Key);
                                                if (i < method.Query.XMLNamespaces.Count - 1) qb.Append(",\r\n");
                                                else qb.Append("\r\n");
                                            }
                                        qb.Append(")\r\n");
                                    }
                                    if (!String.IsNullOrEmpty(withCTEidentifier) && !String.IsNullOrEmpty(withCTEexpression))
                                    {
                                        if (!didSemi) qb.Append(';');
                                        qb.AppendFormat("WITH {0} AS (\r\n{1}\r\n)\r\n", withCTEidentifier, withCTEexpression);
                                    }
                                    qb.AppendFormat("SELECT {0}", select);
                                    if (!String.IsNullOrEmpty(from)) qb.AppendFormat("\r\nFROM {0}", from);
                                    if (!String.IsNullOrEmpty(where)) qb.AppendFormat("\r\nWHERE {0}", where);
                                    if (!String.IsNullOrEmpty(groupBy)) qb.AppendFormat("\r\nGROUP BY {0}", groupBy);
                                    if (!String.IsNullOrEmpty(having)) qb.AppendFormat("\r\nHAVING {0}", having);
                                    if (!String.IsNullOrEmpty(orderBy)) qb.AppendFormat("\r\nORDER BY {0}", orderBy);

                                    // Assign the constructed query:
                                    method.Query.SQL = qb.ToString();
                                }
                            }
                        }

                        if (method.Query == null)
                        {
                            method.Errors.Add("No query specified");
                        }
                    } // foreach (var method)
                }

                // Add the parsed service descriptor:
                tmpServices.Add(jpService.Name, svc);
            }

            // 'aliases' section is optional:
            var jpAliases = doc.Property("aliases");
            if (jpAliases != null)
            {
                // Parse the named aliases:
                var joAliases = (JObject)jpAliases.Value;
                foreach (var alias in joAliases.Properties())
                {
                    // Add the existing Service reference to the new name:
                    tmpServices.Add(alias.Name, tmpServices[getString(alias)]);
                }
            }

            // The update must boil down to an atomic reference update:
            services = new SHA1Hashed<IDictionary<string, Service>>(tmpServices, config.Hash);

            return true;
        }

        static object jsonToSqlValue(JToken jToken, ParameterType targetType)
        {
            switch (jToken.Type)
            {
                case JTokenType.String:
                    return new System.Data.SqlTypes.SqlString((string)jToken);
                case JTokenType.Boolean:
                    return new System.Data.SqlTypes.SqlBoolean((bool)jToken);
                case JTokenType.Integer:
                    switch (targetType.SqlDbType)
                    {
                        case SqlDbType.Int: return new System.Data.SqlTypes.SqlInt32((int)jToken);
                        case SqlDbType.BigInt: return new System.Data.SqlTypes.SqlInt64((long)jToken);
                        case SqlDbType.SmallInt: return new System.Data.SqlTypes.SqlInt16((short)jToken);
                        case SqlDbType.TinyInt: return new System.Data.SqlTypes.SqlByte((byte)(int)jToken);
                        case SqlDbType.Decimal: return new System.Data.SqlTypes.SqlDecimal((decimal)jToken);
                        default: return new System.Data.SqlTypes.SqlInt32((int)jToken);
                    }
                case JTokenType.Float:
                    switch (targetType.SqlDbType)
                    {
                        case SqlDbType.Float: return new System.Data.SqlTypes.SqlDouble((double)jToken);
                        case SqlDbType.Decimal: return new System.Data.SqlTypes.SqlDecimal((decimal)jToken);
                        default: return new System.Data.SqlTypes.SqlDouble((double)jToken);
                    }
                case JTokenType.Null:
                    return DBNull.Value;
                // Not really much else here to support.
                default:
                    throw new Exception("Unsupported JSON token type {0}".F(jToken.Type));
            }
        }

        static string parseConnection(JObject joConnection, List<string> errors, Func<string, string> interpolate)
        {
            var csb = new System.Data.SqlClient.SqlConnectionStringBuilder();

            try
            {
                // Set the connection properties:
                csb.DataSource = interpolate(getString(joConnection.Property("dataSource")));
                csb.InitialCatalog = interpolate(getString(joConnection.Property("initialCatalog")));

                var userID = interpolate(getString(joConnection.Property("userID")));
                if (userID != null)
                {
                    csb.IntegratedSecurity = false;
                    csb.UserID = userID;
                    csb.Password = interpolate(getString(joConnection.Property("password")));
                }
                else csb.IntegratedSecurity = true;

                // Connection pooling:
                csb.Pooling = getBool(joConnection.Property("pooling")) ?? true;
                csb.MaxPoolSize = getInt(joConnection.Property("maxPoolSize")) ?? 256;
                csb.MinPoolSize = getInt(joConnection.Property("minPoolSize")) ?? 16;

                // Default 10-second connection timeout:
                csb.ConnectTimeout = getInt(joConnection.Property("connectTimeout")) ?? 10;
                // 512 <= packetSize <= 32768
                csb.PacketSize = Math.Max(512, Math.Min(32768, getInt(joConnection.Property("packetSize")) ?? 32768));

                // We must enable async processing:
                csb.AsynchronousProcessing = true;
                csb.ApplicationIntent = ApplicationIntent.ReadOnly;

                // Finalize the connection string and return it:
                return csb.ToString();
            }
            catch (Exception ex)
            {
                errors.Add("Invalid 'connection' object: {0}".F(ex.Message));
                return null;
            }
        }

        static string parseSqlType(string type, out int? length, out int? scale)
        {
            length = null;
            scale = null;

            int idx = type.LastIndexOf('(');
            if (idx != -1)
            {
                Debug.Assert(type[type.Length - 1] == ')');

                int comma = type.LastIndexOf(',');
                if (comma == -1)
                {
                    length = Int32.Parse(type.Substring(idx + 1, type.Length - idx - 2));
                }
                else
                {
                    length = Int32.Parse(type.Substring(idx + 1, comma - idx - 1));
                    scale = Int32.Parse(type.Substring(comma + 1, type.Length - comma - 2));
                }

                type = type.Substring(0, idx);
            }

            return type;
        }

        static void parseParameterTypes(JObject joParameterTypes, List<string> errors, IDictionary<string, ParameterType> parameterTypes, Func<string, string> interpolate)
        {
            foreach (var jpParam in joParameterTypes.Properties())
            {
                // Null assignments cause removal:
                if (jpParam.Value.Type == JTokenType.Null)
                {
                    parameterTypes.Remove(jpParam.Name);
                    continue;
                }

                var jpType = ((JObject)jpParam.Value).Property("type");

                var type = interpolate(getString(jpType));
                int? length;
                int? scale;
                var typeBase = parseSqlType(type, out length, out scale).ToLowerInvariant();

                var sqlType = getSqlType(typeBase);
                if (!sqlType.HasValue)
                {
                    errors.Add("Unrecognized SQL type name '{0}'".F(typeBase));
                    continue;
                }

                parameterTypes[jpParam.Name] = new ParameterType()
                {
                    Name = jpParam.Name,
                    TypeBase = typeBase,
                    SqlDbType = sqlType.Value,
                    Length = length,
                    Scale = scale,
                };
            }
        }

        #endregion

        #region Loading configuration

        SHA1Hashed<JObject> ReadJSONStream(Stream input)
        {
            using (var hsr = new HsonReader(input, UTF8.WithoutBOM, true, 8192))
#if TRACE
            // Send the JSON to Console.Out while it's being read:
            using (var tee = new TeeTextReader(hsr, (line) => Console.Write(line)))
            using (var sha1 = new SHA1TextReader(tee, UTF8.WithoutBOM))
#else
            using (var sha1 = new SHA1TextReader(hsr, UTF8.WithoutBOM))
#endif
            using (var jr = new JsonTextReader(sha1))
            {
                var result = new SHA1Hashed<JObject>(Json.Serializer.Deserialize<JObject>(jr), sha1.GetHash());
#if TRACE
                Console.WriteLine();
                Console.WriteLine();
#endif
                return result;
            }
        }

        async Task<SHA1Hashed<JObject>> FetchConfigData()
        {
            string url, path;
            bool noConfig = true;

            // Prefer to fetch over HTTP:
            if (localConfig.TryGetSingleValue("config.Url", out url))
            {
                noConfig = false;
                //Trace.WriteLine("Getting config data via HTTP");

                // Fire off a request now to our configuration server for our config data:
                try
                {
                    var req = HttpWebRequest.CreateHttp(url);
                    using (var rsp = await req.GetResponseAsync())
                    using (var rspstr = rsp.GetResponseStream())
                        return ReadJSONStream(rspstr);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex.ToString());

                    // Fall back on loading a local file:
                    goto loadFile;
                }
            }

        loadFile:
            if (localConfig.TryGetSingleValue("config.Path", out path))
            {
                noConfig = false;
                //Trace.WriteLine("Getting config data via file");

                // Load the local JSON file:
                try
                {
                    using (var fs = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                        return ReadJSONStream(fs);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine(ex.ToString());
                    return null;
                }
            }

            // If all else fails, complain:
            if (noConfig)
                throw new Exception("Either '{0}' or '{1}' configuration keys are required".F("config.Url", "config.Path"));

            return null;
        }

        #endregion

        #region Query execution

        /// <summary>
        /// Correctly strips out all SQL comments, excluding false-positives from string literals.
        /// </summary>
        /// <param name="s"></param>
        /// <returns></returns>
        static string stripSQLComments(string s)
        {
            if (s == null) return null;

            StringBuilder sb = new StringBuilder(s.Length);
            int i = 0;
            while (i < s.Length)
            {
                if (s[i] == '\'')
                {
                    // Skip strings.
                    sb.Append('\'');

                    ++i;
                    while (i < s.Length)
                    {
                        if ((i < s.Length - 1) && (s[i] == '\'') && (s[i + 1] == '\''))
                        {
                            // Skip the escaped quote char:
                            sb.Append('\'');
                            sb.Append('\'');
                            i += 2;
                        }
                        else if (s[i] == '\'')
                        {
                            sb.Append('\'');
                            ++i;
                            break;
                        }
                        else
                        {
                            sb.Append(s[i]);
                            ++i;
                        }
                    }
                }
                else if ((i < s.Length - 1) && (s[i] == '-') && (s[i + 1] == '-'))
                {
                    // Scan up to next '\r\n':
                    i += 2;
                    while (i < s.Length)
                    {
                        if ((i < s.Length - 1) && (s[i] == '\r') && (s[i + 1] == '\n'))
                        {
                            // Leave off the parser at the newline:
                            break;
                        }
                        else if ((s[i] == '\r') || (s[i] == '\n'))
                        {
                            // Leave off the parser at the newline:
                            break;
                        }
                        else ++i;
                    }

                    // All of the line comment is now skipped.
                }
                else if ((i < s.Length - 1) && (s[i] == '/') && (s[i + 1] == '*'))
                {
                    // Scan up to next '*/':
                    i += 2;
                    while (i < s.Length)
                    {
                        if ((i < s.Length - 1) && (s[i] == '*') && (s[i + 1] == '/'))
                        {
                            // Skip the end '*/':
                            i += 2;
                            break;
                        }
                        else ++i;
                    }

                    // All of the block comment is now skipped.
                }
                else if (s[i] == ';')
                {
                    // No ';'s allowed.
                    throw new Exception("No semicolons are allowed in any query clause");
                }
                else
                {
                    // Write out the character and advance the pointer:
                    sb.Append(s[i]);
                    ++i;
                }
            }

            return sb.ToString();
        }

        /// <summary>
        /// Checks each word in a SQL fragment against the <paramref name="keywords"/> list and returns true if any match.
        /// </summary>
        /// <param name="s"></param>
        /// <param name="keywords"></param>
        /// <returns></returns>
        static bool containsSQLkeywords(string s, params string[] keywords)
        {
            if (s == null) return false;

            int rec = 0;
            int i = 0;
            int pdepth = 0;

            while (i < s.Length)
            {
                // Allow letters and underscores to pass for keywords:
                if (Char.IsLetter(s[i]) || s[i] == '_')
                {
                    if (rec == -1) rec = i;

                    ++i;
                    continue;
                }

                // Check last keyword only if at depth 0 of nested parens (this allows subqueries):
                if ((rec != -1) && (pdepth == 0))
                {
                    if (keywords.Contains(s.Substring(rec, i - rec), StringComparer.OrdinalIgnoreCase))
                        return true;
                }

                if (s[i] == '\'')
                {
                    // Process strings.

                    ++i;
                    while (i < s.Length)
                    {
                        if ((i < s.Length - 1) && (s[i] == '\'') && (s[i + 1] == '\''))
                        {
                            // Skip the escaped quote char:
                            i += 2;
                        }
                        else if (s[i] == '\'')
                        {
                            ++i;
                            break;
                        }
                        else ++i;
                    }

                    rec = -1;
                }
                else if ((s[i] == '[') || (s[i] == '"'))
                {
                    // Process quoted identifiers.

                    if (s[i] == '[')
                    {
                        // Bracket quoted identifier.
                        ++i;
                        while (i < s.Length)
                        {
                            if (s[i] == ']')
                            {
                                ++i;
                                break;
                            }
                            else ++i;
                        }
                    }
                    else if (s[i] == '"')
                    {
                        // Double-quoted identifier. Note that these are not strings.
                        ++i;
                        while (i < s.Length)
                        {
                            if ((i < s.Length - 1) && (s[i] == '"') && (s[i + 1] == '"'))
                            {
                                i += 2;
                            }
                            else if (s[i] == '"')
                            {
                                ++i;
                                break;
                            }
                            else ++i;
                        }
                    }

                    rec = -1;
                }
                else if (s[i] == ' ' || s[i] == '.' || s[i] == ',' || s[i] == '\r' || s[i] == '\n')
                {
                    rec = -1;

                    ++i;
                }
                else if (s[i] == '(')
                {
                    rec = -1;

                    ++pdepth;
                    ++i;
                }
                else if (s[i] == ')')
                {
                    rec = -1;

                    --pdepth;
                    if (pdepth < 0)
                    {
                        throw new Exception("Too many closing parentheses encountered");
                    }
                    ++i;
                }
                else if (s[i] == ';')
                {
                    // No ';'s allowed.
                    throw new Exception("No semicolons are allowed in any query clause");
                }
                else
                {
                    // Check last keyword:
                    if (rec != -1)
                    {
                        if (keywords.Contains(s.Substring(rec, i - rec), StringComparer.OrdinalIgnoreCase))
                            return true;
                    }

                    rec = -1;
                    ++i;
                }
            }

            // We must be at paren depth 0 here:
            if (pdepth > 0)
            {
                throw new Exception("{0} {1} left unclosed".F(pdepth, pdepth == 1 ? "parenthesis" : "parentheses"));
            }

            if (rec != -1)
            {
                if (keywords.Contains(s.Substring(rec, i - rec), StringComparer.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }

        static JsonResult getErrorResponse(Exception ex)
        {
            JsonResultException jex;
            JsonSerializationException jsex;
            System.Data.SqlClient.SqlException sqex;

            object innerException = null;
            if (ex.InnerException != null)
                innerException = (object)getErrorResponse(ex.InnerException);

            if ((jex = ex as JsonResultException) != null)
            {
                return new JsonResult(jex.StatusCode, jex.Message);
            }
            else if ((jsex = ex as JsonSerializationException) != null)
            {
                object errorData = new
                {
                    type = ex.GetType().FullName,
                    message = ex.Message,
                    stackTrace = ex.StackTrace,
                    innerException
                };

                return new JsonResult(500, jsex.Message, new[] { errorData });
            }
            else if ((sqex = ex as System.Data.SqlClient.SqlException) != null)
            {
                return sqlError(sqex);
            }
            else
            {
                object errorData = new
                {
                    type = ex.GetType().FullName,
                    message = ex.Message,
                    stackTrace = ex.StackTrace,
                    innerException
                };

                return new JsonResult(500, ex.Message, new[] { errorData });
            }
        }

        static JsonResult sqlError(System.Data.SqlClient.SqlException sqex)
        {
            int statusCode = 500;

            var errorData = new List<System.Data.SqlClient.SqlError>(sqex.Errors.Count);
            var msgBuilder = new StringBuilder(sqex.Message.Length);
            foreach (System.Data.SqlClient.SqlError err in sqex.Errors)
            {
                // Skip "The statement has been terminated.":
                if (err.Number == 3621) continue;

                errorData.Add(err);

                if (msgBuilder.Length > 0)
                    msgBuilder.AppendFormat("\n{0}", err.Message);
                else
                    msgBuilder.Append(err.Message);

                // Determine the HTTP status code to return:
                switch (sqex.Number)
                {
                    // Column does not allow NULLs.
                    case 515: statusCode = 400; break;
                    // Violation of UNIQUE KEY constraint '{0}'. Cannot insert duplicate key in object '{1}'.
                    case 2627: statusCode = 409; break;
                }
            }

            string message = msgBuilder.ToString();
            return new JsonResult(statusCode, message, errorData);
        }

        /// <summary>
        /// 
        /// </summary>
        /// <param name="typeBase">assumed to be lowercase</param>
        /// <returns></returns>
        static SqlDbType? getSqlType(string typeBase)
        {
            switch (typeBase)
            {
                case "bigint": return SqlDbType.BigInt;
                case "binary": return SqlDbType.Binary;
                case "bit": return SqlDbType.Bit;
                case "char": return SqlDbType.Char;
                case "date": return SqlDbType.Date;
                case "datetime": return SqlDbType.DateTime;
                case "datetime2": return SqlDbType.DateTime2;
                case "datetimeoffset": return SqlDbType.DateTimeOffset;
                case "decimal": return SqlDbType.Decimal;
                case "float": return SqlDbType.Float;
                // TODO(jsd): ???
                case "geography": return SqlDbType.VarChar;
                case "geometry": return SqlDbType.VarChar;
                case "hierarchyid": return SqlDbType.Int;
                /////////////////
                case "image": return SqlDbType.Image;
                case "int": return SqlDbType.Int;
                case "money": return SqlDbType.Money;
                case "nchar": return SqlDbType.NChar;
                case "numeric": return SqlDbType.Decimal;
                case "nvarchar": return SqlDbType.NVarChar;
                case "ntext": return SqlDbType.NText;
                case "real": return SqlDbType.Real;
                case "smalldatetime": return SqlDbType.SmallDateTime;
                case "smallint": return SqlDbType.SmallInt;
                case "smallmoney": return SqlDbType.SmallMoney;
                case "sql_variant": return SqlDbType.Variant;
                case "text": return SqlDbType.Text;
                case "time": return SqlDbType.Time;
                case "timestamp": return SqlDbType.Timestamp;
                case "tinyint": return SqlDbType.TinyInt;
                case "uniqueidentifier": return SqlDbType.UniqueIdentifier;
                case "varbinary": return SqlDbType.VarBinary;
                case "varchar": return SqlDbType.VarChar;
                case "xml": return SqlDbType.Xml;
                default: return (SqlDbType?)null;
            }
        }

        static object getSqlValue(SqlDbType sqlDbType, string value)
        {
            if (value == null) return DBNull.Value;
            if (value == "\0") return DBNull.Value;

            switch (sqlDbType)
            {
                case SqlDbType.Int: return new System.Data.SqlTypes.SqlInt32(Int32.Parse(value));
                case SqlDbType.Bit: return new System.Data.SqlTypes.SqlBoolean(Boolean.Parse(value));
                case SqlDbType.VarChar: return new System.Data.SqlTypes.SqlString(value);
                case SqlDbType.NVarChar: return new System.Data.SqlTypes.SqlString(value);
                case SqlDbType.Char: return new System.Data.SqlTypes.SqlString(value);
                case SqlDbType.NChar: return new System.Data.SqlTypes.SqlString(value);
                case SqlDbType.DateTime: return new System.Data.SqlTypes.SqlDateTime(DateTime.Parse(value));
                case SqlDbType.DateTime2: return DateTime.Parse(value);
                case SqlDbType.DateTimeOffset: return DateTimeOffset.Parse(value);
                case SqlDbType.Decimal: return new System.Data.SqlTypes.SqlDecimal(Decimal.Parse(value));
                case SqlDbType.Money: return new System.Data.SqlTypes.SqlMoney(Decimal.Parse(value));
                default: return new System.Data.SqlTypes.SqlString(value);
            }
        }

        async Task<List<Dictionary<string, object>>> ReadResult(SqlDataReader dr)
        {
            int fieldCount = dr.FieldCount;

            // TODO: check if this is superfluous.
            var header = new string[fieldCount];
            for (int i = 0; i < fieldCount; ++i)
            {
                header[i] = dr.GetName(i);
            }

            var list = new List<Dictionary<string, object>>();

            // Enumerate rows asynchronously:
            while (await dr.ReadAsync())
            {
                var result = new Dictionary<string, object>();
                Dictionary<string, object> addTo = result;

                // Enumerate columns asynchronously:
                for (int i = 0; i < fieldCount; ++i)
                {
                    object col = await dr.GetFieldValueAsync<object>(i);
                    string name = header[i];

                    if (name.StartsWith("__obj$"))
                    {
                        string objname = name.Substring(6);
                        if (String.IsNullOrEmpty(objname))
                            addTo = result;
                        else
                        {
                            if (col == DBNull.Value)
                                addTo = null;
                            else
                                addTo = new Dictionary<string, object>();
                            if (result.ContainsKey(objname))
                                throw new JsonResultException(400, "{0} key specified more than once".F(name));
                            result.Add(objname, addTo);
                        }
                        continue;
                    }

                    if (addTo == null) continue;
                    addTo.Add(name, col);
                }

                list.Add(result);
            }
            return list;
        }

        async Task<JsonResult> ExecuteQuery(HttpListenerRequest req, Method method)
        {
            if (method.Errors.Count > 0)
            {
                return new JsonResult(500, "Bad method descriptor", new
                {
                    service = method.Service.Name,
                    method = method.Name,
                    errors = method.Errors
                });
            }

            // Open a connection and execute the command:
            using (var conn = new System.Data.SqlClient.SqlConnection(method.ConnectionString))
            using (var cmd = conn.CreateCommand())
            {
                // Add parameters:
                if (method.Parameters != null)
                {
                    foreach (var param in method.Parameters)
                    {
                        bool isValid = true;
                        string message = null;
                        object sqlValue;
                        var paramType = (param.Value.SqlType ?? param.Value.Type);
                        string rawValue = req.QueryString[param.Key];

                        if (param.Value.IsOptional & (rawValue == null))
                        {
                            // Use the default value if the parameter is optional and is not specified on the query-string:
                            sqlValue = param.Value.DefaultValue;
                        }
                        else
                        {
                            try
                            {
                                sqlValue = getSqlValue(paramType.SqlDbType, rawValue);
                            }
                            catch (Exception ex)
                            {
                                isValid = false;
                                sqlValue = DBNull.Value;
                                message = ex.Message;
                            }
                        }

                        if (!isValid)
                            return new JsonResult(400, "Invalid parameter value", new
                            {
                                parameter = param.Key,
                                message
                            });

                        // Add the SQL parameter:
                        var sqlprm = cmd.Parameters.Add(param.Value.Name, paramType.SqlDbType);
                        if (paramType.Length != null) sqlprm.Precision = (byte)paramType.Length.Value;
                        if (paramType.Scale != null) sqlprm.Scale = (byte)paramType.Scale.Value;
                        sqlprm.SqlValue = sqlValue;
                    }
                }

                //cmd.CommandTimeout = 360;   // seconds
                cmd.CommandType = System.Data.CommandType.Text;
                // Set TRANSACTION ISOLATION LEVEL and optionally ROWCOUNT before the query:
                cmd.CommandText = @"SET TRANSACTION ISOLATION LEVEL READ UNCOMMITTED;" + Environment.NewLine;
                //if (rowLimit > 0)
                //    cmd.CommandText += "SET ROWCOUNT {0};".F(rowLimit) + Environment.NewLine;
                cmd.CommandText += method.Query.SQL;

                // Stopwatches used for precise timing:
                Stopwatch swOpenTime, swExecTime, swReadTime;

                swOpenTime = Stopwatch.StartNew();
                try
                {
                    // Open the connection asynchronously:
                    await conn.OpenAsync();
                    swOpenTime.Stop();
                }
                catch (Exception ex)
                {
                    swOpenTime.Stop();
                    return getErrorResponse(ex);
                }

                // Execute the query:
                SqlDataReader dr;
                swExecTime = Stopwatch.StartNew();
                try
                {
                    // Execute the query asynchronously:
                    dr = await cmd.ExecuteReaderAsync(System.Data.CommandBehavior.SequentialAccess | System.Data.CommandBehavior.CloseConnection);
                    swExecTime.Stop();
                }
                catch (ArgumentException aex)
                {
                    swExecTime.Stop();
                    // SQL Parameter validation only gives `null` for `aex.ParamName`.
                    return new JsonResult(400, aex.Message);
                }
                catch (Exception ex)
                {
                    swExecTime.Stop();
                    return getErrorResponse(ex);
                }

                swReadTime = Stopwatch.StartNew();
                try
                {
                    var result = await ReadResult(dr);
                    swReadTime.Stop();
                    var meta = new
                    {
                        service = method.Service.Name,
                        method = method.Name,
                        openMsec = Math.Round(swOpenTime.ElapsedTicks * 1000m / (decimal)Stopwatch.Frequency, 2),
                        execMsec = Math.Round(swExecTime.ElapsedTicks * 1000m / (decimal)Stopwatch.Frequency, 2),
                        readMsec = Math.Round(swReadTime.ElapsedTicks * 1000m / (decimal)Stopwatch.Frequency, 2)
                    };
                    return new JsonResult(result, meta);
                }
                catch (JsonResultException jex)
                {
                    swReadTime.Stop();
                    return new JsonResult(jex.StatusCode, jex.Message);
                }
                catch (Exception ex)
                {
                    swReadTime.Stop();
                    return getErrorResponse(ex);
                }
            }
        }

        #endregion

        #region Main handler logic

        /// <summary>
        /// Main logic.
        /// </summary>
        /// <param name="context"></param>
        /// <returns></returns>
        public async Task<IHttpResponseAction> Execute(IHttpRequestContext context)
        {
            var req = context.Request;

            // GET requests only:
            if (req.HttpMethod != "GET")
                return new JsonResult(405, "Method Not Allowed");

            // Capture the current service configuration values only once per connection in case they update during:
            var services = this.services;

            // Split the path into component parts:
            string absPath = req.Url.AbsolutePath;
            string[] path;

            if (absPath == "/") path = new string[0];
            else path = absPath.Substring(1).Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            if (path.Length == 0)
            {
                // TODO: some descriptive information here.
                return new JsonResult(new { });
            }

            if (path[0] == "meta")
            {
                if (path.Length == 1)
                {
                    // Report all service descriptors:
                    return new JsonResult(new
                    {
                        hash = services.HashHexString,
                        services = services.Value.Keys
                    });
                }

                // Look up the service name:
                string serviceName = path[1];

                Service desc;
                if (!services.Value.TryGetValue(serviceName, out desc))
                    return new JsonResult(400, "Unknown service name '{0}'".F(serviceName), new
                    {
                        service = serviceName
                    });

                if (path.Length == 2)
                {
                    // Report this service descriptor:
                    return new JsonResult(new
                    {
                        hash = services.HashHexString,
                        service = new ServiceSerialized(desc, inclName: true, onlyMethodNames: true)
                    });
                }
                if (path.Length > 3)
                {
                    return new JsonResult(400, "Too many path components supplied");
                }

                // Find method:
                string methodName = path[2];
                Method method;
                if (!desc.Methods.TryGetValue(methodName, out method))
                    return new JsonResult(400, "Unknown method name '{0}'".F(methodName), new
                    {
                        method = methodName
                    });

                // Report this method descriptor:
                return new JsonResult(new
                {
                    hash = services.HashHexString,
                    service = RestfulLink.Create("parent", "/meta/{0}".F(method.Service.Name)),
                    method = new MethodSerialized(method, inclMethodName: true)
                });
            }
            else if (path[0] == "data")
            {
                if (path.Length == 1)
                {
                    return new RedirectResponse("/meta");
                }

                // Look up the service name:
                string serviceName = path[1];

                Service desc;
                if (!services.Value.TryGetValue(serviceName, out desc))
                    return new JsonResult(400, "Unknown service name '{0}'".F(serviceName), new
                    {
                        service = serviceName
                    });

                if (path.Length == 2)
                {
                    return new RedirectResponse("/meta/{0}".F(serviceName));
                }
                if (path.Length > 3)
                {
                    return new JsonResult(400, "Too many path components supplied");
                }

                // Find method:
                string methodName = path[2];
                Method method;
                if (!desc.Methods.TryGetValue(methodName, out method))
                    return new JsonResponse(400, "Unknown method name '{0}'".F(methodName), new
                    {
                        method = methodName
                    });

                // TODO: Is it deprecated?

                // Check required parameters:
                if (method.Parameters != null)
                {
                    foreach (var param in method.Parameters)
                    {
                        if (param.Value.IsOptional) continue;
                        if (!req.QueryString.AllKeys.Contains(param.Key))
                            return new JsonResult(400, "Missing required parameter '{0}'".F(param.Key), new
                            {
                                parameter = param.Key
                            });
                    }
                }

                // Execute the query:
                var result = await ExecuteQuery(req, method);
                return result;
            }
            else
            {
                return new JsonResult(400, "Unknown request type '{0}'".F(path[0]));
            }
        }

        #endregion
    }
}

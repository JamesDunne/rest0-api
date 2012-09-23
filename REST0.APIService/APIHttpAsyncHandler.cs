using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using REST0.APIService.Descriptors;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Data;
using System.Data.SqlClient;
using System.Data.SqlTypes;
using System.Diagnostics;
using System.Hson;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Threading.Tasks;

#pragma warning disable 1998

namespace REST0.APIService
{
    public sealed class APIHttpAsyncHandler : IHttpAsyncHandler, IInitializationTrait, IConfigurationTrait
    {
        ConfigurationDictionary localConfig;
        SHA1Hashed<ServiceCollection> services;

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
        const int refreshInterval = 5;

        public async Task<bool> Initialize(IHttpAsyncHostHandlerContext context)
        {
            // Initialize gets called after Configure.
            await RefreshConfigData();

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

        async Task<bool> RefreshConfigData()
        {
            // Get the latest config data:
            SHA1Hashed<JObject> config;
            try
            {
                // TODO: add HTTP fetch first and failover to file, like we did before.
                config = await FetchConfigDataFile();
            }
            catch (Exception ex)
            {
                // Create a service collection that represents the parser error:
                this.services = SHA1Hashed.Create(
                    new ServiceCollection
                    {
                        Errors = new List<string>
                        {
                            "{0}".F(ex.Message)
                        },
                        // Empty dictionary is easier than dealing with `null`:
                        Services = new Dictionary<string, Service>(0, StringComparer.OrdinalIgnoreCase)
                    },
                    SHA1Hashed.Zero
                );

                return false;
            }

            Debug.Assert(config != null);

            // Parse the config object:
            var tmp = ParseConfigData(config.Value);
            Debug.Assert(tmp != null);

            // Store the new service collection paired with the JSON hash:
            this.services = SHA1Hashed.Create(tmp, config.Hash);
            return true;
        }

        #endregion

        #region Loading configuration

        SHA1Hashed<JObject> ReadJSONStream(TextReader input)
        {
#if TRACE
            // Send the JSON to Console.Out while it's being read:
            using (var tee = new TeeTextReader(input, (line) => Console.Write(line)))
            using (var sha1 = new SHA1TextReader(tee, UTF8.WithoutBOM))
#else
            using (var sha1 = new SHA1TextReader(input, UTF8.WithoutBOM))
#endif
            using (var jr = new JsonTextReader(sha1))
            {
                // NOTE(jsd): Relying on parameter evaluation order for `sha1.GetHash()` to be correct.
                var result = new SHA1Hashed<JObject>(Json.Serializer.Deserialize<JObject>(jr), sha1.GetHash());
#if TRACE
                Console.WriteLine();
                Console.WriteLine();
#endif
                return result;
            }
        }

        async Task<SHA1Hashed<JObject>> FetchConfigDataHTTP()
        {
            string url;

            // Prefer to fetch over HTTP:
            if (!localConfig.TryGetSingleValue("config.Url", out url))
            {
                throw new Exception("config.Url argument was not set");
            }

            // Fire off a request now to our configuration server for our config data:
            // Read only raw JSON from the HTTP response, not HSON:
            var req = HttpWebRequest.CreateHttp(url);
            using (var rsp = await req.GetResponseAsync())
            using (var rspstr = rsp.GetResponseStream())
            using (var tr = new StreamReader(rspstr))
                return ReadJSONStream(tr);
        }

        async Task<SHA1Hashed<JObject>> FetchConfigDataFile()
        {
            string path;

            if (!localConfig.TryGetSingleValue("config.Path", out path))
            {
                throw new Exception("config.Path argument was not set");
            }

            // Load the local HSON file:
            using (var hsr = new HsonReader(path, UTF8.WithoutBOM, true, 8192))
            {
                try
                {
                    return ReadJSONStream(hsr);
                }
                catch (JsonReaderException jrex)
                {
                    int targetLineNumber = jrex.LineNumber;
                    if (targetLineNumber > 0) targetLineNumber--;
                    int targetLinePosition = jrex.LinePosition;
                    if (targetLinePosition > 0) targetLinePosition--;

                    // Look up the target line/col in the HSON reader's source map:
                    var map = hsr.SourceMap;
                    var segments = map.Lines[targetLineNumber].Segments;

                    // Do a binary-search over the segments by line position:
                    int idx = Array.BinarySearch(segments, new REST0.APIService.SourceMap.Segment(targetLinePosition), REST0.APIService.SourceMap.SegmentByTargetLinePosComparer.Default);
                    if (idx < 0)
                    {
                        // Wasn't found exactly but we know where it should be.
                        idx = ~idx - 1;
                    }

                    // Pull out the error message:
                    string message = jrex.Message;
                    int lasti = jrex.Message.IndexOf(" Path '");
                    if (lasti >= 0)
                        message = message.Substring(0, lasti);

                    throw new Exception("{0} (line {1}, col {2}): {3}".F(segments[idx].SourceName, segments[idx].SourceLineNumber + 1, segments[idx].SourceLinePosition + 1, message));
                }
            }
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
            return (int?)(long?)((JValue)prop.Value).Value;
        }

        ServiceCollection ParseConfigData(JObject joConfig)
        {
            // Create the ServiceCollection that will be returned:
            var coll = new ServiceCollection()
            {
                Errors = new List<string>(5),
                Services = new Dictionary<string, Service>(StringComparer.OrdinalIgnoreCase)
            };

            // Parse the root token dictionary first:
            var rootTokens = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var jpTokens = joConfig.Property("$");
            if (jpTokens != null)
            {
                // Extract the key/value pairs onto a copy of the token dictionary:
                foreach (var prop in ((JObject)jpTokens.Value).Properties())
                {
                    // Newtonsoft.Json already guarantees that property names must be unique; it will throw a JsonReaderException in that case.
#if false
                    if (rootTokens.ContainsKey(prop.Name))
                    {
                        coll.Errors.Add("A token named '{0}' already exists in the '$' collection; cannot add a duplicate".F(prop.Name));
                        continue;
                    }
#endif
                    rootTokens[prop.Name] = getString(prop);
                }
            }

            // Parse root parameter types:
            var rootParameterTypes = new Dictionary<string, ParameterType>(StringComparer.OrdinalIgnoreCase);
            var jpParameterTypes = joConfig.Property("parameterTypes");
            if (jpParameterTypes != null)
            {
                var errors = new List<string>(5);
                parseParameterTypes(jpParameterTypes, errors, rootParameterTypes, (s) => s);
                if (errors.Count > 0)
                {
                    // Add errors encountered and keep going:
                    coll.Errors.AddRange(errors);
                }
            }

            // 'services' section is not optional:
            JToken jtServices;
            if (!joConfig.TryGetValue("services", out jtServices))
            {
                coll.Errors.Add("A 'services' section is required");
                return coll;
            }
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
                    // NOTE(jsd): Forward references are not allowed. Base service must be defined before the current service in document order.
                    string baseName = getString(jpBase);
                    if (!coll.Services.TryGetValue(baseName, out baseService))
                    {
                        coll.Errors.Add("Unknown base service name '{0}' for service '{1}'; services must declared in document order".F(baseName, jpService.Name));
                        continue;
                    }

                    // Create copies of what's inherited from the base service to mutate:
                    connectionString = baseService.ConnectionString;
                    tokens = new Dictionary<string, string>(baseService.Tokens);
                    parameterTypes = new Dictionary<string, ParameterType>(baseService.ParameterTypes, StringComparer.OrdinalIgnoreCase);
                    methods = new Dictionary<string, Method>(baseService.Methods, StringComparer.OrdinalIgnoreCase);
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
                    connectionString = parseConnection(jpConnection, svcErrors, (s) => s.Interpolate(tokenLookup));
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
                        parseParameterTypes(jpParameterTypes, svcErrors, parameterTypes, (s) => s.Interpolate(tokenLookup));
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
                    Tokens = tokens,
                    Errors = svcErrors
                };

                // Parse the methods:
                var jpMethods = joService.Property("methods");
                if (jpMethods != null)
                {
                    parseMethods(jpMethods, svc, connectionString, parameterTypes, methods, tokenLookup);
                }

                // Add the parsed service descriptor:
                coll.Services.Add(jpService.Name, svc);
            }

            // 'aliases' section is optional:
            var jpAliases = joConfig.Property("aliases");
            if (jpAliases != null)
            {
                // Parse the named aliases:
                var joAliases = (JObject)jpAliases.Value;
                foreach (var jpAlias in joAliases.Properties())
                {
                    // Add the existing Service reference to the new name:
                    string svcName = getString(jpAlias);

                    // Must find the existing service by its name first:
                    Service svcref;
                    if (!coll.Services.TryGetValue(svcName, out svcref))
                    {
                        coll.Errors.Add("Unknown service name '{0}' for alias '{1}'".F(svcName, jpAlias.Name));
                        continue;
                    }

                    // Can't add an alias name that already exists:
                    if (coll.Services.ContainsKey(jpAlias.Name))
                    {
                        coll.Errors.Add("Cannot add alias name '{0}' because that name is already in use".F(jpAlias.Name));
                        continue;
                    }

                    coll.Services.Add(jpAlias.Name, svcref);
                }
            }

            return coll;
        }

        private void parseMethods(JProperty jpMethods, Service svc, string connectionString, IDictionary<string, ParameterType> parameterTypes, IDictionary<string, Method> methods, Func<string, string> tokenLookup)
        {
            if (jpMethods.Value.Type != JTokenType.Object)
            {
                svc.Errors.Add("The `methods` property is expected to be of type object");
                return;
            }

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
                if (jpMethod.Value.Type != JTokenType.Object)
                {
                    svc.Errors.Add("The method property `{0}` is expected to be of type object".F(jpMethod.Name));
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

                method.Description = getString(joMethod.Property("description")).Interpolate(tokenLookup);
                method.DeprecatedMessage = getString(joMethod.Property("deprecated")).Interpolate(tokenLookup);

                // Parse connection:
                var jpConnection = joMethod.Property("connection");
                if (jpConnection != null)
                {
                    method.ConnectionString = parseConnection(jpConnection, method.Errors, (s) => s.Interpolate(tokenLookup));
                }

                // Parse parameter types:
                var jpParameterTypes = joMethod.Property("parameterTypes");
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
                        parseParameterTypes(jpParameterTypes, method.Errors, method.ParameterTypes, (s) => s.Interpolate(tokenLookup));
                    }
                }

                // Parse the parameters:
                var jpParameters = joMethod.Property("parameters");
                if (jpParameters != null)
                {
                    parseParameters(jpParameters, method, parameterTypes, tokenLookup);
                }

                // Parse query:
                var jpQuery = joMethod.Property("query");
                if (jpQuery != null)
                {
                    parseQuery(jpQuery, method, tokenLookup);
                }

                if (method.Query == null)
                {
                    method.Errors.Add("No query specified");
                }

                // Parse result mapping:
                var jpMapping = joMethod.Property("result");
                if (jpMapping != null)
                {
                    var joMapping = (JObject)jpMapping.Value;
                    method.Mapping = parseMapping(joMapping, method.Errors);
                }
            } // foreach (var method)
        }

        private static void parseParameters(JProperty jpParameters, Method method, IDictionary<string, ParameterType> parameterTypes, Func<string, string> tokenLookup)
        {
            if (jpParameters.Value.Type != JTokenType.Object)
            {
                method.Errors.Add("The `parameters` property is expected to be of type object");
                return;
            }
            var joParameters = (JObject)jpParameters.Value;

            // Keep track of unique SQL parameter names:
            var sqlNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Parse parameter properties:
            method.Parameters = new Dictionary<string, Parameter>(joParameters.Count, StringComparer.OrdinalIgnoreCase);
            foreach (var jpParam in joParameters.Properties())
            {
                if (jpParam.Value.Type != JTokenType.Object)
                {
                    method.Errors.Add("Parameter property '{0}' is expected to be of type object".F(jpParam.Name));
                    continue;
                }

                var joParam = (JObject)jpParam.Value;
                var sqlName = getString(joParam.Property("sqlName")).Interpolate(tokenLookup);
                var sqlType = getString(joParam.Property("sqlType")).Interpolate(tokenLookup);
                var typeName = getString(joParam.Property("type")).Interpolate(tokenLookup);
                var isOptional = getBool(joParam.Property("optional")) ?? false;
                var desc = getString(joParam.Property("description"));
                object defaultValue = DBNull.Value;

                // Assign a default `sqlName` if null:
                if (sqlName == null) sqlName = "@" + jpParam.Name;
                // TODO: validate sqlName is valid SQL parameter identifier!

                if (sqlNames.Contains(sqlName))
                {
                    method.Errors.Add("Duplicate SQL parameter name (`sqlName`): '{0}'".F(sqlName));
                    continue;
                }

                var param = new Parameter()
                {
                    Name = jpParam.Name,
                    SqlName = sqlName,
                    Description = desc,
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
                        continue;
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

                if (isOptional)
                {
                    var jpDefault = joParam.Property("default");
                    if (jpDefault != null)
                        // Parse the default value into a SqlValue:
                        param.DefaultValue = jsonToSqlValue(jpDefault.Value, param.SqlType ?? param.Type);
                    else
                        param.DefaultValue = DBNull.Value;
                }

                method.Parameters.Add(jpParam.Name, param);
            }
        }

        private static void parseQuery(JProperty jpQuery, Method method, Func<string, string> tokenLookup)
        {
            if (jpQuery.Value.Type != JTokenType.Object)
            {
                method.Errors.Add("The `query` property is expected to be an object");
                return;
            }

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
            var xmlNamespaces = new Dictionary<string, string>(StringComparer.Ordinal);
            var jpXmlns = joQuery.Property("xmlns");
            if (jpXmlns != null)
            {
                var joXmlns = (JObject)jpXmlns.Value;
                foreach (var jpNs in joXmlns.Properties())
                {
                    var prefix = jpNs.Name;
                    var ns = getString(jpNs).Interpolate(tokenLookup);
                    xmlNamespaces.Add(prefix, ns);
                }
                if (xmlNamespaces.Count > 0) method.Query.XMLNamespaces = xmlNamespaces;
            }

            string withCTEidentifier, withCTEexpression, select, from;
            string where, groupBy, having, orderBy;

            try
            {
                // Strip out all SQL comments:
                withCTEidentifier = stripSQLComments(method.Query.WithCTEidentifier);
                withCTEexpression = stripSQLComments(method.Query.WithCTEexpression);
                select = stripSQLComments(method.Query.Select);
                from = stripSQLComments(method.Query.From);
                where = stripSQLComments(method.Query.Where);
                groupBy = stripSQLComments(method.Query.GroupBy);
                having = stripSQLComments(method.Query.Having);
                orderBy = stripSQLComments(method.Query.OrderBy);
            }
            catch (Exception ex)
            {
                method.Errors.Add(ex.Message);
                return;
            }

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

            try
            {
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
            }
            catch (Exception ex)
            {
                method.Errors.Add(ex.Message);
            }

            if (method.Errors.Count != 0)
                return;

            // Construct the query:
            bool didSemi = false;
            if (xmlNamespaces.Count > 0)
            {
                didSemi = true;
                qb.AppendLine(";WITH XMLNAMESPACES (");
                using (var en = xmlNamespaces.GetEnumerator())
                    for (int i = 0; en.MoveNext(); ++i)
                    {
                        var xmlns = en.Current;
                        qb.AppendFormat("  '{0}' AS {1}", xmlns.Value.Replace("\'", "\'\'"), xmlns.Key);
                        if (i < xmlNamespaces.Count - 1) qb.Append(",\r\n");
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

        private Dictionary<string, ColumnMapping> parseMapping(JObject joMapping, List<string> errors)
        {
            var mapping = new Dictionary<string, ColumnMapping>();
            foreach (var prop in joMapping.Properties())
            {
                if (prop.Value.Type == JTokenType.Object)
                {
                    mapping.Add(prop.Name, new ColumnMapping(parseMapping((JObject)prop.Value, errors)));
                }
                else if (prop.Value.Type == JTokenType.String)
                {
                    string value = getString(prop);
                    // Parse "columnName`instanceNumber" for resolving duplicate column names.
                    int idx = value.LastIndexOf('`');
                    if (idx != -1)
                    {
                        string colName = value.Substring(0, idx);
                        string colInst = value.Substring(idx + 1);
                        mapping.Add(prop.Name, new ColumnMapping(colName, Int32.Parse(colInst) - 1));
                    }
                    else
                    {
                        mapping.Add(prop.Name, new ColumnMapping(value));
                    }
                }
                else errors.Add("Unhandled token type {0} for mapping property '{1}'".F(prop.Value.Type, prop.Name));
            }
            return mapping;
        }

        static object jsonToSqlValue(JToken jToken, ParameterType targetType)
        {
            switch (jToken.Type)
            {
                case JTokenType.String: return new SqlString((string)jToken);
                case JTokenType.Boolean: return new SqlBoolean((bool)jToken);
                case JTokenType.Integer:
                    switch (targetType.SqlDbType)
                    {
                        case SqlDbType.Int: return new SqlInt32((int)jToken);
                        case SqlDbType.BigInt: return new SqlInt64((long)jToken);
                        case SqlDbType.SmallInt: return new SqlInt16((short)jToken);
                        case SqlDbType.TinyInt: return new SqlByte((byte)(int)jToken);
                        case SqlDbType.Decimal: return new SqlDecimal((decimal)jToken);
                        default: return new SqlInt32((int)jToken);
                    }
                case JTokenType.Float:
                    switch (targetType.SqlDbType)
                    {
                        case SqlDbType.Float: return new SqlDouble((double)jToken);
                        case SqlDbType.Decimal: return new SqlDecimal((decimal)jToken);
                        default: return new SqlDouble((double)jToken);
                    }
                case JTokenType.Null: return DBNull.Value;
                // Not really much else here to support.
                default:
                    throw new Exception("Unsupported JSON token type {0}".F(jToken.Type));
            }
        }

        static string parseConnection(JProperty jpConnection, List<string> errors, Func<string, string> interpolate)
        {
            if (jpConnection.Value.Type != JTokenType.Object)
            {
                errors.Add("The `connection` property is expected to be of type object");
                return null;
            }

            var joConnection = (JObject)jpConnection.Value;

            var csb = new SqlConnectionStringBuilder();

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

                string strlength;

                if (comma == -1)
                    strlength = type.Substring(idx + 1, type.Length - idx - 2).Trim();
                else
                    strlength = type.Substring(idx + 1, comma - idx - 1).Trim();

                if (strlength.Equals("max", StringComparison.OrdinalIgnoreCase))
                {
                    // TODO: ?
                    length = -1;
                }
                else
                {
                    length = Int32.Parse(strlength);
                }

                if (comma != -1)
                {
                    scale = Int32.Parse(type.Substring(comma + 1, type.Length - comma - 2));
                }

                type = type.Substring(0, idx);
            }

            return type;
        }

        static void parseParameterTypes(JProperty jpParameterTypes, List<string> errors, IDictionary<string, ParameterType> parameterTypes, Func<string, string> interpolate)
        {
            if (jpParameterTypes.Value.Type != JTokenType.Object)
            {
                errors.Add("The `parameterTypes` property is expected to be of type object");
                return;
            }
            var joParameterTypes = (JObject)jpParameterTypes.Value;

            foreach (var jpParam in joParameterTypes.Properties())
            {
                // Null assignments cause removal:
                if (jpParam.Value.Type == JTokenType.Null)
                {
                    parameterTypes.Remove(jpParam.Name);
                    continue;
                }

                if (jpParam.Value.Type != JTokenType.Object)
                {
                    errors.Add("ParameterType property '{0}' is expected to be of type object".F(jpParam.Name));
                    continue;
                }

                var joParam = ((JObject)jpParam.Value);

                var jpType = joParam.Property("type");
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

                var jpDesc = joParam.Property("description");
                var desc = interpolate(getString(jpDesc));

                parameterTypes[jpParam.Name] = new ParameterType()
                {
                    Name = jpParam.Name,
                    Description = desc,
                    TypeBase = typeBase,
                    SqlDbType = sqlType.Value,
                    Length = length,
                    Scale = scale,
                };
            }
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
                else if (s[i] == '*')
                {
                    // No '*'s allowed.
                    throw new Exception("No asterisks are allowed in any query clause");
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
                else if (s[i] == '*')
                {
                    // No '*'s allowed.
                    throw new Exception("No asterisks are allowed in any query clause");
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
            SqlException sqex;

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
            else if ((sqex = ex as SqlException) != null)
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

        static JsonResult sqlError(SqlException sqex)
        {
            int statusCode = 500;

            var errorData = new List<SqlError>(sqex.Errors.Count);
            var msgBuilder = new StringBuilder(sqex.Message.Length);
            foreach (SqlError err in sqex.Errors)
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
                case SqlDbType.BigInt: return new SqlInt64(Int64.Parse(value));
                case SqlDbType.Binary: return new SqlBinary(Convert.FromBase64String(value));
                case SqlDbType.Bit: return new SqlBoolean(Boolean.Parse(value));
                case SqlDbType.Char: return new SqlString(value);
                case SqlDbType.Date: return new SqlDateTime(DateTime.Parse(value));
                case SqlDbType.DateTime:
                case SqlDbType.DateTime2: return SqlDateTime.Parse(value);
                case SqlDbType.DateTimeOffset: return DateTimeOffset.Parse(value);
                case SqlDbType.Decimal: return new SqlDecimal(Decimal.Parse(value));
                case SqlDbType.Float: return new SqlDouble(Double.Parse(value));
                case SqlDbType.Image: return new SqlBinary(Convert.FromBase64String(value));
                case SqlDbType.Int: return new SqlInt32(Int32.Parse(value));
                case SqlDbType.Money: return new SqlMoney(Decimal.Parse(value));
                case SqlDbType.NChar: return new SqlString(value);
                case SqlDbType.NVarChar: return new SqlString(value);
                case SqlDbType.NText: return new SqlString(value);
                case SqlDbType.Real: return new SqlDouble(Double.Parse(value));
                case SqlDbType.SmallDateTime: return new SqlDateTime(DateTime.Parse(value));
                case SqlDbType.SmallInt: return new SqlInt16(Int16.Parse(value));
                case SqlDbType.SmallMoney: return new SqlDecimal(Decimal.Parse(value));
                //case SqlDbType.Variant: return new (Double.Parse(value));
                case SqlDbType.Text: return new SqlString(value);
                case SqlDbType.Time: return DateTime.Parse(value);
                case SqlDbType.Timestamp: return Convert.FromBase64String(value);
                case SqlDbType.TinyInt: return new SqlByte(Byte.Parse(value));
                case SqlDbType.UniqueIdentifier: return new Guid(value);
                case SqlDbType.VarBinary: return new SqlBinary(Convert.FromBase64String(value));
                case SqlDbType.VarChar: return new SqlString(value);
                case SqlDbType.Xml:
                    // NOTE(jsd): SqlXml's ctor copies the stream contents into memory.
                    using (var sr = new StringReader(value))
                    using (var xr = new System.Xml.XmlTextReader(sr))
                        return new SqlXml(xr);
                default: return null;
            }
        }

        /// <summary>
        /// Represents a row mapping implementation.
        /// </summary>
        /// <param name="names"></param>
        /// <param name="ordinals"></param>
        /// <param name="method"></param>
        /// <param name="columns"></param>
        /// <returns></returns>
        delegate Dictionary<string, object> RowMapperDelegate(Method method, string[] names, ILookup<string, int> ordinals, object[] values);

        /// <summary>
        /// Parses column names for '{' and '}' which are used to indicate nested object mapping.
        /// </summary>
        /// <param name="names"></param>
        /// <param name="ordinals"></param>
        /// <param name="method"></param>
        /// <param name="values"></param>
        /// <returns></returns>
        Dictionary<string, object> RowMapperCurlyInflate(Method method, string[] names, ILookup<string, int> ordinals, object[] values)
        {
            var objStack = new Stack<Dictionary<string, object>>(3);
            var result = new Dictionary<string, object>();
            var addTo = result;

            // Enumerate columns asynchronously:
            for (int i = 0; i < values.Length; ++i)
            {
                object col = values[i];
                string name = names[i];

                // Opening or closing a sub-object?
                if (name.StartsWith("{") || name.StartsWith("}"))
                {
                    int n = 0;
                    while (n < name.Length)
                    {
                        // Allow any number of leading close-curlies:
                        if (name[n] == '}')
                        {
                            addTo = objStack.Pop();
                            ++n;
                            continue;
                        }

                        // Only one open-curly allowed at the end:
                        if (name[n] == '{')
                        {
                            var curr = addTo;
                            objStack.Push(addTo);
                            if (curr == null) break;

                            string objname = name.Substring(n + 1);

                            if (col == DBNull.Value)
                                addTo = null;
                            else
                                addTo = new Dictionary<string, object>();

                            if (curr.ContainsKey(objname))
                                throw new JsonResultException(500, "{0} key specified more than once".F(name));
                            curr.Add(objname, addTo);
                        }
                        break;
                    }

                    continue;
                }

                if (addTo == null) continue;
                addTo.Add(name, col);
            }

            if (objStack.Count != 0)
                throw new JsonResultException(500, "Too many open curlies in column list: {0}".F(objStack.Count));

            return result;
        }

        Dictionary<string, object> mapColumns(Dictionary<string, ColumnMapping> mapping, ILookup<string, int> ordinals, object[] values)
        {
            int count = mapping.Count;

            ColumnMapping exists;
            if (mapping.TryGetValue("<exists>", out exists))
            {
                --count;
                if (values[ordinals[exists.Name].ElementAt(exists.Instance)] == DBNull.Value)
                    return null;
            }

            // Create a dictionary to hold this JSON sub-object:
            var result = new Dictionary<string, object>(count, StringComparer.OrdinalIgnoreCase);
            foreach (var prop in mapping)
            {
                if (prop.Key == "<exists>") continue;
                object value;
                // Recursively map columns:
                if (prop.Value.Columns != null)
                    value = mapColumns(prop.Value.Columns, ordinals, values);
                else
                    value = values[ordinals[prop.Value.Name].ElementAt(prop.Value.Instance)];
                result.Add(prop.Key, value);
            }
            return result;
        }

        /// <summary>
        /// Maps columns from the result set using the mapping schema defined in the method descriptor.
        /// </summary>
        /// <param name="names"></param>
        /// <param name="ordinals"></param>
        /// <param name="method"></param>
        /// <param name="values"></param>
        /// <returns></returns>
        Dictionary<string, object> RowMapperUseMapping(Method method, string[] names, ILookup<string, int> ordinals, object[] values)
        {
            // No custom mapping?
            if (method.Mapping == null)
            {
                var result = new Dictionary<string, object>();

                // Use a default mapping:
                for (int i = 0; i < names.Length; ++i)
                {
                    if (result.ContainsKey(names[i]))
                    {
                        // TODO: add a warning about duplicate column names.
                        continue;
                    }
                    result.Add(names[i], values[i]);
                }

                return result;
            }

            // We have a custom mapping:
            return mapColumns(method.Mapping, ordinals, values);
        }

        /// <summary>
        /// Reads the entire SqlDataReader asynchronously and returns the entire list of row objects when complete.
        /// </summary>
        /// <param name="method"></param>
        /// <param name="dr"></param>
        /// <param name="rowMapper"></param>
        /// <returns></returns>
        async Task<List<Dictionary<string, object>>> ReadResult(Method method, SqlDataReader dr, RowMapperDelegate rowMapper)
        {
            int fieldCount = dr.FieldCount;

            var names = new string[fieldCount];
            var columns = new object[fieldCount];
            for (int i = 0; i < fieldCount; ++i)
            {
                names[i] = dr.GetName(i);
            }

            var nameLookup = (
                from i in Enumerable.Range(0, fieldCount)
                select new { i, name = dr.GetName(i) }
            ).ToLookup(p => p.name, p => p.i);

            var list = new List<Dictionary<string, object>>();

            // Enumerate rows asynchronously:
            while (await dr.ReadAsync())
            {
                // Enumerate columns asynchronously:
                for (int i = 0; i < fieldCount; ++i)
                {
                    columns[i] = await dr.GetFieldValueAsync<object>(i);
                }

                // Map all the columns to a single object:
                var result = rowMapper(method, names, nameLookup, columns);
                list.Add(result);
            }
            return list;
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
            var main = this.services;
            var services = main.Value.Services;

            // Not getting any further than this with severe errors:
            if (main.Value.Errors.Count > 0)
            {
                return new JsonResult(500, "Severe errors encountered", new
                {
                    errors = main.Value.Errors
                });
            }

            // Split the path into component parts:
            string[] path;
            string absPath = req.Url.AbsolutePath;
            if (absPath == "/") path = new string[0];
            else path = absPath.Substring(1).Split(new char[] { '/' }, StringSplitOptions.RemoveEmptyEntries);

            if (path.Length == 0)
            {
                // TODO: some descriptive information here.
                return new JsonResult(new
                {
                    configHash = main.HashHexString,
                    links = new RestfulLink[]
                    {
                        RestfulLink.Create("meta", "/errors"),
                        RestfulLink.Create("meta", "/meta")
                    }
                });
            }

            string action = path[0];

            // Requests are always of one of these forms:
            //  * /{action}
            //  * /{action}/{service}
            //  * /{action}/{service}/{method}

            if (path.Length == 1)
            {
                if (action == "meta")
                {
                    // `/meta` request
                    // Report all service descriptors:
                    return metaAll(main);
                }
                else if (action == "data")
                {
                    return new RedirectResponse("/meta");
                }
                else if (action == "errors")
                {
                    return errorsAll(main);
                }
                else
                {
                    return new JsonResult(400, "Unknown request type '{0}'".F(action));
                }
            }

            // Look up the service name:
            string serviceName = path[1];

            Service service;
            if (!main.Value.Services.TryGetValue(serviceName, out service))
                return new JsonResult(400, "Unknown service name '{0}'".F(serviceName), new
                {
                    service = serviceName
                });

            if (path.Length == 2)
            {
                if (action == "meta")
                {
                    // Report this service descriptor:
                    return metaService(main, service);
                }
                else if (action == "data")
                {
                    return new RedirectResponse("/meta/{0}".F(serviceName));
                }
                else if (action == "errors")
                {
                    // Report errors encountered while building a specific service descriptor.
                    return errorsService(main, service);
                }
                else
                {
                    return new JsonResult(400, "Unknown request type '{0}'".F(action));
                }
            }
            if (path.Length > 3)
            {
                return new JsonResult(400, "Too many path components supplied");
            }

            // Find method:
            string methodName = path[2];
            Method method;
            if (!service.Methods.TryGetValue(methodName, out method))
                return new JsonResult(400, "Unknown method name '{0}'".F(methodName), new
                {
                    method = methodName
                });

            if (action == "meta")
            {
                // Report this method descriptor:
                return metaMethod(main, method);
            }
            else if (action == "data")
            {
                // Await execution of the method and return the results:
                var result = await dataMethod(main, method, req.QueryString);
                return result;
            }
            else if (action == "errors")
            {
                // Report errors encountered while building a specific method:
                return errorsMethod(main, service, method);
            }
            else
            {
                return new JsonResult(400, "Unknown request type '{0}'".F(action));
            }
        }

        IHttpResponseAction metaAll(SHA1Hashed<ServiceCollection> main)
        {
            // Report all service descriptors:
            return new JsonResult(new
            {
                configHash = main.HashHexString,
                errors = main.Value.Errors,
                serviceLinks = main.Value.Services.ToDictionary(
                    s => s.Key,
                    s => RestfulLink.Create("child", "/meta/{0}".F(s.Value.Name)),
                    StringComparer.OrdinalIgnoreCase
                )
            });
        }

        IHttpResponseAction metaService(SHA1Hashed<ServiceCollection> main, Service svc)
        {
            return new JsonResult(new
            {
                configHash = main.HashHexString,
                service = new ServiceSerialized(svc)
            });
        }

        IHttpResponseAction metaMethod(SHA1Hashed<ServiceCollection> main, Method method)
        {
            return new JsonResult(new
            {
                configHash = main.HashHexString,
                serviceLink = RestfulLink.Create("parent", "/meta/{0}".F(method.Service.Name)),
                selfLink = RestfulLink.Create("meta", "/meta/{0}/{1}".F(method.Service.Name, method.Name)),
                dataLink = RestfulLink.Create("data", "/data/{0}/{1}".F(method.Service.Name, method.Name)),
                method = new MethodSerialized(method)
            });
        }

        IHttpResponseAction errorsMethod(SHA1Hashed<ServiceCollection> main, Service desc, Method method)
        {
            return new JsonResult(new
            {
                configHash = main.HashHexString,
                service = desc.Name,
                method = method.Name,
                errors = method.Errors
            });
        }

        IHttpResponseAction errorsService(SHA1Hashed<ServiceCollection> main, Service desc)
        {
            return new JsonResult(new
            {
                configHash = main.HashHexString,
                service = desc.Name,
                errors = desc.Errors,
                methods = desc.Methods.Where(m => m.Value.Errors.Any()).ToDictionary(
                    m => m.Key,
                    m => new
                    {
                        errors = m.Value.Errors
                    },
                    StringComparer.OrdinalIgnoreCase
                )
            });
        }

        IHttpResponseAction errorsAll(SHA1Hashed<ServiceCollection> main)
        {
            return new JsonResult(new
            {
                configHash = main.HashHexString,
                errors = main.Value.Errors,
                services =
                    main.Value.Services.Where(s => s.Value.Errors.Any() || s.Value.Methods.Any(m => m.Value.Errors.Any())).ToDictionary(
                        s => s.Key,
                        s => new
                        {
                            errors = s.Value.Errors,
                            methods = s.Value.Methods.Where(m => m.Value.Errors.Any()).ToDictionary(
                                m => m.Key,
                                m => new
                                {
                                    errors = m.Value.Errors
                                },
                                StringComparer.OrdinalIgnoreCase
                            )
                        },
                        StringComparer.OrdinalIgnoreCase
                    )
            });
        }

        async Task<IHttpResponseAction> dataMethod(SHA1Hashed<ServiceCollection> main, Method method, System.Collections.Specialized.NameValueCollection queryString)
        {
            // Check for descriptor errors:
            if (method.Errors.Count > 0)
            {
                return new JsonResult(500, "Bad method descriptor", new
                {
                    configHash = main.HashHexString,
                    service = method.Service.Name,
                    method = method.Name,
                    errors = method.Errors
                });
            }

            // Check required parameters:
            if (method.Parameters != null)
            {
                // Create a hash set of the query-string parameter names:
                var q = new HashSet<string>(queryString.AllKeys, StringComparer.OrdinalIgnoreCase);

                // Create a list of missing required parameter names:
                var missingParams = new List<string>(method.Parameters.Count(p => !p.Value.IsOptional));
                missingParams.AddRange(
                    from p in method.Parameters
                    where !p.Value.IsOptional && !q.Contains(p.Key)
                    select p.Key
                );

                if (missingParams.Count > 0)
                    return new JsonResult(400, "Missing required parameters", new
                    {
                        parameters = missingParams.ToDictionary(
                            p => p,
                            p => new ParameterSerialized(method.Parameters[p]),
                            StringComparer.OrdinalIgnoreCase
                        )
                    });

                missingParams = null;
            }

            // Open a connection and execute the command:
            using (var conn = new SqlConnection(method.ConnectionString))
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
                        string rawValue = queryString[param.Key];

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
                                if (sqlValue == null)
                                {
                                    isValid = false;
                                    message = "Unsupported SQL type '{0}'".F(paramType.SqlDbType);
                                }
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
                        sqlprm.IsNullable = param.Value.IsOptional;
                        if (paramType.Length != null) sqlprm.Precision = (byte)paramType.Length.Value;
                        if (paramType.Scale != null) sqlprm.Scale = (byte)paramType.Scale.Value;
                        sqlprm.SqlValue = sqlValue;
                    }
                }

                //cmd.CommandTimeout = 360;   // seconds
                cmd.CommandType = CommandType.Text;
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
                    dr = await cmd.ExecuteReaderAsync(CommandBehavior.SequentialAccess | CommandBehavior.CloseConnection);
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
                    var result = await ReadResult(method, dr, RowMapperUseMapping);
                    swReadTime.Stop();

                    var meta = new MetadataSerialized
                    {
                        configHash = main.HashHexString,
                        service = method.Service.Name,
                        method = method.Name,
                        deprecated = method.DeprecatedMessage,
                        timings = new MetadataTimingsSerialized
                        {
                            open = Math.Round(swOpenTime.ElapsedTicks * 1000m / (decimal)Stopwatch.Frequency, 2),
                            exec = Math.Round(swExecTime.ElapsedTicks * 1000m / (decimal)Stopwatch.Frequency, 2),
                            read = Math.Round(swReadTime.ElapsedTicks * 1000m / (decimal)Stopwatch.Frequency, 2),
                            total = Math.Round((swOpenTime.ElapsedTicks + swExecTime.ElapsedTicks + swReadTime.ElapsedTicks) * 1000m / (decimal)Stopwatch.Frequency, 2),
                        }
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
    }
}

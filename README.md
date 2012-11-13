REST0-API
=========

This project allows .NET developers to declaratively construct a RESTful web service which directly executes SQL queries
against an MS SQL Server and returns their results as JSON.

For the service developer, there is no boilerplate C# or VB coding required. All you have to do is declare services and
methods and the SQL queries for those methods inside a simple descriptor document.

This project is based on my Aardwolf framework found at https://github.com/JamesDunne/aardwolf. This framework provides the
asynchronous HTTP service host which efficiently serves requests dynamically.

Example
-------

Let's take a look at a very simple example data request to get a feel for what the project does:

**Request:**

`GET /data/sis;core/GetStudent?id=1`

**Response:**

```javascript
{
  "results": [
    {
      "StudentID": 1,
      "FirstName": "James",
      "LastName": "Dunne"
    }
  ]
}
```

Let's break down this example URL:

 * `/data` means this is a data request.
 * `/sis;core` indicates the service's full name. Service name components are separated with semicolons to allow
   for namespacing and versioning. Aliases may also be set up to allow bleeding-edge clients to always work
   against the latest version of a service. In our example, `"sis;core"` is an alias for `"sis;core;v5"`, the
   latest version defined.
 * `/GetStudent` is the method name selected from the service to execute
 * `?id=1` is a query-string parameter

Requests
--------

The absolute path of a request URL is always made of at most 3 parts:

 1. request type (`meta`, `errors`, or `data`)
 2. service name
 3. method name

There are several kinds of request types available to make:

  * `data`: The most common kind of request which executes methods and gets data back from a database.
    * All 3 request parts (type, service, method) are required for a data request, otherwise a 302 redirect is
      issued to the corresponding `meta` request URL.
    * Any required parameters missing from the query-string parameter will cause a 400 Bad Request response with
      a list of all the missing parameters that are required.
    * To set a required parameter's value to NULL, use the special value `%00` in the query-string (see the
      `parameters` section below for more details).
  * `meta`: A request which yields detailed metadata for services and methods.
    * This request type can be made against the global level, the service level, or the method level.
    * A meta request against the global level will yield a list of named service links, including aliases.
    * A meta request against the service level will yield the service's details and a list of named method links.
    * A meta request against the method level will yield detailed information about the method, its parameters, and
      its query.
  * `errors`: A request which yields all error messages encountered while processing the service declaration file.
    * This is mostly to aid the service developer in diagnosing common failures.
    * A well-defined service descriptor file should produce no error messages.
    * It is strongly encouraged to resolve all error messages before publishing a service for consumers to use.
    * An errors request against the global level will yield a recursive list of all errors encountered across all
      services and their methods.
    * An errors request against the service level will yield a recursive list of all errors encountered over the
      service's methods.
    * An errors request against the method level will yield a list of all errors encountered for that method.

Services
--------

When the service host application first starts up, it fetches a service descriptor file (either via HTTP or via a local file)
and parses it to fully constructs all services and methods. Once the descriptor is parsed, the service is ready to process
HTTP requests. The service also sets up a background task to frequently fetch new versions of the service descriptor file so
that it can run unattended and keep itself up-to-date.

The declarative approach is achieved through the use of a human-readable JSON file format which I call HSON. HSON includes
nice features on top of JSON such as comments (both `//` and `/* */` kind) and multi-line string literals (e.g.
`@"Hello, ""world""."`). HSON also allows one to import partial HSON documents directly into the current HSON document
(recursively, of course) using the `@import("path")` directive. This import functionality allows for a more maintainable
service descriptor which can be split across many files.

Let's take a look at the simplest example of how a service developer would define a single service and a single method
in an HSON document:

```javascript
{
  "services": {             // services dictionary
    "sis;core;v1": {        // service name
      "connection": {       // connection string
        "dataSource":       "(local)\\SQLEXPRESS",   // or use @"(local)\SQLEXPRESS" to avoid backslash escaping
        "initialCatalog":   "APITest"
      },
      "methods": {          // service methods dictionary
        "GetStudent": {     // method name
          "query": {        // SQL Query
            "from":     "vw_Student st",
            "select":   "st.StudentID, st.FirstName, st.LastName"
          }
        }
      }
    }
  }
}
```

First, we see the `services` section. This section is a dictionary of unique service names with a **service descriptor** assigned
to each name. A service descriptor defines, among other things, all the methods that belong to that service.

At the next level, we see `sis;core;v1` as a property key. This is a unique name for a service and its value is that
service's service descriptor.

Semicolons are used in service names because they are URL-friendly. The convention is a three-part name beginning with the
general category of service type followed by a specific variant name and finally a version identifier. The service developer
is free to abandon this convention but it is strongly recommended to at least include a version identifier in the service
names.

The service descriptor in this example defines only two properties, `connection` and `methods`.

The `connection` property is used to describe the default database connection string used for all methods of this service. An
individual method may provide its own `connection` property to override what's defined at the service level here. The minimum
requirements for a `connection` are the `dataSource` and `initialCatalog` properties. Set these to the server name (and
instance) and the database name, respectively. You can see here that we're referencing a local SQLEXPRESS instance and a
database named "APITest". This sample database is included with the project and you can attach it to your
local SQLEXPRESS instance.

Methods
-------

Continuing from the example above...

```javascript
"methods": {          // service methods dictionary
  "GetStudent": {     // method name
    "query": {        // SQL Query
      "from":     "vw_Student st",
      "select":   "st.StudentID, st.FirstName, st.LastName"
    }
  }
}
```

Next, we come to the `methods` section. This is the main section of interest here. It is a dictionary of unique method
names with a **method descriptor** assigned to each name.

The method descriptor describes, among other things, the SQL query used to implement the method. This is the `query`
property.

There are several elements of a method descriptor:

  * `description`: (optional) gives a description of the method.
  * `parameters`: (optional) defines a mapping of query-string parameter names to their SQL parameter counterparts.
  * `parameterTypes`: (optional) defines method-specific parameter types.
  * `connection`: (optional) override the service-level DB connection.
  * `deprecated`: (optional) a message to display to consumers of the method indicating its deprecation.
  * `query`: defines the parts of the SQL SELECT query to be executed.
  * `result`: (optional) defines a custom per-row object mapping

Queries
-------

The `query` property is interesting here in that is *not* one complete SELECT query in raw text form, as one might first
expect. Rather, it is an object with properties each describing the individual clauses of a single SELECT query.

This query clause separation is beneficial for many reasons. Firstly, it guarantees that one cannot write a query that is
*not* an actual SELECT query. This is very useful for ensuring that the service will only read data and never mutate data.
Secondly, it provides a way to rearrange the clauses of a SQL query in a way that is more readable and possibly more amenable
to a logical thought process in forming a query. Finally, individual clauses of the query, such as `orderBy` may be used to
drive the implementation of an automatic data paging mechanism. Having the clauses already broken out without having to
parse the SQL makes it easy to implement such advanced features.

The final query is constructed as follows from the `query` object's properties:

    [WITH XMLNAMESPACES (...)]
    [WITH `withCTEidentifier` AS (`withCTEexpression`)]
    SELECT `select`
    [FROM `from`]
    [WHERE `where`]
    [GROUP BY `groupBy`]
    [HAVING `having`]
    [ORDER BY `orderBy`]

Hopefully this break-down makes it clear as to where each property of the `query` object goes in the constructed SELECT query.
The only property absolutely required of the `query` object is the `select` property.

`WITH XMLNAMESPACES` is added to support SQL-XML queries. Adding a sub-object property named `xmlns` with key/value
pairs representing XML namespace prefix assignments to XML namespace URIs will build out this query clause. For
example, if we have `"xmlns": {"sis": "http://example.org/sis/v1"}` then the resulting `WITH XMLNAMESPACES` clause will
be this:

```sql
    WITH XMLNAMESPACES (
        'http://example.org/sis/v1' AS sis
    )
```

*NOTE:* the asterisk (`*`) shortcut is disallowed in `select` clauses because it obscures the actual column list being
selected and may possibly introduce inconsistencies in versioning queries.

JSON result mapping
-------------------

All query results are serialized to an array of JSON objects. Each JSON object consitutes a single row of data. By
default, a simple column-to-property mapping is generated. The order of properties serialized is the same as the order
of columns retrieved. In this default mapping scheme, duplicate column names are ignored from the result set and only
the first instance of a duplicately named column is used in the output mapping.

A custom mapping may be supplied via the `"result"` property of the method descriptor.

Each property of the result mapping may have a value that is an object or a string literal.

Here are the rules of mapping:

 * If the value is a string literal, then that value represents the name of the column to map to that property.
   * Since column names do not have to be unique in a SQL query's result set, a disambiguation feature is provided to
     select the proper column when there exist multiple columns with the same name in the result set. Add a back-quote
     character to the end of the name followed by an integer that is the instance number of the column, e.g. "id`2" will
     select the second occurence of the "id" column from the result set. Without this disambiguation, the default
     instance number of 1 is assumed.
 * If the value is an object, a sub-object is created and its properties are used for further mapping, recursively.
   * A special (optional) property named `"<exists>"` is used to determine the nullability of the whole sub-object.
     If this column's value is a NULL value then the entire sub-object is given a `null` value in the response and
     all mapping is ceased for that sub-object and its children. If the column's value is not NULL then mapping resumes
     and the column is ignored and will not be present in the output.
   * This feature is very useful when doing LEFT JOINs in your query and you want to represent each joined table's
     columns as independent sub-objects of the row object. You may use any of the joined-in columns as your
     `"<exists>"` test column.

Examples:

```javascript
  "query": {
    // Note the duplication of "id":
    "select": "1 as id, 'James' as first, 'Dunne' as last, null as address, 2 as id, 4 as stuff"
  },
  "result": {
    "Person": {
      "<exists>": "first",      // "Person" will be null if "first" column is NULL
      "ID":     "id`1",         // use first instance of "id"
      "First":  "first",
      "Last":   "last",
      "Address": {
        "<exists>": "address",  // "Address" will be null if "address" column is NULL
        "Line1":    "address"
      }
    },
    "Test": {
      "ID":     "id`2",         // use second instance of "id"
      "Stuff":  "stuff"
    }
  }
```

This mapping will generate the following JSON response:

```javascript
  {
    "Person": {
      "ID": 1,
      "First": "James",
      "Last": "Dunne",
      "Address": null
    },
    "Test": {
      "ID": 2,
      "Stuff": 4
    }
  }
```

Parameters
----------

Parameters are inputs to methods which can be passed via the query-string all the way down to the SQL query.

Parameters are defined in a `parameters` section as part of a method descriptor.

Let's take a look at an example `parameters` section:

```javascript
  "GetStudent": {       // method name
    "parameters": {     // method parameters
      "id": { "sqlName": "@id", "type": "StudentID" }
    },
    ...
  }
```

Each key in the `parameters` section is the parameter name to be used in the request query-string and its value is that
parameter's *parameter descriptor*.

Properties of the parameter descriptor:

  * `sqlName`: (required) the SQL parameter name to create for usage in the query
  * `type`: a named parameter type from a `parameterTypes` collection (mutually exclusive to `sqlType`)
  * `sqlType`: an anonymous SQL parameter type (e.g. `nvarchar(20)`) (mutually exclusive to `type`)
  * `optional`: (optional) a boolean value which represents the optionality of the parameter (default is false)
  * `default`: (optional) assign an explicit default value for the parameter (default is NULL)
  * `description`: (optional) gives a description of the parameter.

The `type` property names an existing type from a `parameterTypes` section. The `sqlType` property just describes an
anonymous SQL type used only for the current parameter. For some cases, it's best to not bother defining a named
parameter type just for a single parameter. These should be used sparingly, e.g. for one-off cases, not to be established
as a best practice.

In order to execute a method with parameters, simply pass them by name as query-string parameters, like so:

`http://localhost/data/service/method?param1=value1&param2=value2&param3=%00`

Query-string Parameter Serialization:

The special syntax `%00` is used to denote an explicit NULL value for a parameter. Any binary data must be specified
in a BASE-64 encoded string.

Notice that we're missing a definition for the `StudentID` parameter in the previous example. Let's take care of that
by introducing a new section, `parameterTypes`.

Parameter Types
---------------

Parameter types are described by `parameterTypes` sections. These sections may appear at the global level or the service
level but *not* at the method level. Parameter type names must be unique per service and shared across all methods of
the service.

Let's take a look at an example `parameterTypes` section:

```javascript
{
  "parameterTypes": {
    "StudentID": { "type": "int" },
  },
  "services": {
    ...
  }
}
```

Here we define a global parameter types section. This section is a dictionary of parameter type names where each name is
assigned a parameter type descriptor. This descriptor describes the SQL type of the parameter via the `type` property.

This section exists to reduce repetitiveness in parameter definition. The purpose of the set of named parameter types is to
provide self-documentation of services and their parameters for new service consumers. If a parameter is assigned a clearly
named type, as opposed to just `int`, this helps to document the fact that a `StudentID` primary key is expected as opposed
to just any old integer value.

More examples:

```javascript
  "parameterTypes": {
    "StudentID":      { "type": "int" },
    "FirstName":      { "type": "nvarchar(50)" },
    "DateTimeOffset": { "type": "datetimeoffset(7)" },
    // Can provide length and scale:
    "Grade":          { "type": "decimal(8,4)" }
  },
  "parameters": {
    // Add `"optional": true` to make a parameter optional; its default value will be NULL in that case.
    "test":   { "sqlName": "@test",   "type": "Grade",            "optional": true },
    "dummy":  { "sqlName": "@dummy",  "sqlType": "nvarchar(10)",  "optional": true }
  },
```

Properties of a parameter type descriptor:

  * `type`: (required) the SQL type of the parameter.
  * `description`: (optional) gives a description of the parameter.

Service Aliases
---------------

```javascript
{
  "aliases": {
    "sis;core": "sis;core;v3"
  },
  "services": {
    "sis;core;v1": { ... },
    "sis;core;v2": { ... },
    "sis;core;v3": { ... },
  }
}
```

Here we see an example `aliases` section at the root level of the descriptor document. It defines a set of named aliases
for existing service names. This is useful for providing a layer of indirection over fixed service names. A common example
is to create an unversioned alias name that always points to the bleeding-edge version name. This way, clients that want
to stay on top can, while those that want to stick with a more constant service can specify the exact version they want.

Service Inheritance
-------------------

Services support a very simple inheritance model. This is achieved through the use of the `base` property in the service
descriptor. It is used to name the service being derived from.

A derived service by default inherits all methods and parameter types from its base service. Methods and parameter types
may be replaced or added in the derived service. Removing methods is done by assigning the method name to `null` inside
the `methods` section.

Examples:

```javascript
  "sis;core;v3": {
    // Derives from "sis;core;v2" service:
    "base": "sis;core;v2",
    // Methods are added to/overwritten over the base service:
    "methods": {
      // We deprecate the "Questionable" method here with a warning message; it may still be used, but
      // a warning will be issued.
      "Questionable": { "deprecated": "This method does not correctly query data." }
    }
  },
```

Versioning
----------

If a client wants to stick to a known, specific version of the service, all it has to do is specify that
exact service name in the request URL, e.g.

`GET /data/sis;core;v5/GetStudent?id=1`

This way the client can protect itself against unwanted breaking changes which are bound to come. Of course, if the developers
responsible for the service's definition begin altering older versions then all bets are off. The intention here is to provide
the service developers with reasonable tooling to make it easy to maintain a reasonable versioning strategy and to provide a
reasonable guarantee to clients that things will not change for a specific version.

Great attention has been given to versioning of services and making it easy to define those versions. The optimal workflow is
to define a base service as version 1 and create incremental versions from there. Once a version has been stablized, release
it and start work on the next version based on the just-released version; one should *never* alter old stablizied versions.

The service host is amenable to live updates and bumping clients up to the latest version should be as simple as updating an
alias.

String Interpolation
--------------------

A useful feature is the ability to define a dictionary of string values that may be interpolated into other
string values found in the descriptor objects.

The `"$"` key represents this dictionary of named string values which can be used for interpolation. These key
names can include any character except `'}'`.

Almost any string value (with few exceptions) may contain a `"${key}"` interpolation syntax which looks up `key`
in the `"$"` dictionary and interpolates its value into the final string value.

The `"$"` dictionary also participates in service inheritance. A derived service inherits its base service's `"$"`
dictionary for interpolation values.

The `"$"` dictionary may be defined at the root level or at the service level.

Examples:

```javascript
  "sis;core;v1": {
    // This is the base dictionary. Its values may be overridden in derived services.
    "$": {
        "Student":      "vw001_Student",
        "Student;st":   "st.StudentID, st.FirstName, st.LastName"
    },
    "methods": {
      "GetStudent": {
        "parameters": {
            "id":       { "sqlName": "@id",     "type": "StudentID" }
        },
        "query": {
            "from":     "${Student} st",
            "where":    "st.StudentID = @id",
            "select":   "${Student;st}"
        }
      }
    }
  }
```

Here we can see the usage of the `"$"` dictionary and the string interpolation syntax found in the `query` properties.

Benchmarks
==========

Test setup, all on one server, running on commodity desktop hardware.

```
Windows Server 2008 R2 x64 (boot from VHD)
Intel Core i5-2500K @ 3.30GHz
8.0 GB RAM

SQL Server 2008 R2 x64
Running locally

wcat 6.4 x64 test runner
  10 sec warmup
  20 sec duration
  10 sec cooldown

Request URLs per "transaction":
  /data/sis;core;v1/GetStudent?id=1
  /data/sis;core;v1/GetStudent?id=2
  /data/sis;core;v1/GetStudent?id=3
  /data/sis;core/GetStudentByName?lastName=Dunne&firstName=James

Results:

  Virtual clients | Requests/sec | Errors
  ----------------+--------------+---------
               16 | 10920.50     | 0
               32 | 10950.75     | 0
               64 | 10918.80     | 0
              128 | 10809.10     | 0
              256 | 10346.60     | 0
              512 | 10747.20     | 0

There is a max limit of 1,000 concurrent connections that I cannot overcome currently. This will likely require rewriting
the main event loop of the REST0 framework.
```

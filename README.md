REST0-API
=========

This project allows .NET developers to declaratively construct a RESTful web service which directly executes SQL
queries against an MS SQL Server and returns their results as JSON. There is no boilerplate C# or VB coding
required. All you have to do is declare methods and the SQL queries used to fetch data.

How it works
------------

When the service host application first starts up, it fetches a service descriptor file (either via HTTP or via
a local file) and parses it and fully constructs all services and methods. Once the descriptor is parsed, the service
is ready to process HTTP requests. The service also sets up a background task to frequently fetch the service
descriptor file so it can run unattended and keep itself up-to-date.

Let's take a look at a very simple example data request:

**Request:**
`GET /data/sis;core/GetStudent?id=1`

**Response:**
```javascript
{
    "results": [
        {
            "Student": {
                "StudentID": 1,
                "FirstName": "James",
                "LastName": "Dunne"
            }
        }
    ]
}
```

Let's break down this example URL:

 * `/data` means this is a data request; one may also do `/meta` requests to get useful metadata.
 * `/sis;core` indicates the service's full name. Service name components are separated with semicolons to allow
   for namespacing and versioning. Aliases may also be set up to allow bleeding-edge clients to always work
   against the latest version of a service. In our example, `"sis;core"` is an alias for `"sis;core;v5"`, the
   latest version defined.
 * `/GetStudent` is the method name selected from the service to execute
 * `?id=1` is a query-string parameter which is bound to a SQL parameter `@id`

As it turns out, the path part of a request URL is always made of 3 parts:

 1. request type (`meta` or `data`)
 2. service name
 3. method name

If a client wants to stick to a known, specific version of the service, all it has to do is specify that
exact service name in the request URL, e.g.

`GET /data/sis;core;v5/GetStudent?id=1`

This way the client can protect itself against unwanted breaking changes which are bound to come. Of course,
if the developers responsible for the service's definition begin altering older versions then all bets are off.
The intention here is to provide the service developers with reasonable tooling to make it easy to maintain a
reasonable versioning strategy and to provide a reasonable guarantee to clients that things will not change for
a specific version.

Great attention has been given to versioning of services and making it easy to define those versions. The optimal
workflow is to define a base service as version 1 and create incremental versions from there. Once a version
has been stablized, release it and start work on the next version based on the just-released version; one should
*never* alter old stablizied versions.

Declarative HSON
----------------

The declarative approach is achieved through the use of a human-readable JSON file format which I call HSON.
HSON includes nice features on top of JSON such as comments (both `//` and `/* */` kind) and multi-line string
literals (e.g. `@"Hello, ""world""."`). HSON also allows one to import partial HSON documents directly into the
current HSON document (recursively, of course) using the `@import("path")` directive. This import functionality
allows for a more maintainable service descriptor which can be split across many files.

Let's take a look at an example of how a service developer would define a service and its methods.

```javascript
{
    "aliases": {
        "sis;core": "sis;core;v1"
    },
    "parameterTypes": {
        "StudentID": { "type": "int" },
    },
    "services": {
        "sis;core;v1": {
            "connection": {
                "dataSource":       "(local)\\SQLEXPRESS",
                "initialCatalog":   "APITest"
            },
            "methods": {
                "GetStudent": {
                    "parameters": {
                        "id":       { "sqlName": "@id",     "type": "StudentID" }
                    },
                    "query": {
                        "from":     "vw_Student st",
                        "where":    "st.StudentID = @id",
                        "select":   "st.StudentID, st.FirstName, st.LastName"
                    }
                },
```

First is the `aliases` section which lets a developer assign aliases to service names. The ideal
use of this feature is to assign a non-versioned alias to the latest version of a service.

Semicolons are used in service names because they are URL-friendly. The convention is a three-part
name beginning with the general category of service type followed by a specific variant name and
finally a version identifier.

Next we see the `parameterTypes` section which defines a set of named SQL parameter types that all
services may share. This section exists to reduce repetitiveness in parameter definition. At this
level, the section is optional because it can also be specified at the service level.

The purpose of the set of named parameter types is to provide self-documentation of services and
their parameters for new consumers. If a parameter is assigned a clearly named type, so much the
better for the client developer. So far, the only property of a parameter type is `type` which is
the SQL type to use for binding.

Now we see the main section, `services`. This section is a dictionary of unique service names with
service descriptors assigned to each name. There are several elements of a service descriptor:

  * `parameterTypes`: override and create new service-specific `parameterType`s using the same format
     introduced at the root level.
  * `connection`: defines a common SQL connection string (via its common component parts).
    The minimum requirements for a `connection` object are `dataSource` and `initialCatalog` properties.
    Set these to the server name (and instance) and the database name, respectively.
  * `methods`: defines method names and the SQL queries used to execute them.

This is a non-exhaustive list of service descriptor properties. There are a few more sections used for
advanced purposes that will be covered later in more detail.

Of course, the `methods` section is the main section of interest here. It is a dictionary of unique
method names with method descriptors assigned to each name. There are several elements of a method
descriptor:

  * `parameters`: defines a mapping of query-string parameter names to their SQL parameter counterparts.
  * `connection`: override the service-level DB connection.
  * `query`: defines the parts of the SQL SELECT query to be executed.

The SQL query is broken out into individual clauses of the SELECT query. This separation is beneficial
for many reasons. Firstly, it guarantees that one cannot write a query that is not a SELECT query.
Secondly, it provides a way to rearrange the clauses of a SQL query in a way that is more readable and
more amenable to a logical thought process during query development.

The final query is constructed as follows:

    [WITH XMLNAMESPACES (
        '`xmlns:???`' AS `???`
    )]
    [WITH `withCTEidentifier` AS (`withCTEexpression`)]
    SELECT `select`
    [FROM `from`]
    [WHERE `where`]
    [GROUP BY `groupBy`]
    [HAVING `having`]
    [ORDER BY `orderBy`]

Hopefully this break-down is clear where each property of the `query` object goes in the constructed
SELECT query. The only property absolutely required is `select`, of course.
NOTE: If this form is too restrictive, advanced developers may opt for the `sql` property which
overrides this deconstructed form and allows one to specify raw SQL query code. Use of this property
should be discouraged though. The deconstructed form guarantees safety in that one cannot write a
non-SELECT query. The `sql` property is only offered for complex SELECT query shapes that can otherwise
not be specified in the deconstructed form.

Versioning
----------

Services are versioned through a very simple inheritance model. This is achieved with a property of the
service descriptor not mentioned previously. The `base` property is used to name the service being derived
from.

A derived service by default inherits all methods and parameter types from its base service. Methods and
parameter types may be replaced or added in the derived service. Removing methods is done by assigning the
method name to `null` inside the `methods` section.

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

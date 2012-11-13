using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Aardwolf;

namespace REST0.APIService
{
    class Program
    {
        static void Main(string[] args)
        {
            // Parse the commandline arguments:
            var configValues = ConfigurationDictionary.Parse(args);

            // Require at least one "bind" value:
            List<string> bindUriPrefixes;
            if (!configValues.TryGetValue("bind", out bindUriPrefixes) || bindUriPrefixes.Count == 0)
            {
                Console.Error.WriteLine("Require at least one bind=http://ip:port/ argument.");
                return;
            }

            // Configurable number of accept requests active at one time per CPU core:
            int accepts = 4;
            string acceptsString;
            if (configValues.TryGetSingleValue("accepts", out acceptsString))
            {
                if (!Int32.TryParse(acceptsString, out accepts))
                    accepts = 4;
            }
            else
            {
                accepts = 4;
            }

            // Create an HTTP host and start it:
            var handler = new APIHttpAsyncHandler();

            var host = new HttpAsyncHost(handler, accepts);
            host.SetConfiguration(configValues);
            host.Run(bindUriPrefixes.ToArray());
        }
    }
}

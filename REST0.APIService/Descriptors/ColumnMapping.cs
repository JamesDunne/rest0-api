using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace REST0.APIService.Descriptors
{
    class ColumnMapping
    {
        public readonly Dictionary<string, ColumnMapping> Columns;
        public string Name;
        public int Instance;

        internal ColumnMapping(string name)
        {
            Name = name;
            Instance = 0;
            Columns = null;
        }

        internal ColumnMapping(string name, int instance)
        {
            Name = name;
            Instance = instance;
            Columns = null;
        }

        internal ColumnMapping(Dictionary<string, ColumnMapping> columns)
        {
            Name = null;
            Columns = columns;
        }
    }
}

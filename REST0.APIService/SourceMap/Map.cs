using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace REST0.APIService.SourceMap
{
    public struct Map
    {
        public readonly Line[] Lines;

        public Map(Line[] lines)
        {
            Lines = lines;
        }
    }
}

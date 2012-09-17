using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace REST0.APIService.SourceMap
{
    public struct Line
    {
        public readonly Segment[] Segments;

        public Line(Segment[] segments)
        {
            Segments = segments;
        }
    }
}

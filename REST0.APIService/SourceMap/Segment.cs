using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;

namespace REST0.APIService.SourceMap
{
    [DebuggerDisplay("{TargetLinePosition};{SourceName};{SourceLineNumber};{SourceLinePosition}")]
    public struct Segment
    {
        public readonly int TargetLinePosition;
        public readonly string SourceName;
        public readonly int SourceLineNumber;
        public readonly int SourceLinePosition;

        public Segment(int targetLinePos, string sourceName, int sourceLineNumber, int sourceLinePos)
        {
            TargetLinePosition = targetLinePos;
            SourceName = sourceName;
            SourceLineNumber = sourceLineNumber;
            SourceLinePosition = sourceLinePos;
        }

        public Segment(int targetLinePos)
        {
            TargetLinePosition = targetLinePos;
            SourceName = null;
            SourceLineNumber = -1;
            SourceLinePosition = -1;
        }
    }

    public class SegmentByTargetLinePosComparer : IComparer<Segment>
    {
        public static readonly SegmentByTargetLinePosComparer Default = new SegmentByTargetLinePosComparer();

        public int Compare(Segment x, Segment y)
        {
            return x.TargetLinePosition.CompareTo(y.TargetLinePosition);
        }
    }
}

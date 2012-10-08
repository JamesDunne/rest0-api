using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace System.Hson
{
    /// <summary>
    /// Determines how to emit whitespace in JSON.
    /// </summary>
    public enum JsonWhitespaceHandling
    {
        /// <summary>
        /// Removes all whitespace.
        /// </summary>
        NoWhitespace,
        /// <summary>
        /// Removes all whitespace but injects a single space character after a ':' or a ',' character.
        /// </summary>
        OnlySpacesAfterCommaAndColon,
        /// <summary>
        /// Leaves input HSON whitespace untouched, including extra whitespace found on comment-only lines.
        /// </summary>
        Untouched
    }

    /// <summary>
    /// Options to control the JSON emitter.
    /// </summary>
    public sealed class JsonEmitterOptions
    {
        /// <summary>
        /// Determines how whitespace is emitted. Default is NoWhitespace.
        /// </summary>
        public JsonWhitespaceHandling WhitespaceHandling { get; set; }
    }

    /// <summary>
    /// An HSON parser exception.
    /// </summary>
    public sealed class HsonParserException : Exception
    {
        public HsonParserException(int line, int column, string messageFormat, params object[] args)
            : base(String.Format("HSON parser error at line {0}({1})", line, column) + ": " + String.Format(messageFormat, args))
        {
            Line = line;
            Column = column;
        }

        public int Line { get; private set; }
        public int Column { get; private set; }
    }

    /// <summary>
    /// This class reads in a stream assumed to be in HSON format (JSON with human-readable additions) and
    /// emits JSON as output to any Read() command. The input HSON is not guaranteed to be well-formed JSON,
    /// thus the output JSON is also not guaranteed to be well-formed (see remarks).
    /// </summary>
    /// <remarks>
    /// The JSON subset of HSON is only superficially parsed to clean out comments and reparse multi-line string literals.
    /// </remarks>
    public sealed class HsonReader : StreamReader
    {
        readonly IEnumerator<char> hsonStripper;
        readonly bool detectEncodingFromByteOrderMarks;
        readonly int bufferSize;
        readonly string path;

        #region Constructors

        public HsonReader(string path, Encoding encoding, bool detectEncodingFromByteOrderMarks = true, int bufferSize = 8192)
            : base(path, encoding, detectEncodingFromByteOrderMarks, bufferSize)
        {
            this.path = path;
            this.detectEncodingFromByteOrderMarks = detectEncodingFromByteOrderMarks;
            this.bufferSize = bufferSize;
            this.hsonStripper = StripHSON();
            // Defaults:
            this.EmitterOptions = new JsonEmitterOptions() { WhitespaceHandling = JsonWhitespaceHandling.NoWhitespace };
            this.ImportStream = defaultFileImport;
            this._segments = new List<REST0.APIService.SourceMap.Segment>(80);
        }

        #endregion

        #region Options

        /// <summary>
        /// Gets a mutable class that controls the JSON emitter options.
        /// </summary>
        public JsonEmitterOptions EmitterOptions { get; private set; }

        /// <summary>
        /// Gets or sets a function used to import other HSON streams via the @import("path") directive.
        /// </summary>
        public Func<string, HsonReader> ImportStream { get; set; }

        /// <summary>
        /// Gets or sets a function used to record source mapping segments.
        /// </summary>
        public Action<REST0.APIService.SourceMap.Segment> RecordMappingSegment { get; set; }

        #endregion

        #region Import

        /// <summary>
        /// Default file import function.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        HsonReader defaultFileImport(string path)
        {
            // Treat paths as relative to current directory:
            var absPath = Path.Combine(Path.GetDirectoryName(this.path), path);
            return new HsonReader(absPath, base.CurrentEncoding, detectEncodingFromByteOrderMarks, bufferSize);
        }

        #endregion

        #region Source Map

        readonly List<REST0.APIService.SourceMap.Segment> _segments;
        int _emittedCount;

        /// <summary>
        /// Gets a new instance representing the Source Map for reverse-mapping the output positions to source positions.
        /// </summary>
        public REST0.APIService.SourceMap.Map SourceMap
        {
            get
            {
                return new REST0.APIService.SourceMap.Map(
                    new REST0.APIService.SourceMap.Line[1] {
                        new REST0.APIService.SourceMap.Line(_segments.ToArray())
                    }
                );
            }
        }

        #endregion

        #region HSON parser

        /// <summary>
        /// This function parses HSON and emits JSON, but not necessarily well-formed JSON. The JSON subset of HSON is
        /// only superficially parsed to clean out comments and reparse multi-line string literals.
        /// </summary>
        /// <returns></returns>
        IEnumerator<char> StripHSON()
        {
            // Set lastLine diff than line so that we generate the first segment in source map:
            int lastEmitLine = 0, lastEmitCol = 1;
            int lastReadLine = 1, lastReadCol = 1;
            int line = 1, col = 1;
            int epos = 0;
            char lastEmitted = (char)0;

            // Records the last-emitted character:
            Func<char, char> emit = (ec) =>
            {
                lastEmitted = ec;

                // Keep track of source mapping of segments:
                bool emitSegment = false;
                if (lastEmitLine != lastReadLine) emitSegment = true;
                else if (lastEmitCol != lastReadCol - 1) emitSegment = true;

                // Record a new segment of columns:
                if (emitSegment)
                {
                    var seg = new REST0.APIService.SourceMap.Segment(epos, this.path, lastReadLine - 1, lastReadCol - 1);
                    _segments.Add(seg);
                    if (RecordMappingSegment != null)
                        RecordMappingSegment(seg);
                }

                lastEmitLine = lastReadLine;
                lastEmitCol = lastReadCol;
                ++_emittedCount;
                ++epos;

                return ec;
            };

            // Reads the next character and keeps track of current line/column:
            Func<int> readNext = () =>
            {
                lastReadLine = line;
                lastReadCol = col;

                int x = base.Read();

                if (x == -1) return x;
                else if (x == '\r') return x;
                else if (x == '\n')
                {
                    ++line;
                    col = 1;
                    return x;
                }
                else ++col;
                return x;
            };

            int c, c2;

            // Read single chars at a time, relying on buffering implemented by base StreamReader class:
            c = readNext();
            while (c != -1)
            {
                // Parse comments and don't emit them:
                if (c == '/')
                {
                    c2 = readNext();
                    if (c2 == -1) throw new HsonParserException(line, col, "Unexpected end of stream");

                    if (c2 == '/')
                    {
                        // single line comment
                        c = readNext();
                        while (c != -1)
                        {
                            // Presence of an '\r' is irrelevant since we're not consuming it for storage.

                            // Stop at '\n':
                            if (c == '\n')
                            {
                                if (EmitterOptions.WhitespaceHandling == JsonWhitespaceHandling.Untouched)
                                {
                                    yield return emit((char)c);
                                }
                                c = readNext();
                                break;
                            }
                            else if (c == '\r')
                            {
                                if (EmitterOptions.WhitespaceHandling == JsonWhitespaceHandling.Untouched)
                                {
                                    yield return emit((char)c);
                                }
                                c = readNext();
                            }
                            else c = readNext();
                        }
                    }
                    else if (c2 == '*')
                    {
                        // block comment
                        c = readNext();
                        while (c != -1)
                        {
                            // Read up until '*/':
                            if (c == '*')
                            {
                                c = readNext();
                                if (c == -1) throw new HsonParserException(line, col, "Unexpected end of stream");
                                else if (c == '/') break;
                                else c = readNext();
                            }
                            else c = readNext();
                        }
                        if (c == -1) throw new HsonParserException(line, col, "Unexpected end of stream");
                        c = readNext();
                        continue;
                    }
                    // Not either comment type:
                    else throw new HsonParserException(line, col, "Unknown comment type");
                }
                else if (c == '@')
                {
                    c = readNext();
                    if (c == -1) throw new HsonParserException(line, col, "Unexpected end of stream");
                    if (c == '"')
                    {
                        // Parse the multiline string and emit a JSON string literal while doing so:
                        if (EmitterOptions.WhitespaceHandling == JsonWhitespaceHandling.OnlySpacesAfterCommaAndColon)
                        {
                            if (lastEmitted == ':' || lastEmitted == ',') yield return emit(' ');
                        }

                        // Emit the opening '"':
                        yield return emit('"');

                        c = readNext();
                        if (c == -1) throw new HsonParserException(line, col, "Unexpected end of stream");
                        while (c != -1)
                        {
                            // Is it a terminating '"' or a double '""'?
                            if (c == '"')
                            {
                                c = readNext();
                                if (c == '"')
                                {
                                    // Double quote chars are emitted as a single escaped quote char:
                                    yield return emit('\\');
                                    yield return emit('"');
                                    c = readNext();
                                }
                                else
                                {
                                    // Emit the terminating '"' and exit:
                                    yield return emit('"');
                                    break;
                                }
                            }
                            else if (c == '\\')
                            {
                                // Backslashes have no special meaning in multiline strings, pass them through as escaped:
                                yield return emit('\\');
                                yield return emit('\\');
                                c = readNext();
                            }
                            else if (c == '\r')
                            {
                                yield return emit('\\');
                                yield return emit('r');
                                c = readNext();
                            }
                            else if (c == '\n')
                            {
                                yield return emit('\\');
                                yield return emit('n');
                                c = readNext();
                            }
                            else
                            {
                                // Emit any other regular char:
                                yield return emit((char)c);
                                c = readNext();
                            }
                            if (c == -1) throw new HsonParserException(line, col, "Unexpected end of stream");
                        }
                    }
                    else if (Char.IsLetter((char)c))
                    {
                        // Read the word up to the next non-word char:
                        StringBuilder sbDirective = new StringBuilder(6);
                        sbDirective.Append((char)c);
                        while ((c = readNext()) != -1)
                        {
                            if (!Char.IsLetter((char)c)) break;
                            sbDirective.Append((char)c);
                        }
                        if (c == -1) throw new HsonParserException(line, col, "Unexpected end of directive");

                        string directive = sbDirective.ToString();
                        if (directive == "import")
                        {
                            // @import directive
                            if (c != '(') throw new HsonParserException(line, col, "Expected '('");
                            c = readNext();
                            // Parse a string argument:
                            if (c != '"') throw new HsonParserException(line, col, "Expected '\"'");
                            StringBuilder sbValue = new StringBuilder(80);
                            while ((c = readNext()) != -1)
                            {
                                if (c == '"') break;
                                sbValue.Append((char)c);
                            }
                            if (c != '"') throw new HsonParserException(line, col, "Expected '\"'");
                            c = readNext();
                            if (c != ')') throw new HsonParserException(line, col, "Expected ')'");
                            c = readNext();
                            // Call the import function to get an HsonReader to stream its output through to our caller:
                            string path = sbValue.ToString();
                            using (var imported = ImportStream(path))
                            {
                                int importEpos = epos;

                                // Imported Source Map segments get copied to our `_segments` list + an offset:
                                imported.RecordMappingSegment = (seg) => _segments.Add(new REST0.APIService.SourceMap.Segment(seg.TargetLinePosition + importEpos, seg.SourceName, seg.SourceLineNumber, seg.SourceLinePosition));

                                // Stream all the characters in:
                                while ((c2 = imported.Read()) != -1)
                                {
                                    yield return (char)c2;
                                }

                                epos += imported._emittedCount;
                            }
                        }
                        else
                        {
                            throw new HsonParserException(line, col, "Unknown directive, '@{0}'", directive);
                        }
                    }
                    else
                    {
                        throw new HsonParserException(line, col, "Unknown @directive");
                    }
                }
                else if (c == '"')
                {
                    // Parse and emit the string literal:
                    if (EmitterOptions.WhitespaceHandling == JsonWhitespaceHandling.OnlySpacesAfterCommaAndColon)
                    {
                        if (lastEmitted == ':' || lastEmitted == ',') yield return emit(' ');
                    }
                    yield return emit((char)c);

                    c = readNext();
                    if (c == -1) throw new HsonParserException(line, col, "Unexpected end of stream");
                    while (c != -1)
                    {
                        if (c == '"')
                        {
                            // Yield the terminating '"' and exit:
                            yield return emit((char)c);
                            break;
                        }
                        else if (c == '\\')
                        {
                            // We don't care what escape sequence it is so long as we handle the '\"' case properly.
                            yield return emit((char)c);
                            c = readNext();
                            // An early-terminated escape sequence is an error:
                            if (c == -1) throw new HsonParserException(line, col, "Unexpected end of stream");
                            // Yield the escape char too:
                            yield return emit((char)c);
                            c = readNext();
                        }
                        else
                        {
                            yield return emit((char)c);
                            c = readNext();
                        }
                        if (c == -1) throw new HsonParserException(line, col, "Unexpected end of stream");
                    }

                    c = readNext();
                }
                // Don't actually parse the underlying JSON, just recognize its basic tokens:
                else if (c == '{' || c == '[' || c == ',')
                {
                    if (EmitterOptions.WhitespaceHandling == JsonWhitespaceHandling.OnlySpacesAfterCommaAndColon)
                    {
                        if (lastEmitted == ':' || lastEmitted == ',') yield return emit(' ');
                    }
                    yield return emit((char)c);
                    c = readNext();
                }
                else if (c == ':' || c == ']' || c == '}')
                {
                    yield return emit((char)c);
                    c = readNext();
                }
                else if (Char.IsLetterOrDigit((char)c) || c == '_' || c == '.')
                {
                    if (EmitterOptions.WhitespaceHandling == JsonWhitespaceHandling.OnlySpacesAfterCommaAndColon)
                    {
                        if (lastEmitted == ':' || lastEmitted == ',') yield return emit(' ');
                    }
                    yield return emit((char)c);
                    c = readNext();
                }
                else if (Char.IsWhiteSpace((char)c))
                {
                    if (EmitterOptions.WhitespaceHandling == JsonWhitespaceHandling.Untouched)
                    {
                        yield return emit((char)c);
                    }
                    c = readNext();
                }
                else throw new HsonParserException(line, col, "Unexpected character '{0}'", (char)c);
            }
        }

        #endregion

        #region Public overrides

        public override int Read()
        {
            if (!hsonStripper.MoveNext()) return -1;
            return hsonStripper.Current;
        }

        public override int Peek()
        {
            return hsonStripper.Current;
        }

        public override int Read(char[] buffer, int index, int count)
        {
            if (count == 0) return 0;
            if (index >= buffer.Length) throw new ArgumentOutOfRangeException("index");
            if (count > buffer.Length) throw new ArgumentOutOfRangeException("count");
            if (index + count > buffer.Length) throw new ArgumentOutOfRangeException("count");

            int nr;
            for (nr = index; (nr < count) & hsonStripper.MoveNext(); ++nr)
            {
                buffer[nr] = hsonStripper.Current;
            }

            return nr;
        }

        public override string ReadLine()
        {
            var sb = new StringBuilder();
            while (hsonStripper.MoveNext() & (hsonStripper.Current != '\n'))
            {
                sb.Append(hsonStripper.Current);
            }
            return sb.ToString();
        }

        public override string ReadToEnd()
        {
            var sb = new StringBuilder();
            while (hsonStripper.MoveNext())
            {
                sb.Append(hsonStripper.Current);
            }
            return sb.ToString();
        }

        #endregion

        #region Test cases
#if TEST
        static void test()
        {
            var testCases = new string[] {
                @"",
                @"{}",
                @"/* hello world*/{}",
                @"// hello world
{}",
                @"{}/* hello world */",
                @"{}// hello world",
                @"{}
// word up!",
                @"/********/",
                @"{""key"": value}",
                @"{""key"": ""value""}",
                @"{""key"": 0.1423}",
                @"{""key"": []}",
                @"{""key"": [{},{}]}",
                @"{""key"": [,]}",      // invalid JSON but passes
                @"[]",
                @"[/* help! */1,2,3/*toomuch*/4]",
                @"""word!""",
                @"@""multiline
test
here""",
                @"true",
                @"false",
                @"null",
                @"1.2",
                @"""abc\""word""",
                @"""a\u01C3bcd""",

                // Failure cases:
                @"/********",
                @"@""",
                @"""",
                @"""\",
                @"""\""",
                @"@""\",
                @"/+",
                @"/*",
                @"a / b"
            };

            for (int i = 0; i < testCases.Length; ++i)
                try
                {
                    using (var hr = new HsonReader(new MemoryStream(Encoding.UTF8.GetBytes(testCases[i]))))
                    {
                        Console.WriteLine("'{0}'", hr.ReadToEnd());
                    }
                }
                catch (HsonParserException hpe)
                {
                    Console.WriteLine(hpe.Message);
                }

            Console.WriteLine();
            using (var hr = new HsonReader(@"config.hson"))
            {
                Console.WriteLine("'{0}'", hr.ReadToEnd());
            }
        }
#endif
        #endregion
    }
}

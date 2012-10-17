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
        // No options yet.
    }

    /// <summary>
    /// Represents a segment of some source input.
    /// </summary>
    public struct SourcePosition
    {
        /// <summary>
        /// Name of source.
        /// </summary>
        public readonly string Name;
        /// <summary>
        /// 1-based line number.
        /// </summary>
        public readonly int LineNumber;
        /// <summary>
        /// 1-based position on the line.
        /// </summary>
        public readonly int LinePosition;

        /// <summary>
        /// Creates a source position record.
        /// </summary>
        /// <param name="name">Name of source.</param>
        /// <param name="lineNumber">1-based line number.</param>
        /// <param name="linePos">1-based position on the line.</param>
        public SourcePosition(string name, int lineNumber, int linePos)
        {
            Name = name;
            LineNumber = lineNumber;
            LinePosition = linePos;
        }
    }

    /// <summary>
    /// An HSON parser exception.
    /// </summary>
    public sealed class HsonParserException : Exception
    {
        internal HsonParserException(SourcePosition source, string messageFormat, params object[] args)
            : base(String.Format("HSON parser error in '{0}' at line {1}({2})", source.Name, source.LineNumber, source.LinePosition) + ": " + String.Format(messageFormat, args))
        {
            SourcePosition = source;
        }

        /// <summary>
        /// Position of where the error occurred.
        /// </summary>
        public SourcePosition SourcePosition { get; private set; }
    }

    /// <summary>
    /// Represents a type of token that can be emitted.
    /// </summary>
    public enum TokenType
    {
        Invalid,

        Raw,            // unparsed sequence of JSON characters: null, true, false, numbers, etc.
        StringLiteral,
        Colon,
        Comma,
        OpenCurly,
        CloseCurly,
        OpenBracket,
        CloseBracket
    }

    /// <summary>
    /// A token to be emitted.
    /// </summary>
    public struct Token
    {
        /// <summary>
        /// The type of token.
        /// </summary>
        public TokenType TokenType;
        /// <summary>
        /// Optional raw text to be output with the token.
        /// </summary>
        public string Text;
        /// <summary>
        /// Where the source of this token was found.
        /// </summary>
        public SourcePosition Source;

        public Token(SourcePosition source, TokenType type)
            : this()
        {
            Source = source;
            TokenType = type;
        }

        public Token(SourcePosition source, TokenType type, string text)
            : this(source, type)
        {
            Text = text;
        }
    }

    /// <summary>
    /// This class reads in a local file assumed to be in HSON format (JSON with human-readable additions) and
    /// emits JSON tokens. The input HSON is not guaranteed to be well-formed JSON, thus the output JSON is also
    /// not guaranteed to be well-formed (see remarks).
    /// </summary>
    /// <remarks>
    /// The JSON subset of HSON is only lexed to parse the raw tokens and is not validated. Invalid tokens found
    /// in the source HSON will result in errors.
    /// </remarks>
    public sealed class HsonReader
    {
        readonly string path;
        readonly Encoding encoding;
        readonly bool detectEncodingFromByteOrderMarks;
        readonly int bufferSize;

        #region Constructors

        public HsonReader(string path, Encoding encoding, bool detectEncodingFromByteOrderMarks = true, int bufferSize = 8192)
        {
            this.path = path;
            this.encoding = encoding;
            this.detectEncodingFromByteOrderMarks = detectEncodingFromByteOrderMarks;
            this.bufferSize = bufferSize;
            // Defaults:
            this.Import = defaultFileImport;
        }

        #endregion

        #region Options

        /// <summary>
        /// Gets or sets a function used to import other HSON streams via the @import("path") directive.
        /// </summary>
        public Func<string, IEnumerator<Token>> Import { get; set; }

        #endregion

        #region Import

        /// <summary>
        /// Default file import function.
        /// </summary>
        /// <param name="path"></param>
        /// <returns></returns>
        IEnumerator<Token> defaultFileImport(string path)
        {
            // Treat paths as relative to current directory:
            var absPath = Path.Combine(Path.GetDirectoryName(this.path), path);
            return new HsonReader(absPath, encoding, detectEncodingFromByteOrderMarks, bufferSize).Read();
        }

        #endregion

        #region HSON parser

        /// <summary>
        /// This function parses HSON and emits JSON, but not necessarily well-formed JSON. The JSON subset of HSON is
        /// only superficially parsed to clean out comments and reparse multi-line string literals.
        /// </summary>
        /// <returns></returns>
        public IEnumerator<Token> Read()
        {
            using (var fs = new FileStream(this.path, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize))
            using (var sr = new StreamReader(fs, encoding, detectEncodingFromByteOrderMarks, bufferSize))
            {
                // Track the last read position and next position in the source:
                SourcePosition posRead = new SourcePosition(this.path, 1, 1),
                               posNext = new SourcePosition(this.path, 1, 1);

                // Reads the next character and keeps track of current position in the source:
                Func<int> readNext = () =>
                {
                    posRead = posNext;

                    // Read single chars at a time, relying on buffered reads for performance.
                    // Attempt to read a single char from the input stream:
                    int x = sr.Read();

                    // EOF?
                    if (x == -1) return x;
                    // CR does not affect output position:
                    else if (x == '\r') return x;
                    // LF affects output position:
                    else if (x == '\n')
                        posNext = new SourcePosition(posRead.Name, posRead.LineNumber + 1, 1);
                    // TODO: How to treat '\t'?
                    else
                        posNext = new SourcePosition(posRead.Name, posRead.LineNumber, posRead.LinePosition + 1);

                    return x;
                };

                int c, c2;

                c = readNext();
                while (c != -1)
                {
                    // Parse comments and don't emit them:
                    if (c == '/')
                    {
                        c2 = readNext();
                        if (c2 == -1) throw new HsonParserException(posRead, "Unexpected end of stream");

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
                                    c = readNext();
                                    break;
                                }
                                else if (c == '\r')
                                {
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
                                    if (c == -1) throw new HsonParserException(posRead, "Unexpected end of stream");
                                    else if (c == '/') break;
                                    else c = readNext();
                                }
                                else c = readNext();
                            }
                            if (c == -1) throw new HsonParserException(posRead, "Unexpected end of stream");
                            c = readNext();
                            continue;
                        }
                        // Not either comment type:
                        else throw new HsonParserException(posRead, "Unknown comment type");
                    }
                    else if (c == '@')
                    {
                        c = readNext();
                        if (c == -1) throw new HsonParserException(posRead, "Unexpected end of stream");

                        SourcePosition emitSource = posRead;

                        // @"multi-line string literal":
                        if (c == '"')
                        {
                            // Parse the multiline string and emit a string literal token:
                            StringBuilder emit = new StringBuilder();

                            c = readNext();
                            if (c == -1) throw new HsonParserException(posRead, "Unexpected end of stream");
                            while (c != -1)
                            {
                                // Is it a terminating '"' or a double '""'?
                                if (c == '"')
                                {
                                    c = readNext();
                                    if (c == '"')
                                    {
                                        // Double quote chars are emitted as a single escaped quote char:
                                        emit.Append('\\');
                                        emit.Append('"');
                                        c = readNext();
                                    }
                                    else
                                    {
                                        // Exit:
                                        break;
                                    }
                                }
                                else if (c == '\\')
                                {
                                    // Backslashes have no special meaning in multiline strings, pass them through as escaped:
                                    emit.Append('\\');
                                    emit.Append('\\');
                                    c = readNext();
                                }
                                else if (c == '\r')
                                {
                                    emit.Append('\\');
                                    emit.Append('r');
                                    c = readNext();
                                }
                                else if (c == '\n')
                                {
                                    emit.Append('\\');
                                    emit.Append('n');
                                    c = readNext();
                                }
                                else
                                {
                                    // Emit any other regular char:
                                    emit.Append((char)c);
                                    c = readNext();
                                }
                                if (c == -1) throw new HsonParserException(posRead, "Unexpected end of stream");
                            }
                            // Yield the string literal token:
                            yield return new Token(emitSource, TokenType.StringLiteral, emit.ToString());
                        }
                        // @directive...
                        else if (Char.IsLetter((char)c))
                        {
                            // Read the word up to the next non-word char:
                            StringBuilder sbDirective = new StringBuilder("import".Length);
                            sbDirective.Append((char)c);
                            while ((c = readNext()) != -1)
                            {
                                if (!Char.IsLetter((char)c)) break;
                                sbDirective.Append((char)c);
                            }
                            if (c == -1) throw new HsonParserException(posRead, "Unexpected end of directive");

                            string directive = sbDirective.ToString();
                            if (directive == "import")
                            {
                                // @import directive
                                if (c != '(') throw new HsonParserException(posRead, "Expected '('");
                                c = readNext();
                                // Parse a string argument:
                                if (c != '"') throw new HsonParserException(posRead, "Expected '\"'");
                                StringBuilder sbValue = new StringBuilder(80);
                                while ((c = readNext()) != -1)
                                {
                                    if (c == '"') break;
                                    sbValue.Append((char)c);
                                }
                                if (c != '"') throw new HsonParserException(posRead, "Expected '\"'");
                                c = readNext();
                                if (c != ')') throw new HsonParserException(posRead, "Expected ')'");
                                c = readNext();

                                // Call the import function to get an IEnumerator<Token> to stream its output through to our caller:
                                string path = sbValue.ToString();
                                using (var imported = Import(path))
                                {
                                    while (imported.MoveNext())
                                    {
                                        yield return imported.Current;
                                    }
                                }
                            }
                            else
                            {
                                throw new HsonParserException(posRead, "Unknown directive, '@{0}'", directive);
                            }
                        }
                        else
                        {
                            throw new HsonParserException(posRead, "Unknown @directive");
                        }
                    }
                    else if (c == '"')
                    {
                        // Parse the string literal:
                        SourcePosition emitSource = posRead;
                        c = readNext();
                        if (c == -1) throw new HsonParserException(posRead, "Unexpected end of stream");

                        StringBuilder emit = new StringBuilder(80);
                        while (c != -1)
                        {
                            if (c == '"')
                            {
                                // Exit:
                                break;
                            }
                            else if (c == '\\')
                            {
                                // We don't care what escape sequence it is so long as we handle the '\"' case properly.

                                // Emit the '\':
                                emit.Append((char)c);

                                // An early-terminated escape sequence is an error:
                                c = readNext();
                                if (c == -1) throw new HsonParserException(posRead, "Unexpected end of stream");

                                // Emit the escaped char too:
                                emit.Append((char)c);
                                c = readNext();
                            }
                            else
                            {
                                // Emit regular characters:
                                emit.Append((char)c);
                                c = readNext();
                            }

                            if (c == -1) throw new HsonParserException(posRead, "Unexpected end of stream");
                        }

                        c = readNext();

                        // TODO: concatenace neighboring string literals!
                        yield return new Token(emitSource, TokenType.StringLiteral, emit.ToString());
                    }
                    // Don't actually parse the underlying JSON, just recognize its basic tokens:
                    else if (Char.IsWhiteSpace((char)c))
                    {
                        // Don't emit whitespace runs as a token.
                        c = readNext();
                    }
                    else if (c == '{')
                    {
                        yield return new Token(posRead, TokenType.OpenCurly);
                        c = readNext();
                    }
                    else if (c == '[')
                    {
                        yield return new Token(posRead, TokenType.OpenBracket);
                        c = readNext();
                    }
                    else if (c == ',')
                    {
                        yield return new Token(posRead, TokenType.Comma);
                        c = readNext();
                    }
                    else if (c == ':')
                    {
                        yield return new Token(posRead, TokenType.Colon);
                        c = readNext();
                    }
                    else if (c == ']')
                    {
                        yield return new Token(posRead, TokenType.CloseBracket);
                        c = readNext();
                    }
                    else if (c == '}')
                    {
                        yield return new Token(posRead, TokenType.CloseCurly);
                        c = readNext();
                    }
                    // FIXME: what's '_' doing here?
                    else if (Char.IsLetterOrDigit((char)c) || c == '_' || c == '.')
                    {
                        SourcePosition runStart = posRead;

                        StringBuilder emit = new StringBuilder();
                        while ((c != -1) && (Char.IsLetterOrDigit((char)c) || c == '_' || c == '.'))
                        {
                            emit.Append((char)c);
                            c = readNext();
                        }

                        yield return new Token(runStart, TokenType.Raw, emit.ToString());
                    }
                    else throw new HsonParserException(posRead, "Unexpected character '{0}'", (char)c);
                }
            }
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

    /// <summary>
    /// Translates a JSON token stream into a character stream.
    /// </summary>
    public sealed class JsonTokenStream : TextReader
    {
        readonly IEnumerator<int> charStream;
        readonly List<SourceMap.Segment> segments;

        public JsonTokenStream(IEnumerator<Token> input)
        {
            this.segments = new List<SourceMap.Segment>(1024);
            this.charStream = ProduceChars(input, segments);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                charStream.Dispose();
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// Produce individual characters out of the token stream.
        /// </summary>
        /// <returns></returns>
        static IEnumerator<int> ProduceChars(IEnumerator<Token> input, List<SourceMap.Segment> segments)
        {
            int pos = 1;

            // Keeps track of the output position:
            Func<int, int> emit = (e) => { ++pos; return e; };

            using (input)
                while (input.MoveNext())
                {
                    var tok = input.Current;

                    // Add a new sourcemap segment for the upcoming output:
                    segments.Add(new SourceMap.Segment(pos, tok.Source.Name, tok.Source.LineNumber, tok.Source.LinePosition));

                    switch (tok.TokenType)
                    {
                        case TokenType.Raw:
                            foreach (char c in tok.Text)
                                yield return emit((int)c);
                            break;
                        case TokenType.StringLiteral:
                            yield return emit('"');
                            foreach (char c in tok.Text)
                                yield return emit((int)c);
                            yield return emit('"');
                            break;
                        case TokenType.Colon:
                            yield return emit(':');
                            break;
                        case TokenType.Comma:
                            yield return emit(',');
                            break;
                        case TokenType.OpenCurly:
                            yield return emit('{');
                            break;
                        case TokenType.OpenBracket:
                            yield return emit('[');
                            break;
                        case TokenType.CloseBracket:
                            yield return emit(']');
                            break;
                        case TokenType.CloseCurly:
                            yield return emit('}');
                            break;
                        case TokenType.Invalid:
                        default:
                            throw new InvalidDataException("Invalid token type encountered");
                    }
                }
        }

        /// <summary>
        /// Gets a new copy of the source map.
        /// </summary>
        public SourceMap.Map SourceMap
        {
            get
            {
                return new SourceMap.Map(
                    // There's only ever one output line of JSON:
                    new SourceMap.Line[1]
                    {
                        new SourceMap.Line(segments.ToArray())
                    }
                );
            }
        }

        /// <summary>
        /// Read a single character from the JSON token stream.
        /// </summary>
        /// <returns></returns>
        public override int Read()
        {
            if (!charStream.MoveNext()) return -1;
            return charStream.Current;
        }

        /// <summary>
        /// Peeks at the last read character or throws an exception.
        /// </summary>
        /// <returns></returns>
        public override int Peek()
        {
            // Might throw an exception if not initialized.
            return charStream.Current;
        }

        #region Untested

        public override int Read(char[] buffer, int index, int count)
        {
            if (count == 0) return 0;
            if (index >= buffer.Length) throw new ArgumentOutOfRangeException("index");
            if (count > buffer.Length) throw new ArgumentOutOfRangeException("count");
            if (index + count > buffer.Length) throw new ArgumentOutOfRangeException("count");

            int nr;
            for (nr = index; (nr < count) & charStream.MoveNext(); ++nr)
            {
                buffer[nr] = (char)charStream.Current;
            }

            return nr;
        }

        public override string ReadLine()
        {
            var sb = new StringBuilder(80);
            while (charStream.MoveNext() && (charStream.Current != '\n'))
            {
                sb.Append((char)charStream.Current);
            }
            return sb.ToString();
        }

        public override string ReadToEnd()
        {
            var sb = new StringBuilder();
            while (charStream.MoveNext())
            {
                sb.Append((char)charStream.Current);
            }
            return sb.ToString();
        }

        #endregion
    }
}

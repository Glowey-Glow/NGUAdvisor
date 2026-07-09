using System.Globalization;
using System.Text;

namespace NGUAdvisor.Managers
{
    // Self-contained strict JSON validator for allocation profiles.
    //
    // The advisor parses profiles with SimpleJSON, which is extremely lenient: it treats } and ]
    // as interchangeable and silently ignores stray/missing commas, so a malformed profile does not
    // crash - it misparses silently and the bot misbehaves with no feedback. This validator does a
    // strict RFC-8259 parse (with one intentional tolerance: trailing commas, which every real parser
    // here accepts) and reports the first structural problem with a line/column, so the user can fix
    // it instead of guessing. Zero game/Unity dependencies so it is unit-testable in isolation.
    public static class ProfileValidator
    {
        public struct Result
        {
            public bool Ok;
            public int Line;    // 1-based; 0 when Ok
            public int Col;     // 1-based; 0 when Ok
            public string Message;

            public static Result Success => new Result { Ok = true };
        }

        public static Result Validate(string json)
        {
            if (json == null)
                return Fail(json, 0, "Profile is empty.");

            var p = new Parser(json);
            p.SkipWhitespace();
            if (p.Eof)
                return Fail(json, p.Pos, "Profile is empty.");

            if (!p.ParseValue(out var err))
                return err;

            p.SkipWhitespace();
            if (!p.Eof)
                return Fail(json, p.Pos, "Unexpected extra content after the top-level value.");

            return Result.Success;
        }

        private static Result Fail(string json, int pos, string message)
        {
            LineCol(json, pos, out var line, out var col);
            return new Result { Ok = false, Line = line, Col = col, Message = message };
        }

        private static void LineCol(string json, int pos, out int line, out int col)
        {
            line = 1;
            col = 1;
            if (json == null)
                return;
            if (pos > json.Length)
                pos = json.Length;
            for (int i = 0; i < pos; i++)
            {
                if (json[i] == '\n')
                {
                    line++;
                    col = 1;
                }
                else
                {
                    col++;
                }
            }
        }

        private class Parser
        {
            private readonly string _s;
            public int Pos;

            public Parser(string s)
            {
                _s = s;
                Pos = 0;
            }

            public bool Eof => Pos >= _s.Length;
            private char Cur => _s[Pos];

            public void SkipWhitespace()
            {
                while (Pos < _s.Length)
                {
                    var c = _s[Pos];
                    if (c == ' ' || c == '\t' || c == '\r' || c == '\n')
                        Pos++;
                    else
                        break;
                }
            }

            private Result Err(string message) => Fail(_s, Pos, message);
            private Result ErrAt(int pos, string message) => Fail(_s, pos, message);

            public bool ParseValue(out Result err)
            {
                err = Result.Success;
                SkipWhitespace();
                if (Eof)
                {
                    err = Err("Expected a value but reached the end of the profile.");
                    return false;
                }

                var c = Cur;
                switch (c)
                {
                    case '{': return ParseObject(out err);
                    case '[': return ParseArray(out err);
                    case '"': return ParseString(out err);
                    case 't':
                    case 'f': return ParseKeyword(out err);
                    case 'n': return ParseKeyword(out err);
                    default:
                        if (c == '-' || (c >= '0' && c <= '9'))
                            return ParseNumber(out err);
                        err = Err($"Unexpected character '{Describe(c)}' where a value was expected.");
                        return false;
                }
            }

            private bool ParseObject(out Result err)
            {
                err = Result.Success;
                Pos++; // consume {
                SkipWhitespace();
                if (!Eof && Cur == '}') { Pos++; return true; }

                while (true)
                {
                    SkipWhitespace();
                    if (Eof)
                    {
                        err = Err("Unterminated object - missing '}'.");
                        return false;
                    }
                    if (Cur != '"')
                    {
                        err = Err($"Expected a property name in double quotes, found '{Describe(Cur)}'.");
                        return false;
                    }
                    if (!ParseString(out err)) return false;

                    SkipWhitespace();
                    if (Eof || Cur != ':')
                    {
                        err = Err("Expected ':' after the property name.");
                        return false;
                    }
                    Pos++; // consume :

                    if (!ParseValue(out err)) return false;

                    SkipWhitespace();
                    if (Eof)
                    {
                        err = Err("Unterminated object - missing '}'.");
                        return false;
                    }
                    if (Cur == ',')
                    {
                        Pos++;
                        SkipWhitespace();
                        // Tolerate a trailing comma before '}'
                        if (!Eof && Cur == '}') { Pos++; return true; }
                        continue;
                    }
                    if (Cur == '}') { Pos++; return true; }
                    if (Cur == ']')
                    {
                        err = Err("Object closed with ']' instead of '}'.");
                        return false;
                    }
                    err = Err($"Expected ',' or '}}' after a property value, found '{Describe(Cur)}' (a missing comma is the usual cause).");
                    return false;
                }
            }

            private bool ParseArray(out Result err)
            {
                err = Result.Success;
                Pos++; // consume [
                SkipWhitespace();
                if (!Eof && Cur == ']') { Pos++; return true; }

                while (true)
                {
                    if (!ParseValue(out err)) return false;

                    SkipWhitespace();
                    if (Eof)
                    {
                        err = Err("Unterminated array - missing ']'.");
                        return false;
                    }
                    if (Cur == ',')
                    {
                        Pos++;
                        SkipWhitespace();
                        // Tolerate a trailing comma before ']'
                        if (!Eof && Cur == ']') { Pos++; return true; }
                        continue;
                    }
                    if (Cur == ']') { Pos++; return true; }
                    if (Cur == '}')
                    {
                        err = Err("Array closed with '}' instead of ']'.");
                        return false;
                    }
                    err = Err($"Expected ',' or ']' after an array element, found '{Describe(Cur)}' (a missing comma is the usual cause).");
                    return false;
                }
            }

            private bool ParseString(out Result err)
            {
                err = Result.Success;
                int start = Pos;
                Pos++; // consume opening quote
                while (Pos < _s.Length)
                {
                    var c = _s[Pos];
                    if (c == '"') { Pos++; return true; }
                    if (c == '\\')
                    {
                        Pos++;
                        if (Pos >= _s.Length) break;
                        var e = _s[Pos];
                        if (e == '"' || e == '\\' || e == '/' || e == 'b' || e == 'f' || e == 'n' || e == 'r' || e == 't')
                        {
                            Pos++;
                        }
                        else if (e == 'u')
                        {
                            Pos++;
                            for (int k = 0; k < 4; k++)
                            {
                                if (Pos >= _s.Length || !IsHex(_s[Pos]))
                                {
                                    err = ErrAt(Pos, "Invalid \\u escape in string (expected 4 hex digits).");
                                    return false;
                                }
                                Pos++;
                            }
                        }
                        else
                        {
                            err = ErrAt(Pos, $"Invalid escape '\\{Describe(e)}' in string.");
                            return false;
                        }
                    }
                    else if (c == '\n' || c == '\r')
                    {
                        err = ErrAt(Pos, "Unterminated string (line break before closing quote).");
                        return false;
                    }
                    else
                    {
                        Pos++;
                    }
                }
                err = ErrAt(start, "Unterminated string - missing closing quote.");
                return false;
            }

            private bool ParseNumber(out Result err)
            {
                err = Result.Success;
                int start = Pos;
                if (!Eof && Cur == '-') Pos++;
                while (!Eof && ((Cur >= '0' && Cur <= '9') || Cur == '.' || Cur == 'e' || Cur == 'E' || Cur == '+' || Cur == '-'))
                    Pos++;
                var slice = _s.Substring(start, Pos - start);
                if (!double.TryParse(slice, NumberStyles.Float, CultureInfo.InvariantCulture, out _))
                {
                    err = ErrAt(start, $"Invalid number '{slice}'.");
                    return false;
                }
                return true;
            }

            private bool ParseKeyword(out Result err)
            {
                err = Result.Success;
                if (Match("true") || Match("false") || Match("null"))
                    return true;
                err = Err("Expected true, false, or null.");
                return false;
            }

            private bool Match(string word)
            {
                if (Pos + word.Length > _s.Length) return false;
                for (int k = 0; k < word.Length; k++)
                    if (_s[Pos + k] != word[k]) return false;
                Pos += word.Length;
                return true;
            }

            private static bool IsHex(char c) =>
                (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F');

            private static string Describe(char c)
            {
                if (c == '\n') return "\\n";
                if (c == '\r') return "\\r";
                if (c == '\t') return "\\t";
                return c.ToString();
            }
        }
    }
}

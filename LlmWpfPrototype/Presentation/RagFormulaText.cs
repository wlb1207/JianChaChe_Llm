using System.Text;
using System.Text.RegularExpressions;

namespace LlmWpfPrototype.Presentation;

// Display-only formatter for engineering LaTeX formulas (structural mechanics,
// GB/T 3811, GB 50017, JT/T 1430). If an expression cannot be parsed cleanly
// without altering its meaning, the caller keeps the full original text.
public static class RagFormulaText
{
    private const int MaxInputLength = 4096;
    private const int MaxDepth = 24;
    private static readonly Regex HorizontalWhitespace = new(@"[ \t]+", RegexOptions.CultureInvariant);
    private static readonly Regex AtomicOperand = new(
        @"^(?:[\p{L}\p{N}_′'₋₊⁰-⁹₀-₉]+|[0-9]+(?:\.[0-9]+)?|\[[\p{L}\p{N}_′'₋₊⁰-⁹₀-₉]+\])$",
        RegexOptions.CultureInvariant);
    private static readonly Regex SimpleScriptIdentifier = new(
        @"^[A-Za-z0-9\p{IsGreekandCoptic}\u2160-\u217F]{1,6}$",
        RegexOptions.CultureInvariant);

    public static bool TryFormat(string latex, out string rendered)
    {
        rendered = latex;
        if (string.IsNullOrWhiteSpace(latex) || latex.Length > MaxInputLength)
            return false;

        try
        {
            var value = new Parser(latex).ReadExpression(0, stopOnRightBrace: false, endEnvironment: null);
            var cleaned = CleanRenderedLines(value);
            if (cleaned.Length == 0)
                return false;
            rendered = cleaned;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string CleanRenderedLines(string value)
    {
        var lines = value.ReplaceLineEndings("\n").Split('\n');
        var formatted = new List<string>(lines.Length);
        foreach (var rawLine in lines)
        {
            var line = HorizontalWhitespace.Replace(rawLine, " ").Trim();
            if (line.Length > 0)
                formatted.Add(line);
        }
        return string.Join("\n", formatted);
    }

    private static string FormatScript(string value, bool superscript)
    {
        var trimmed = value.Trim();
        if (trimmed.Length == 0)
            return string.Empty;

        if (superscript && (trimmed == "°" || trimmed == "′" || trimmed == "′′" || trimmed == "'" || trimmed == "''"))
            return trimmed.Replace('\'', '′');

        const string scriptDigits = "0123456789+-=()";
        const string superscriptGlyphs = "⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻⁼⁽⁾";
        const string subscriptGlyphs = "₀₁₂₃₄₅₆₇₈₉₊₋₌₍₎";
        const string superscriptLetters = "ni";
        const string superscriptLetterGlyphs = "ⁿⁱ";
        const string subscriptLetters = "aehijklmnoprstuvx";
        const string subscriptLetterGlyphs = "ₐₑₕᵢⱼₖₗₘₙₒₚᵣₛₜᵤᵥₓ";

        var converted = new StringBuilder();
        var allConverted = true;
        foreach (var letter in trimmed)
        {
            var digitIndex = scriptDigits.IndexOf(letter);
            if (digitIndex >= 0)
            {
                converted.Append((superscript ? superscriptGlyphs : subscriptGlyphs)[digitIndex]);
                continue;
            }

            if (superscript)
            {
                var supLetterIndex = superscriptLetters.IndexOf(letter);
                if (supLetterIndex >= 0)
                {
                    converted.Append(superscriptLetterGlyphs[supLetterIndex]);
                    continue;
                }
            }
            else
            {
                var subLetterIndex = subscriptLetters.IndexOf(letter);
                if (subLetterIndex >= 0)
                {
                    converted.Append(subscriptLetterGlyphs[subLetterIndex]);
                    continue;
                }
            }

            allConverted = false;
            break;
        }

        if (allConverted)
            return converted.ToString();

        if (SimpleScriptIdentifier.IsMatch(trimmed))
            return (superscript ? "^" : "_") + trimmed;

        return (superscript ? "^(" : "_(") + trimmed + ")";
    }

    private static string ApplyAccent(string content, char combiningMark)
    {
        var trimmed = content.Trim();
        if (trimmed.Length == 0)
            return string.Empty;
        if (trimmed.Length == 1)
            return trimmed + combiningMark;
        // Put combining accent over the first symbol (e.g., \overline{\sigma_x} -> σ̄_x) or wrap cleanly.
        return trimmed[0].ToString() + combiningMark + trimmed[1..];
    }

    private sealed class Parser(string source)
    {
        private int _position;

        internal string ReadExpression(int depth, bool stopOnRightBrace, string? endEnvironment)
        {
            if (depth > MaxDepth)
                throw new FormatException();

            var expression = new StringBuilder();
            var canAttachScript = false;
            var hasSubscript = false;
            var hasSuperscript = false;

            while (_position < source.Length)
            {
                if (stopOnRightBrace && source[_position] == '}')
                    break;

                if (endEnvironment is not null && TryConsumeEndEnvironment(endEnvironment))
                    return expression.ToString();

                var character = source[_position++];
                if (character == '}')
                    throw new FormatException();

                if (character == '{')
                {
                    var grouped = ReadExpression(depth + 1, stopOnRightBrace: true, endEnvironment: null);
                    if (_position >= source.Length || source[_position++] != '}')
                        throw new FormatException();
                    expression.Append(grouped);
                    canAttachScript = grouped.Length > 0 && CanAttachAfter(grouped[^1]);
                    hasSubscript = false;
                    hasSuperscript = false;
                    continue;
                }

                if (char.IsWhiteSpace(character))
                {
                    expression.Append(' ');
                    continue;
                }

                if (character == '&')
                {
                    expression.Append("  ");
                    canAttachScript = false;
                    hasSubscript = false;
                    hasSuperscript = false;
                    continue;
                }

                if (character == '~')
                {
                    expression.Append('~');
                    canAttachScript = false;
                    hasSubscript = false;
                    hasSuperscript = false;
                    continue;
                }

                if (character is '^' or '_')
                {
                    var superscript = character == '^';
                    if (!canAttachScript || (superscript ? hasSuperscript : hasSubscript))
                        throw new FormatException();
                    hasSuperscript |= superscript;
                    hasSubscript |= !superscript;
                    var argument = ReadScriptArgument(depth + 1);
                    while (expression.Length > 0 && expression[^1] == ' ')
                        expression.Length--;
                    expression.Append(FormatScript(argument, superscript));
                    continue;
                }

                string atom;
                if (character == '\\')
                {
                    var prevChar = LastNonSpaceChar(expression);
                    atom = ReadCommand(depth + 1, prevChar);
                }
                else if (character == '\'')
                {
                    atom = "′";
                }
                else if (char.IsLetterOrDigit(character) || IsAllowedLiteralChar(character))
                {
                    atom = character switch
                    {
                        '=' => " = ",
                        '<' => " < ",
                        '>' => " > ",
                        _ => character.ToString()
                    };
                }
                else
                {
                    throw new FormatException();
                }

                expression.Append(atom);
                if (atom.Length > 0)
                {
                    canAttachScript = CanAttachAfter(atom[^1]);
                    hasSubscript = false;
                    hasSuperscript = false;
                }
            }

            if (endEnvironment is not null)
                throw new FormatException();
            if (depth == 0 && !stopOnRightBrace && _position != source.Length)
                throw new FormatException();
            return expression.ToString();
        }

        private static bool IsAllowedLiteralChar(char ch) =>
            "+-*/=<>.,;:()[]|!′×·±−÷≤≥≠≈≡∑∏∫√°%℃，、：；（）～—–‖⟨⟩⌈⌉⌊⌋".Contains(ch)
            || (ch >= '\u2160' && ch <= '\u217F'); // Roman numerals Ⅰ Ⅱ Ⅲ Ⅳ

        private static bool CanAttachAfter(char ch) =>
            char.IsLetterOrDigit(ch) || ")]}|∞∑∏∫′'\u0304\u0305\u0302\u0303\u20D7\u0307\u0308".Contains(ch);

        private string ReadScriptArgument(int depth)
        {
            SkipWhitespace();
            if (_position >= source.Length)
                throw new FormatException();

            var character = source[_position];
            if (character == '{')
                return ReadBracedArgument(depth);
            _position++;
            if (character == '\\')
            {
                var value = ReadCommand(depth, '\0').Trim();
                if (value.Length == 0)
                    throw new FormatException();
                return value;
            }
            if (character == '\'')
                return "′";
            if (!char.IsLetterOrDigit(character) && !"+-°′".Contains(character))
                throw new FormatException();
            return character.ToString();
        }

        private string ReadBracedArgument(int depth)
        {
            SkipWhitespace();
            if (_position >= source.Length || source[_position++] != '{')
                throw new FormatException();
            var argument = ReadExpression(depth, stopOnRightBrace: true, endEnvironment: null);
            if (_position >= source.Length || source[_position++] != '}')
                throw new FormatException();
            return HorizontalWhitespace.Replace(argument, " ").Trim();
        }

        private string ReadRawBracedText()
        {
            SkipWhitespace();
            if (_position >= source.Length || source[_position++] != '{')
                throw new FormatException();
            var sb = new StringBuilder();
            var braceDepth = 1;
            while (_position < source.Length && braceDepth > 0)
            {
                var ch = source[_position++];
                if (ch == '\\' && _position < source.Length)
                {
                    var next = source[_position];
                    if (next is '{' or '}' or '%' or '_' or '&' or '#')
                    {
                        sb.Append(next);
                        _position++;
                        continue;
                    }
                    if (next == ' ')
                    {
                        sb.Append(' ');
                        _position++;
                        continue;
                    }
                }
                if (ch == '{')
                {
                    braceDepth++;
                    continue;
                }
                if (ch == '}')
                {
                    braceDepth--;
                    if (braceDepth == 0)
                        break;
                    continue;
                }
                sb.Append(ch);
            }
            if (braceDepth != 0)
                throw new FormatException();
            return sb.ToString();
        }

        private string? TryReadOptionalBracketArgument(int depth)
        {
            var saved = _position;
            SkipWhitespace();
            if (_position >= source.Length || source[_position] != '[')
            {
                _position = saved;
                return null;
            }
            _position++;
            var sb = new StringBuilder();
            var bracketDepth = 1;
            while (_position < source.Length && bracketDepth > 0)
            {
                var ch = source[_position++];
                if (ch == '[') bracketDepth++;
                else if (ch == ']')
                {
                    bracketDepth--;
                    if (bracketDepth == 0) break;
                }
                sb.Append(ch);
            }
            if (bracketDepth != 0)
                throw new FormatException();
            var inner = sb.ToString().Trim();
            if (inner.Length == 0)
                return string.Empty;
            return new Parser(inner).ReadExpression(depth + 1, stopOnRightBrace: false, endEnvironment: null).Trim();
        }

        private void TrySkipOptionalSpacingBracket()
        {
            var saved = _position;
            SkipWhitespace();
            if (_position < source.Length && source[_position] == '[')
            {
                var close = source.IndexOf(']', _position + 1);
                if (close > _position && close - _position <= 16)
                {
                    _position = close + 1;
                    return;
                }
            }
            _position = saved;
        }

        private string ReadDelimiterToken()
        {
            SkipWhitespace();
            if (_position >= source.Length)
                throw new FormatException();
            var ch = source[_position++];
            if (ch == '.')
                return string.Empty;
            if (ch is '(' or ')' or '[' or ']' or '|' or '/')
                return ch.ToString();
            if (ch != '\\')
                throw new FormatException();
            if (_position >= source.Length)
                throw new FormatException();
            var start = _position;
            while (_position < source.Length && char.IsAsciiLetter(source[_position]))
                _position++;
            var cmd = start == _position ? source[_position++].ToString() : source[start.._position];
            return cmd switch
            {
                "{" => "{",
                "}" => "}",
                "|" or "Vert" or "lVert" or "rVert" => "‖",
                "vert" or "lvert" or "rvert" => "|",
                "langle" => "⟨",
                "rangle" => "⟩",
                "lfloor" => "⌊",
                "rfloor" => "⌋",
                "lceil" => "⌈",
                "rceil" => "⌉",
                "backslash" => "\\",
                _ => throw new FormatException()
            };
        }

        private bool TryConsumeEndEnvironment(string envName)
        {
            var saved = _position;
            SkipWhitespace();
            if (_position + 4 >= source.Length || !source.AsSpan(_position).StartsWith(@"\end", StringComparison.Ordinal))
            {
                _position = saved;
                return false;
            }
            _position += 4;
            SkipWhitespace();
            if (_position >= source.Length || source[_position] != '{')
            {
                _position = saved;
                return false;
            }
            var parsedName = ReadRawBracedText().Trim();
            if (string.Equals(parsedName, envName, StringComparison.Ordinal))
                return true;
            _position = saved;
            return false;
        }

        private string ReadEnvironment(int depth)
        {
            var envName = ReadRawBracedText().Trim();
            if (envName is "array")
            {
                // Skip column alignment specification, e.g. \begin{array}{lcc}
                SkipWhitespace();
                if (_position < source.Length && source[_position] == '{')
                    _ = ReadRawBracedText();
            }

            var supported = envName is "cases" or "dcases" or "aligned" or "align" or "align*"
                or "gather" or "gather*" or "gathered" or "split" or "array" or "equation" or "equation*";
            if (!supported)
                throw new FormatException();

            var body = ReadExpression(depth + 1, stopOnRightBrace: false, endEnvironment: envName);
            var lines = body.ReplaceLineEndings("\n").Split('\n')
                .Select(l => HorizontalWhitespace.Replace(l, " ").Trim())
                .Where(l => l.Length > 0)
                .ToArray();
            if (lines.Length == 0)
                return string.Empty;

            if (envName is "cases" or "dcases")
                return string.Join("\n", lines.Select(l => "{ " + l));

            return string.Join("\n", lines);
        }

        private static char LastNonSpaceChar(StringBuilder sb)
        {
            for (var i = sb.Length - 1; i >= 0; i--)
                if (!char.IsWhiteSpace(sb[i])) return sb[i];
            return '\0';
        }

        private bool NextTokenIsClosingParenOrBracket(char openChar)
        {
            var expectedClose = openChar == '(' ? ')' : openChar == '[' ? ']' : '\0';
            if (expectedClose == '\0') return false;
            var pos = _position;
            while (pos < source.Length && char.IsWhiteSpace(source[pos])) pos++;
            if (pos >= source.Length) return false;
            if (source[pos] == expectedClose) return true;
            if (source[pos] != '\\') return false;
            var span = source.AsSpan(pos);
            foreach (var prefix in new[] { @"\right", @"\bigr", @"\Bigr", @"\biggr", @"\Biggr", @"\big", @"\Big", @"\bigg", @"\Bigg" })
            {
                if (!span.StartsWith(prefix, StringComparison.Ordinal)) continue;
                var after = pos + prefix.Length;
                if (after < source.Length && char.IsAsciiLetter(source[after])) continue;
                while (after < source.Length && char.IsWhiteSpace(source[after])) after++;
                if (after < source.Length && source[after] == expectedClose) return true;
            }
            return false;
        }

        private string ReadCommand(int depth, char prevNonSpaceChar)
        {
            if (_position >= source.Length)
                throw new FormatException();

            var start = _position;
            while (_position < source.Length && char.IsAsciiLetter(source[_position]))
                _position++;
            var command = start == _position ? source[_position++].ToString() : source[start.._position];

            switch (command)
            {
                case "\\":
                    TrySkipOptionalSpacingBracket();
                    return "\n";
                case "begin":
                    return ReadEnvironment(depth);
                case "frac" or "dfrac" or "tfrac":
                {
                    var numerator = ReadBracedArgument(depth);
                    var denominator = ReadBracedArgument(depth);
                    if (numerator.Length == 0 || denominator.Length == 0)
                        throw new FormatException();
                    var core = FractionOperand(numerator) + " / " + FractionOperand(denominator);
                    return NextTokenIsClosingParenOrBracket(prevNonSpaceChar) ? core : "(" + core + ")";
                }
                case "sqrt":
                {
                    var rootIndex = TryReadOptionalBracketArgument(depth);
                    var radicand = ReadBracedArgument(depth);
                    if (radicand.Length == 0)
                        throw new FormatException();
                    var prefix = rootIndex switch
                    {
                        null or "" => "√",
                        "3" => "∛",
                        "4" => "∜",
                        _ => FormatScript(rootIndex, superscript: true) + "√"
                    };
                    return prefix + "(" + radicand + ")";
                }
                case "left" or "right" or "middle" or "big" or "Big" or "bigg" or "Bigg"
                    or "bigl" or "bigr" or "Bigl" or "Bigr" or "biggl" or "biggr" or "Biggl" or "Biggr":
                    return ReadDelimiterToken();
                case "text" or "textrm" or "textbf" or "textit" or "mbox" or "hbox":
                    return ReadRawBracedText();
                case "mathrm" or "mathbf" or "mathit" or "mathsf" or "mathtt" or "boldsymbol" or "bm" or "operatorname" or "rm" or "bf":
                    if (command is "rm" or "bf")
                        return string.Empty;
                    return ReadBracedArgument(depth);
                case "bar" or "overline":
                    return ApplyAccent(ReadBracedArgument(depth), '\u0304');
                case "hat" or "widehat":
                    return ApplyAccent(ReadBracedArgument(depth), '\u0302');
                case "tilde" or "widetilde":
                    return ApplyAccent(ReadBracedArgument(depth), '\u0303');
                case "vec":
                    return ApplyAccent(ReadBracedArgument(depth), '\u20D7');
                case "dot":
                    return ApplyAccent(ReadBracedArgument(depth), '\u0307');
                case "ddot":
                    return ApplyAccent(ReadBracedArgument(depth), '\u0308');
                case "hspace" or "phantom":
                    _ = ReadRawBracedText();
                    return " ";
                case "displaystyle" or "textstyle" or "scriptstyle" or "scriptscriptstyle" or "limits" or "nolimits" or "it" or "!" or "notag" or "nonumber":
                    return string.Empty;
                case "tag":
                    return "  (" + ReadRawBracedText().Trim() + ")";
                // Math operator words
                case "max" or "min" or "lim" or "sup" or "inf"
                    or "sin" or "cos" or "tan" or "cot" or "sec" or "csc"
                    or "arcsin" or "arccos" or "arctan" or "log" or "ln" or "lg" or "exp":
                    return command;
                // Lowercase Greek
                case "alpha": return "α";
                case "beta": return "β";
                case "gamma": return "γ";
                case "delta": return "δ";
                case "epsilon": return "ϵ";
                case "varepsilon": return "ε";
                case "zeta": return "ζ";
                case "eta": return "η";
                case "theta": return "θ";
                case "vartheta": return "ϑ";
                case "iota": return "ι";
                case "kappa": return "κ";
                case "lambda": return "λ";
                case "mu": return "μ";
                case "nu": return "ν";
                case "xi": return "ξ";
                case "pi": return "π";
                case "varpi": return "ϖ";
                case "rho": return "ρ";
                case "varrho": return "ϱ";
                case "sigma": return "σ";
                case "varsigma": return "ς";
                case "tau": return "τ";
                case "upsilon": return "υ";
                case "phi": return "φ";
                case "varphi": return "φ";
                case "chi": return "χ";
                case "psi": return "ψ";
                case "omega": return "ω";
                // Uppercase Greek
                case "Gamma": return "Γ";
                case "Delta": return "Δ";
                case "Theta": return "Θ";
                case "Lambda": return "Λ";
                case "Xi": return "Ξ";
                case "Pi": return "Π";
                case "Sigma": return "Σ";
                case "Upsilon": return "Υ";
                case "Phi": return "Φ";
                case "Psi": return "Ψ";
                case "Omega": return "Ω";
                // Operators, relations, symbols
                case "sum": return "∑";
                case "prod": return "∏";
                case "int": return "∫";
                case "partial": return "∂";
                case "nabla": return "∇";
                case "infty": return "∞";
                case "circ" or "degree": return "°";
                case "prime": return "′";
                case "le" or "leq" or "leqslant": return " ≤ ";
                case "ge" or "geq" or "geqslant": return " ≥ ";
                case "lt": return " < ";
                case "gt": return " > ";
                case "ll": return " ≪ ";
                case "gg": return " ≫ ";
                case "ne" or "neq": return " ≠ ";
                case "approx": return " ≈ ";
                case "equiv": return " ≡ ";
                case "sim": return " ~ ";
                case "simeq": return " ≃ ";
                case "cong": return " ≅ ";
                case "propto": return " ∝ ";
                case "times": return " × ";
                case "cdot" or "ast": return " · ";
                case "div": return " ÷ ";
                case "pm": return " ± ";
                case "mp": return " ∓ ";
                case "uparrow": return " ↑ ";
                case "downarrow": return " ↓ ";
                case "Rightarrow" or "implies": return " ⇒ ";
                case "rightarrow" or "to": return " → ";
                case "leftarrow": return " ← ";
                case "leftrightarrow" or "Leftrightarrow": return " ↔ ";
                case "ldots" or "cdots" or "dots": return "…";
                case "forall": return "∀";
                case "exists": return "∃";
                case "in": return " ∈ ";
                case "notin": return " ∉ ";
                case "perp": return " ⊥ ";
                case "parallel": return " ∥ ";
                case "angle": return "∠";
                case "triangle": return "△";
                case "vert" or "lvert" or "rvert": return "|";
                case "Vert" or "lVert" or "rVert" or "|": return "‖";
                case "langle": return "⟨";
                case "rangle": return "⟩";
                case "lfloor": return "⌊";
                case "rfloor": return "⌋";
                case "lceil": return "⌈";
                case "rceil": return "⌉";
                case "," or ";" or ":" or " " or "quad" or "qquad" or "enspace" or "thinspace" or "medspace" or "thickspace":
                    return " ";
                case "%": return "%";
                case "{": return "{";
                case "}": return "}";
                case "_": return "_";
                case "&": return "&";
                case "#": return "#";
                case "$": return "$";
                default:
                    throw new FormatException();
            }
        }

        private static string FractionOperand(string value) =>
            AtomicOperand.IsMatch(value) ? value : "(" + value + ")";

        private void SkipWhitespace()
        {
            while (_position < source.Length && char.IsWhiteSpace(source[_position]))
                _position++;
        }
    }
}

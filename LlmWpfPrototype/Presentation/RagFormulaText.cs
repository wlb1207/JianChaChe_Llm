using System.Text;
using System.Text.RegularExpressions;

namespace LlmWpfPrototype.Presentation;

// A small display-only subset of LaTeX. The caller keeps the full original
// expression when we cannot format it without changing its meaning.
internal static class RagFormulaText
{
    private const int MaxInputLength = 1024;
    private const int MaxDepth = 12;
    private static readonly Regex Whitespace = new(@"\s+", RegexOptions.CultureInvariant);
    private static readonly Regex AtomicOperand = new(@"^(?:[\p{L}\p{N}]|[0-9]+(?:\.[0-9]+)?|\[[\p{L}\p{N}]\])$", RegexOptions.CultureInvariant);

    internal static bool TryFormat(string latex, out string rendered)
    {
        rendered = latex;
        if (latex.Length == 0 || latex.Length > MaxInputLength)
            return false;

        try
        {
            var value = new Parser(latex).ReadExpression(0);
            if (value.Length == 0)
                return false;
            rendered = value;
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string FormatScript(string value, bool superscript)
    {
        const string scriptDigits = "0123456789+-=()";
        const string superscriptGlyphs = "⁰¹²³⁴⁵⁶⁷⁸⁹⁺⁻⁼⁽⁾";
        const string subscriptGlyphs = "₀₁₂₃₄₅₆₇₈₉₊₋₌₍₎";
        const string subscriptLetters = "aehijklmnoprstuvx";
        const string subscriptLetterGlyphs = "ₐₑₕᵢⱼₖₗₘₙₒₚᵣₛₜᵤᵥₓ";
        var converted = new StringBuilder();
        foreach (var letter in value)
        {
            var digitIndex = scriptDigits.IndexOf(letter);
            var letterIndex = subscriptLetters.IndexOf(letter);
            if (digitIndex >= 0)
                converted.Append((superscript ? superscriptGlyphs : subscriptGlyphs)[digitIndex]);
            else if (!superscript && letterIndex >= 0)
                converted.Append(subscriptLetterGlyphs[letterIndex]);
            else
                return (superscript ? "^(" : "_(") + value + ")";
        }
        return converted.ToString();
    }

    private sealed class Parser(string source)
    {
        private int _position;

        internal string ReadExpression(int depth)
        {
            if (depth > MaxDepth)
                throw new FormatException();

            var expression = new StringBuilder();
            var canAttachScript = false;
            var hasSubscript = false;
            var hasSuperscript = false;
            while (_position < source.Length && source[_position] != '}')
            {
                var character = source[_position++];
                if (char.IsWhiteSpace(character))
                {
                    expression.Append(' ');
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
                    atom = ReadCommand(depth + 1);
                }
                else if (char.IsLetterOrDigit(character) ||
                         "+-*/=<>.,;:()[]|!′'×·±−≤≥≠∑√°%".Contains(character))
                {
                    atom = character == '=' ? " = " : character.ToString();
                }
                else
                {
                    throw new FormatException();
                }

                expression.Append(atom);
                canAttachScript = atom.Length > 0 && (char.IsLetterOrDigit(atom[^1]) || ")]}|∞".Contains(atom[^1]));
                hasSubscript = false;
                hasSuperscript = false;
            }

            if (depth == 0 && _position != source.Length)
                throw new FormatException();
            return Whitespace.Replace(expression.ToString(), " ").Trim();
        }

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
                var value = ReadCommand(depth).Trim();
                if (value.Length == 0)
                    throw new FormatException();
                return value;
            }
            if (!char.IsLetterOrDigit(character))
                throw new FormatException();
            return character.ToString();
        }

        private string ReadBracedArgument(int depth)
        {
            SkipWhitespace();
            if (_position >= source.Length || source[_position++] != '{')
                throw new FormatException();
            var argument = ReadExpression(depth);
            if (argument.Length == 0 || _position >= source.Length || source[_position++] != '}')
                throw new FormatException();
            return argument;
        }

        private string ReadCommand(int depth)
        {
            if (_position >= source.Length)
                throw new FormatException();

            var start = _position;
            while (_position < source.Length && char.IsAsciiLetter(source[_position]))
                _position++;
            var command = start == _position ? source[_position++].ToString() : source[start.._position];
            return command switch
            {
                "frac" or "dfrac" or "tfrac" => "(" + FractionOperand(ReadBracedArgument(depth))
                    + " / " + FractionOperand(ReadBracedArgument(depth)) + ")",
                "sqrt" => "√(" + ReadBracedArgument(depth) + ")",
                "mathrm" => ReadBracedArgument(depth),
                "alpha" => "α", "beta" => "β", "gamma" => "γ", "delta" => "δ",
                "Delta" => "Δ", "epsilon" => "ϵ", "varepsilon" => "ε",
                "eta" => "η", "theta" => "θ", "lambda" => "λ", "mu" => "μ",
                "nu" => "ν", "rho" => "ρ", "sigma" => "σ", "tau" => "τ",
                "phi" => "ϕ", "varphi" => "φ", "psi" => "ψ", "omega" => "ω",
                "pi" => "π", "infty" => "∞",
                "le" or "leq" => " ≤ ", "ge" or "geq" => " ≥ ",
                "ne" or "neq" => " ≠ ", "approx" => " ≈ ", "equiv" => " ≡ ",
                "times" => " × ", "cdot" => " · ", "div" => " ÷ ",
                "pm" => " ± ", "mp" => " ∓ ", "uparrow" => " ↑ ", "downarrow" => " ↓ ",
                "Rightarrow" or "implies" => " ⇒ ", "rightarrow" or "to" => " → ",
                "leftarrow" => " ← ", "leftrightarrow" => " ↔ ",
                "," or ";" or ":" or " " or "quad" or "qquad" => " ",
                "!" => "", "%" => "%", "{" => "{", "}" => "}",
                _ => throw new FormatException()
            };
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

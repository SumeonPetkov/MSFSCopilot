using System.Globalization;

namespace SkyLink.Definitions;

/// <summary>
/// A tiny arithmetic expression over <c>value</c>, used to turn a synced value into a K: event
/// parameter, e.g. <c>value * 16</c> (millibars to KOHLSMAN_SET) or <c>bcd16(value)</c>.
/// Supports + - * /, parentheses, numbers and round/floor/ceil/abs/bcd16/clamp16k.
/// </summary>
public sealed class ParamExpr
{
    public static readonly ParamExpr Identity = Parse("value");

    private readonly Func<double, double> _eval;

    public string Source { get; }

    private ParamExpr(string source, Func<double, double> eval)
    {
        Source = source;
        _eval = eval;
    }

    public double Evaluate(double value) => _eval(value);

    public static ParamExpr Parse(string source)
    {
        var p = new Parser(source);
        var f = p.ParseExpr();
        p.ExpectEnd();
        return new ParamExpr(source.Trim(), f);
    }

    public override string ToString() => Source;

    private sealed class Parser(string s)
    {
        private int _i;

        public Func<double, double> ParseExpr()
        {
            var left = ParseTerm();
            while (true)
            {
                Skip();
                if (Eat('+')) { var l = left; var r = ParseTerm(); left = v => l(v) + r(v); }
                else if (Eat('-')) { var l = left; var r = ParseTerm(); left = v => l(v) - r(v); }
                else return left;
            }
        }

        private Func<double, double> ParseTerm()
        {
            var left = ParseUnary();
            while (true)
            {
                Skip();
                if (Eat('*')) { var l = left; var r = ParseUnary(); left = v => l(v) * r(v); }
                else if (Eat('/')) { var l = left; var r = ParseUnary(); left = v => l(v) / r(v); }
                else return left;
            }
        }

        private Func<double, double> ParseUnary()
        {
            Skip();
            if (Eat('-')) { var x = ParseUnary(); return v => -x(v); }
            return ParsePrimary();
        }

        private Func<double, double> ParsePrimary()
        {
            Skip();
            if (Eat('('))
            {
                var e = ParseExpr();
                Skip();
                if (!Eat(')')) throw Error("expected ')'");
                return e;
            }
            if (_i < s.Length && (char.IsDigit(s[_i]) || s[_i] == '.'))
            {
                int start = _i;
                while (_i < s.Length && (char.IsDigit(s[_i]) || s[_i] == '.')) _i++;
                double n = double.Parse(s[start.._i], CultureInfo.InvariantCulture);
                return _ => n;
            }
            if (_i < s.Length && char.IsLetter(s[_i]))
            {
                int start = _i;
                while (_i < s.Length && (char.IsLetterOrDigit(s[_i]) || s[_i] == '_')) _i++;
                string id = s[start.._i].ToLowerInvariant();
                if (id == "value") return v => v;
                Skip();
                if (!Eat('(')) throw Error($"unknown name '{id}'");
                var arg = ParseExpr();
                Skip();
                if (!Eat(')')) throw Error("expected ')'");
                return id switch
                {
                    "round" => v => Math.Round(arg(v), MidpointRounding.AwayFromZero),
                    "floor" => v => Math.Floor(arg(v)),
                    "ceil" => v => Math.Ceiling(arg(v)),
                    "abs" => v => Math.Abs(arg(v)),
                    "bcd16" => v => Bcd16(arg(v)),
                    "clamp16k" => v => Math.Clamp(arg(v), -16384, 16384),
                    _ => throw Error($"unknown function '{id}'"),
                };
            }
            throw Error("expected a number, 'value', a function or '('");
        }

        private static double Bcd16(double v)
        {
            int n = (int)Math.Round(Math.Abs(v));
            int result = 0, shift = 0;
            while (n > 0 && shift < 16)
            {
                result |= (n % 10) << shift;
                n /= 10;
                shift += 4;
            }
            return result;
        }

        public void ExpectEnd()
        {
            Skip();
            if (_i != s.Length) throw Error("unexpected text");
        }

        private void Skip()
        {
            while (_i < s.Length && char.IsWhiteSpace(s[_i])) _i++;
        }

        private bool Eat(char c)
        {
            if (_i < s.Length && s[_i] == c)
            {
                _i++;
                return true;
            }
            return false;
        }

        private FormatException Error(string what) => new($"Expression '{s}': {what} at position {_i}.");
    }
}

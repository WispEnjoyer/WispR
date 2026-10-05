using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;

namespace WispR
{
    /// <summary>
    /// Recognises maths ("15*9", "=sqrt(2)", "19% of 250", "2^10") and unit conversions
    /// ("5 km in miles", "100 f to c", "2 gb in mb") typed into the launcher.
    /// </summary>
    static class Calculator
    {
        public sealed class Answer
        {
            public string Display;  // shown big, e.g. "= 135"
            public string Copy;     // put on the clipboard with Enter
            public string Detail;   // shown small, e.g. "15 × 9"
        }

        public static Answer TryAnswer(string input)
        {
            // Real sums are short; a cap keeps pasted walls of text from costing anything (or nesting deep).
            if (string.IsNullOrWhiteSpace(input) || input.Length > 256) return null;
            input = input.Trim();
            return TryConvert(input) ?? TryCalculate(input);
        }

        // ---------- arithmetic ----------

        static Answer TryCalculate(string input)
        {
            bool forced = input.StartsWith("=");
            string expr = forced ? input.Substring(1) : input;
            if (expr.Trim().Length == 0) return null;

            // "19% of 250" → 19/100*250
            expr = Regex.Replace(expr, @"(\d+(?:[.,]\d+)?)\s*%\s*(?:of|von)\s*", "($1/100)*", RegexOptions.IgnoreCase);

            // Only treat it as a calculation if it clearly is one: an operator or a function between numbers.
            if (!forced)
            {
                if (!Regex.IsMatch(expr, @"\d")) return null;
                if (!Regex.IsMatch(expr, @"[-+*/^×÷x%()]|(sqrt|sin|cos|tan|log|ln|abs|round|floor|ceil|pi)", RegexOptions.IgnoreCase)) return null;
                if (Regex.IsMatch(expr, @"[a-wyz]", RegexOptions.IgnoreCase) &&
                    !Regex.IsMatch(expr, @"^[\s\d.,+\-*/^×÷x%()]*((sqrt|sin|cos|tan|log|ln|abs|round|floor|ceil|pi|e)[\s\d.,+\-*/^×÷x%()]*)*$", RegexOptions.IgnoreCase))
                    return null; // words that aren't maths: probably an app name like "7-Zip"
                if (Regex.IsMatch(expr.Trim(), @"^-?\d+([.,]\d+)?$")) return null; // a lone number
            }

            try
            {
                var p = new Parser(expr);
                double v = p.Parse();
                if (double.IsNaN(v) || double.IsInfinity(v)) return null;
                return new Answer { Display = "= " + Pretty(v), Copy = Plain(v), Detail = Tidy(expr) };
            }
            catch { return null; }
        }

        static string Tidy(string expr) =>
            Regex.Replace(expr.Trim(), @"\s+", " ").Replace("*", " × ").Replace("/", " ÷ ").Replace("  ", " ");

        static string Pretty(double v)
        {
            var c = CultureInfo.CurrentCulture;
            if (Math.Abs(v) >= 1e15 || (v != 0 && Math.Abs(v) < 1e-9)) return v.ToString("0.######E+0", c);
            return Math.Round(v, 10).ToString("#,##0.##########", c);
        }

        static string Plain(double v)
        {
            var c = CultureInfo.CurrentCulture;
            if (Math.Abs(v) >= 1e15 || (v != 0 && Math.Abs(v) < 1e-9)) return v.ToString("R", c);
            return Math.Round(v, 10).ToString("0.##########", c);
        }

        /// <summary>Recursive-descent parser: + - * / ^ %, parentheses, functions, implicit multiplication.</summary>
        sealed class Parser
        {
            readonly string s;
            int i;

            public Parser(string text)
            {
                s = text.Replace('×', '*').Replace('÷', '/').Replace(" ", "");
                s = Regex.Replace(s, @"(?<=[\d)])x(?=[\d(])", "*", RegexOptions.IgnoreCase); // 15x9
                // decimal comma (e.g. German "15,5") — commas are never thousands separators here
                s = s.Replace(',', '.');
            }

            public double Parse()
            {
                double v = Sum();
                if (i != s.Length) throw new FormatException();
                return v;
            }

            double Sum()
            {
                double v = Product();
                while (i < s.Length && (s[i] == '+' || s[i] == '-'))
                {
                    char op = s[i++];
                    double r = Product();
                    v = op == '+' ? v + r : v - r;
                }
                return v;
            }

            double Product()
            {
                double v = Power();
                while (i < s.Length)
                {
                    char c = s[i];
                    if (c == '*' || c == '/')
                    {
                        i++;
                        double r = Power();
                        v = c == '*' ? v * r : v / r;
                    }
                    else if (c == '(' || char.IsLetter(c)) v *= Power(); // implicit: 2(3+4), 2pi
                    else break;
                }
                return v;
            }

            // Nesting limit: deep "((((…" can't exhaust the stack (a stack overflow can't be caught).
            int depth;
            void Enter() { if (++depth > 300) throw new FormatException(); } // ~100 levels of brackets; far below the stack limit

            double Power()
            {
                Enter();
                try
                {
                    double v = Unary();
                    if (i < s.Length && s[i] == '^') { i++; v = Math.Pow(v, Power()); } // right-associative
                    return v;
                }
                finally { depth--; }
            }

            double Unary()
            {
                Enter();
                try
                {
                    if (i < s.Length && s[i] == '-') { i++; return -Unary(); }
                    if (i < s.Length && s[i] == '+') { i++; return Unary(); }
                    double v = Atom();
                    while (i < s.Length && s[i] == '%') { i++; v /= 100; }
                    return v;
                }
                finally { depth--; }
            }

            double Atom()
            {
                if (i >= s.Length) throw new FormatException();
                char c = s[i];
                if (c == '(')
                {
                    i++;
                    Enter();
                    double v;
                    try { v = Sum(); } finally { depth--; }
                    if (i < s.Length && s[i] == ')') i++;
                    return v;
                }
                if (char.IsDigit(c) || c == '.')
                {
                    int start = i;
                    while (i < s.Length && (char.IsDigit(s[i]) || s[i] == '.')) i++;
                    return double.Parse(s.Substring(start, i - start), CultureInfo.InvariantCulture);
                }
                if (char.IsLetter(c))
                {
                    int start = i;
                    while (i < s.Length && char.IsLetter(s[i])) i++;
                    string name = s.Substring(start, i - start).ToLowerInvariant();
                    if (name == "pi") return Math.PI;
                    if (name == "e") return Math.E;
                    double a = Atom();
                    switch (name)
                    {
                        case "sqrt": return Math.Sqrt(a);
                        case "sin": return Math.Sin(a);
                        case "cos": return Math.Cos(a);
                        case "tan": return Math.Tan(a);
                        case "log": return Math.Log10(a);
                        case "ln": return Math.Log(a);
                        case "abs": return Math.Abs(a);
                        case "round": return Math.Round(a);
                        case "floor": return Math.Floor(a);
                        case "ceil": return Math.Ceiling(a);
                    }
                }
                throw new FormatException();
            }
        }

        // ---------- unit conversion ----------

        sealed class Unit { public string Kind, Name; public double Factor; }

        static readonly Dictionary<string, Unit> Units = BuildUnits();

        static Dictionary<string, Unit> BuildUnits()
        {
            var d = new Dictionary<string, Unit>(StringComparer.OrdinalIgnoreCase);
            void Add(string kind, double factor, string name, params string[] aliases)
            {
                var u = new Unit { Kind = kind, Factor = factor, Name = name };
                d[name] = u;
                foreach (var a in aliases) d[a] = u;
            }
            // length (metres)
            Add("length", 0.001, "mm", "millimeter", "millimeters", "millimetre", "millimetres", "millimeter");
            Add("length", 0.01, "cm", "centimeter", "centimeters", "centimetre", "zentimeter");
            Add("length", 1, "m", "meter", "meters", "metre", "metres");
            Add("length", 1000, "km", "kilometer", "kilometers", "kilometre", "kilometres");
            Add("length", 0.0254, "in", "inch", "inches", "zoll", "\"");
            Add("length", 0.3048, "ft", "foot", "feet", "fuß", "fuss");
            Add("length", 0.9144, "yd", "yard", "yards");
            Add("length", 1609.344, "mi", "mile", "miles", "meile", "meilen");
            Add("length", 1852, "nmi", "nautical mile", "seemeile");
            // mass (kilograms)
            Add("mass", 1e-6, "mg", "milligram", "milligrams", "milligramm");
            Add("mass", 0.001, "g", "gram", "grams", "gramm");
            Add("mass", 1, "kg", "kilogram", "kilograms", "kilogramm", "kilo", "kilos");
            Add("mass", 1000, "t", "tonne", "tonnes", "ton", "tons");
            Add("mass", 0.028349523125, "oz", "ounce", "ounces", "unze");
            Add("mass", 0.45359237, "lb", "lbs", "pound", "pounds", "pfund");
            Add("mass", 6.35029318, "st", "stone", "stones");
            // volume (litres)
            Add("volume", 0.001, "ml", "milliliter", "milliliters", "millilitre");
            Add("volume", 0.01, "cl", "centiliter");
            Add("volume", 0.1, "dl", "deciliter");
            Add("volume", 1, "l", "liter", "liters", "litre", "litres");
            Add("volume", 1000, "m3", "m³", "cubic meter", "kubikmeter");
            Add("volume", 3.785411784, "gal", "gallon", "gallons");
            Add("volume", 0.946352946, "qt", "quart", "quarts");
            Add("volume", 0.473176473, "pt", "pint", "pints");
            Add("volume", 0.2365882365, "cup", "cups", "tasse", "tassen");
            Add("volume", 0.0295735295625, "floz", "fl oz", "fluid ounce", "fluid ounces");
            Add("volume", 0.005, "tsp", "teaspoon", "teelöffel");
            Add("volume", 0.015, "tbsp", "tablespoon", "esslöffel");
            // speed (m/s)
            Add("speed", 1, "m/s", "mps");
            Add("speed", 1 / 3.6, "km/h", "kmh", "kph");
            Add("speed", 0.44704, "mph");
            Add("speed", 0.514444, "kn", "knot", "knots", "knoten");
            // time (seconds)
            Add("time", 0.001, "ms", "millisecond", "milliseconds", "millisekunde", "millisekunden");
            Add("time", 1, "s", "sec", "secs", "second", "seconds", "sekunde", "sekunden");
            Add("time", 60, "min", "mins", "minute", "minutes", "minuten");
            Add("time", 3600, "h", "hr", "hrs", "hour", "hours", "stunde", "stunden");
            Add("time", 86400, "d", "day", "days", "tag", "tage");
            Add("time", 604800, "wk", "week", "weeks", "woche", "wochen");
            Add("time", 2629800, "mo", "month", "months", "monat", "monate");
            Add("time", 31557600, "yr", "year", "years", "jahr", "jahre");
            // data (bytes)
            Add("data", 0.125, "bit", "bits");
            Add("data", 1, "B", "byte", "bytes");
            Add("data", 1e3, "KB", "kilobyte", "kilobytes");
            Add("data", 1e6, "MB", "megabyte", "megabytes");
            Add("data", 1e9, "GB", "gigabyte", "gigabytes");
            Add("data", 1e12, "TB", "terabyte", "terabytes");
            Add("data", 1024, "KiB", "kibibyte");
            Add("data", 1048576, "MiB", "mebibyte");
            Add("data", 1073741824, "GiB", "gibibyte");
            Add("data", 1099511627776, "TiB", "tebibyte");
            Add("data", 125, "kbit", "kbps", "kb/s");
            Add("data", 125000, "Mbit", "mbps", "mb/s");
            Add("data", 125000000, "Gbit", "gbps", "gb/s");
            // area (square metres)
            Add("area", 0.0001, "cm2", "cm²");
            Add("area", 1, "m2", "m²", "sqm");
            Add("area", 1e6, "km2", "km²");
            Add("area", 10000, "ha", "hectare", "hectares", "hektar");
            Add("area", 4046.8564224, "acre", "acres");
            Add("area", 0.09290304, "ft2", "ft²", "sqft");
            // temperature (handled specially)
            Add("temp", 0, "°C", "c", "celsius", "°c", "grad");
            Add("temp", 0, "°F", "f", "fahrenheit", "°f");
            Add("temp", 0, "K", "k", "kelvin");
            return d;
        }

        static Answer TryConvert(string input)
        {
            var m = Regex.Match(input, @"^\s*(-?\d+(?:[.,]\d+)?)\s*([^\d\s][^\s]*(?:\s(?:oz|mile|ounce|ounces))?)\s+(?:in|to|into|as|nach|zu|=)\s+(.+?)\s*$", RegexOptions.IgnoreCase);
            if (!m.Success) return null;
            if (!Units.TryGetValue(m.Groups[2].Value.Trim(), out var from)) return null;
            if (!Units.TryGetValue(m.Groups[3].Value.Trim(), out var to)) return null;
            if (from.Kind != to.Kind) return null;

            double value = double.Parse(m.Groups[1].Value.Replace(',', '.'), CultureInfo.InvariantCulture);
            double result = from.Kind == "temp" ? ConvertTemp(value, from.Name, to.Name) : value * from.Factor / to.Factor;

            var c = CultureInfo.CurrentCulture;
            string nice = Math.Abs(result) >= 1e15 || (result != 0 && Math.Abs(result) < 1e-6)
                ? result.ToString("0.####E+0", c)
                : Math.Round(result, 6).ToString("#,##0.######", c);
            string plain = Math.Round(result, 6).ToString("0.######", c);
            return new Answer
            {
                Display = "= " + nice + " " + to.Name,
                Copy = plain,
                Detail = value.ToString("0.######", c) + " " + from.Name + " in " + to.Name,
            };
        }

        static double ConvertTemp(double v, string from, string to)
        {
            double c = from == "°C" ? v : from == "°F" ? (v - 32) * 5 / 9 : v - 273.15;
            return to == "°C" ? c : to == "°F" ? c * 9 / 5 + 32 : c + 273.15;
        }
    }
}

using System;
using System.Globalization;
using System.Numerics;

namespace Magic.Kernel.Types;

public enum FloatDecimalFormat : byte
{
    Decfloat16 = 16,
    Decfloat34 = 34
}

public enum FloatDecimalKind : byte
{
    Finite = 0,
    PositiveInfinity = 1,
    NegativeInfinity = 2,
    QuietNaN = 3,
    SignalingNaN = 4
}

public enum FloatDecimalRounding : byte
{
    HalfEven = 0,
    HalfUp = 1,
    HalfDown = 2,
    Up = 3,
    Down = 4,
    Ceiling = 5,
    Floor = 6,
    AwayFromZero = 7
}

public readonly struct FloatDecimal :
    IComparable<FloatDecimal>,
    IEquatable<FloatDecimal>
{
    private readonly BigInteger _coefficient;
    private readonly int _exponent;
    private readonly bool _negative;
    private readonly FloatDecimalKind _kind;
    private readonly FloatDecimalFormat _format;

    private readonly struct Context
    {
        public Context(int precision, int emin, int emax)
        {
            Precision = precision;
            Emin = emin;
            Emax = emax;
            Etiny = emin - (precision - 1);
        }

        public int Precision { get; }
        public int Emin { get; }
        public int Emax { get; }
        public int Etiny { get; }
    }

    private FloatDecimal(
        BigInteger coefficient,
        int exponent,
        bool negative,
        FloatDecimalKind kind,
        FloatDecimalFormat format)
    {
        _coefficient = coefficient;
        _exponent = exponent;
        _negative = negative;
        _kind = kind;
        _format = format;
    }

    public FloatDecimalFormat Format => _format;
    public FloatDecimalKind Kind => _kind;

    public bool IsFinite => _kind == FloatDecimalKind.Finite;
    public bool IsNaN => _kind == FloatDecimalKind.QuietNaN || _kind == FloatDecimalKind.SignalingNaN;
    public bool IsInfinity => _kind == FloatDecimalKind.PositiveInfinity || _kind == FloatDecimalKind.NegativeInfinity;
    public bool IsZero => IsFinite && _coefficient.IsZero;
    public bool IsNegative => _kind == FloatDecimalKind.NegativeInfinity || (IsFinite && _negative);
    public BigInteger Coefficient => EnsureFinite(_coefficient);
    public int Exponent => EnsureFinite(_exponent);

    public static FloatDecimal Zero16 => new(BigInteger.Zero, 0, false, FloatDecimalKind.Finite, FloatDecimalFormat.Decfloat16);
    public static FloatDecimal Zero34 => new(BigInteger.Zero, 0, false, FloatDecimalKind.Finite, FloatDecimalFormat.Decfloat34);
    public static FloatDecimal One16 => CreateFinite(false, BigInteger.One, 0, FloatDecimalFormat.Decfloat16);
    public static FloatDecimal One34 => CreateFinite(false, BigInteger.One, 0, FloatDecimalFormat.Decfloat34);

    public static FloatDecimal NaN(FloatDecimalFormat format = FloatDecimalFormat.Decfloat34) =>
        new(BigInteger.Zero, 0, false, FloatDecimalKind.QuietNaN, format);

    public static FloatDecimal SignalingNaN(FloatDecimalFormat format = FloatDecimalFormat.Decfloat34) =>
        new(BigInteger.Zero, 0, false, FloatDecimalKind.SignalingNaN, format);

    public static FloatDecimal PositiveInfinity(FloatDecimalFormat format = FloatDecimalFormat.Decfloat34) =>
        new(BigInteger.Zero, 0, false, FloatDecimalKind.PositiveInfinity, format);

    public static FloatDecimal NegativeInfinity(FloatDecimalFormat format = FloatDecimalFormat.Decfloat34) =>
        new(BigInteger.Zero, 0, true, FloatDecimalKind.NegativeInfinity, format);

    public static FloatDecimal FromInt64(long value, FloatDecimalFormat format = FloatDecimalFormat.Decfloat34) =>
        CreateFinite(value < 0, BigInteger.Abs(value), 0, format);

    public static FloatDecimal FromBigInteger(BigInteger value, FloatDecimalFormat format = FloatDecimalFormat.Decfloat34) =>
        CreateFinite(value.Sign < 0, BigInteger.Abs(value), 0, format);

    public static FloatDecimal FromDecimal(decimal value, FloatDecimalFormat format = FloatDecimalFormat.Decfloat34) =>
        Parse(value.ToString(CultureInfo.InvariantCulture), format);

    public static FloatDecimal FromDouble(double value, FloatDecimalFormat format = FloatDecimalFormat.Decfloat34) =>
        Parse(value.ToString("R", CultureInfo.InvariantCulture), format);

    public static FloatDecimal Parse(string text, FloatDecimalFormat format = FloatDecimalFormat.Decfloat34)
    {
        if (!TryParse(text, format, out var value))
            throw new FormatException($"Invalid FloatDecimal literal: '{text}'.");

        return value;
    }

    public static bool TryParse(string? text, FloatDecimalFormat format, out FloatDecimal value)
    {
        value = NaN(format);

        if (string.IsNullOrWhiteSpace(text))
            return false;

        var s = text.Trim();

        var negative = false;
        if (s[0] == '+' || s[0] == '-')
        {
            negative = s[0] == '-';
            s = s.Substring(1).TrimStart();
            if (s.Length == 0)
                return false;
        }

        if (s.Equals("inf", StringComparison.OrdinalIgnoreCase) ||
            s.Equals("infinity", StringComparison.OrdinalIgnoreCase))
        {
            value = negative ? NegativeInfinity(format) : PositiveInfinity(format);
            return true;
        }

        if (s.Equals("nan", StringComparison.OrdinalIgnoreCase))
        {
            value = NaN(format);
            return true;
        }

        if (s.Equals("snan", StringComparison.OrdinalIgnoreCase))
        {
            value = SignalingNaN(format);
            return true;
        }

        var expIndex = s.IndexOfAny(new[] { 'e', 'E' });
        var significandPart = expIndex >= 0 ? s.Substring(0, expIndex) : s;
        var exponentPart = expIndex >= 0 ? s.Substring(expIndex + 1) : "0";

        if (!int.TryParse(exponentPart, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var explicitExponent))
            return false;

        var dotIndex = significandPart.IndexOf('.');
        if (dotIndex >= 0 && significandPart.LastIndexOf('.') != dotIndex)
            return false;

        var intPart = dotIndex >= 0 ? significandPart.Substring(0, dotIndex) : significandPart;
        var fracPart = dotIndex >= 0 ? significandPart.Substring(dotIndex + 1) : string.Empty;

        if (intPart.Length == 0)
            intPart = "0";

        var digits = intPart + fracPart;
        if (digits.Length == 0)
            return false;

        for (var i = 0; i < digits.Length; i++)
        {
            if (digits[i] < '0' || digits[i] > '9')
                return false;
        }

        var trimmedLeading = TrimLeadingZeros(digits);
        var coefficient = trimmedLeading.Length == 0
            ? BigInteger.Zero
            : BigInteger.Parse(trimmedLeading, CultureInfo.InvariantCulture);

        var exponent = explicitExponent - fracPart.Length;
        value = CreateFinite(negative, coefficient, exponent, format);
        return true;
    }

    public FloatDecimal CastTo(
        FloatDecimalFormat targetFormat,
        FloatDecimalRounding? rounding = null)
    {
        if (!IsFinite)
        {
            return _kind switch
            {
                FloatDecimalKind.PositiveInfinity => PositiveInfinity(targetFormat),
                FloatDecimalKind.NegativeInfinity => NegativeInfinity(targetFormat),
                FloatDecimalKind.SignalingNaN => SignalingNaN(targetFormat),
                _ => NaN(targetFormat)
            };
        }

        var mode = rounding ?? DefaultCastRounding(_format, targetFormat);
        return CreateFinite(_negative, _coefficient, _exponent, targetFormat, mode);
    }

    public FloatDecimal Normalize()
    {
        if (!IsFinite)
            return this;

        if (_coefficient.IsZero)
            return new FloatDecimal(BigInteger.Zero, 0, _negative, FloatDecimalKind.Finite, _format);

        var (coeff, exp) = Canonicalize(_coefficient, _exponent);
        return new FloatDecimal(coeff, exp, _negative, FloatDecimalKind.Finite, _format);
    }

    public FloatDecimal Rescale(int scale, FloatDecimalRounding rounding = FloatDecimalRounding.HalfEven)
    {
        if (!IsFinite)
            return this;

        var targetExponent = -scale;

        if (_coefficient.IsZero)
            return new FloatDecimal(BigInteger.Zero, targetExponent, _negative, FloatDecimalKind.Finite, _format);

        if (_exponent == targetExponent)
            return this;

        if (_exponent > targetExponent)
        {
            var shift = _exponent - targetExponent;
            var coeff = _coefficient * Pow10(shift);
            return CreateFinite(_negative, coeff, targetExponent, _format, rounding);
        }

        var drop = targetExponent - _exponent;
        var rounded = RoundCoefficient(_coefficient, drop, _negative, rounding);
        return CreateFinite(_negative, rounded, targetExponent, _format, rounding);
    }

    public string ToScientificString()
    {
        if (IsNaN)
            return _kind == FloatDecimalKind.SignalingNaN ? "sNaN" : "NaN";

        if (_kind == FloatDecimalKind.PositiveInfinity)
            return "Infinity";

        if (_kind == FloatDecimalKind.NegativeInfinity)
            return "-Infinity";

        if (_coefficient.IsZero)
            return (_negative ? "-" : "") + "0E+0";

        var digits = _coefficient.ToString(CultureInfo.InvariantCulture);
        var adjustedExponent = digits.Length - 1 + _exponent;
        var mantissa = digits.Length == 1
            ? digits
            : digits[0] + "." + digits.Substring(1);

        return (_negative ? "-" : "")
             + mantissa
             + "E"
             + (adjustedExponent >= 0 ? "+" : "")
             + adjustedExponent.ToString(CultureInfo.InvariantCulture);
    }

    public override string ToString()
    {
        if (IsNaN)
            return _kind == FloatDecimalKind.SignalingNaN ? "sNaN" : "NaN";

        if (_kind == FloatDecimalKind.PositiveInfinity)
            return "Infinity";

        if (_kind == FloatDecimalKind.NegativeInfinity)
            return "-Infinity";

        var sign = _negative ? "-" : "";

        if (_coefficient.IsZero)
        {
            if (_exponent < 0)
                return sign + "0." + new string('0', -_exponent);

            return sign + "0";
        }

        var digits = _coefficient.ToString(CultureInfo.InvariantCulture);

        if (_exponent == 0)
            return sign + digits;

        if (_exponent > 0)
            return sign + digits + new string('0', _exponent);

        var split = digits.Length + _exponent;
        if (split > 0)
            return sign + digits.Substring(0, split) + "." + digits.Substring(split);

        return sign + "0." + new string('0', -split) + digits;
    }

    public int CompareTo(FloatDecimal other)
    {
        if (IsNaN)
            return other.IsNaN ? 0 : 1;

        if (other.IsNaN)
            return -1;

        if (IsInfinity || other.IsInfinity)
        {
            if (_kind == other._kind)
                return 0;

            if (_kind == FloatDecimalKind.NegativeInfinity || other._kind == FloatDecimalKind.PositiveInfinity)
                return -1;

            if (_kind == FloatDecimalKind.PositiveInfinity || other._kind == FloatDecimalKind.NegativeInfinity)
                return 1;
        }

        if (IsZero && other.IsZero)
            return 0;

        if (_negative != other._negative)
            return _negative ? -1 : 1;

        var magnitude = CompareMagnitude(this, other);
        return _negative ? -magnitude : magnitude;
    }

    public bool Equals(FloatDecimal other)
    {
        if (IsNaN || other.IsNaN)
            return false;

        return CompareTo(other) == 0;
    }

    public override bool Equals(object? obj) => obj is FloatDecimal other && Equals(other);

    public override int GetHashCode()
    {
        if (IsNaN)
            return HashCode.Combine((int)_kind, 0x7ff1);

        if (IsInfinity)
            return HashCode.Combine((int)_kind, 0x7ff2);

        if (_coefficient.IsZero)
            return 0;

        var (coeff, exp) = Canonicalize(_coefficient, _exponent);
        return HashCode.Combine(false, coeff, exp);
    }

    public static FloatDecimal operator +(FloatDecimal a, FloatDecimal b) =>
        Add(a, b, FloatDecimalFormat.Decfloat34);

    public static FloatDecimal operator -(FloatDecimal a, FloatDecimal b) =>
        Add(a, -b, FloatDecimalFormat.Decfloat34);

    public static FloatDecimal operator *(FloatDecimal a, FloatDecimal b) =>
        Multiply(a, b, FloatDecimalFormat.Decfloat34);

    public static FloatDecimal operator /(FloatDecimal a, FloatDecimal b) =>
        Divide(a, b, FloatDecimalFormat.Decfloat34);

    public static FloatDecimal operator -(FloatDecimal value)
    {
        if (value.IsNaN)
            return value;

        if (value._kind == FloatDecimalKind.PositiveInfinity)
            return NegativeInfinity(value._format);

        if (value._kind == FloatDecimalKind.NegativeInfinity)
            return PositiveInfinity(value._format);

        return new FloatDecimal(value._coefficient, value._exponent, !value._negative, value._kind, value._format);
    }

    public static bool operator ==(FloatDecimal left, FloatDecimal right) => left.Equals(right);
    public static bool operator !=(FloatDecimal left, FloatDecimal right) => !left.Equals(right);
    public static bool operator <(FloatDecimal left, FloatDecimal right) => left.CompareTo(right) < 0;
    public static bool operator <=(FloatDecimal left, FloatDecimal right) => left.CompareTo(right) <= 0;
    public static bool operator >(FloatDecimal left, FloatDecimal right) => left.CompareTo(right) > 0;
    public static bool operator >=(FloatDecimal left, FloatDecimal right) => left.CompareTo(right) >= 0;

    public static FloatDecimal Add(
        FloatDecimal a,
        FloatDecimal b,
        FloatDecimalFormat resultFormat,
        FloatDecimalRounding rounding = FloatDecimalRounding.HalfEven)
    {
        if (a.IsNaN || b.IsNaN)
            return NaN(resultFormat);

        if (a.IsInfinity || b.IsInfinity)
        {
            if (a.IsInfinity && b.IsInfinity)
            {
                if (a._kind != b._kind)
                    return NaN(resultFormat);

                return a._kind == FloatDecimalKind.PositiveInfinity
                    ? PositiveInfinity(resultFormat)
                    : NegativeInfinity(resultFormat);
            }

            if (a.IsInfinity)
            {
                return a._kind == FloatDecimalKind.PositiveInfinity
                    ? PositiveInfinity(resultFormat)
                    : NegativeInfinity(resultFormat);
            }

            return b._kind == FloatDecimalKind.PositiveInfinity
                ? PositiveInfinity(resultFormat)
                : NegativeInfinity(resultFormat);
        }

        var commonExponent = Math.Min(a._exponent, b._exponent);

        var left = a._coefficient * Pow10(a._exponent - commonExponent);
        var right = b._coefficient * Pow10(b._exponent - commonExponent);

        if (a._negative)
            left = -left;

        if (b._negative)
            right = -right;

        var sum = left + right;
        var negative = sum.Sign < 0;

        return CreateFinite(negative, BigInteger.Abs(sum), commonExponent, resultFormat, rounding);
    }

    public static FloatDecimal Multiply(
        FloatDecimal a,
        FloatDecimal b,
        FloatDecimalFormat resultFormat,
        FloatDecimalRounding rounding = FloatDecimalRounding.HalfEven)
    {
        if (a.IsNaN || b.IsNaN)
            return NaN(resultFormat);

        if ((a.IsInfinity && b.IsZero) || (b.IsInfinity && a.IsZero))
            return NaN(resultFormat);

        if (a.IsInfinity || b.IsInfinity)
        {
            var negative = a.IsNegative ^ b.IsNegative;
            return negative ? NegativeInfinity(resultFormat) : PositiveInfinity(resultFormat);
        }

        var sign = a._negative ^ b._negative;
        var coeff = a._coefficient * b._coefficient;
        var exp = a._exponent + b._exponent;

        return CreateFinite(sign, coeff, exp, resultFormat, rounding);
    }

    public static FloatDecimal Divide(
        FloatDecimal a,
        FloatDecimal b,
        FloatDecimalFormat resultFormat,
        FloatDecimalRounding rounding = FloatDecimalRounding.HalfEven)
    {
        if (a.IsNaN || b.IsNaN)
            return NaN(resultFormat);

        if ((a.IsZero && b.IsZero) || (a.IsInfinity && b.IsInfinity))
            return NaN(resultFormat);

        var negative = a.IsNegative ^ b.IsNegative;

        if (b.IsZero)
            return negative ? NegativeInfinity(resultFormat) : PositiveInfinity(resultFormat);

        if (a.IsInfinity)
            return negative ? NegativeInfinity(resultFormat) : PositiveInfinity(resultFormat);

        if (b.IsInfinity)
            return new FloatDecimal(BigInteger.Zero, 0, negative, FloatDecimalKind.Finite, resultFormat);

        var ctx = GetContext(resultFormat);
        var extraDigits = ctx.Precision + 2;
        var scale = Math.Max(0, extraDigits + DigitCount(b._coefficient) - DigitCount(a._coefficient));

        var scaledDividend = a._coefficient * Pow10(scale);
        var quotient = BigInteger.DivRem(scaledDividend, b._coefficient, out var remainder);
        var exp = a._exponent - b._exponent - scale;

        if (!remainder.IsZero)
        {
            quotient = quotient * 10 + 1;
            exp -= 1;
        }

        return CreateFinite(negative, quotient, exp, resultFormat, rounding);
    }

    public static FloatDecimal Abs(FloatDecimal value)
    {
        if (!value.IsFinite)
        {
            if (value._kind == FloatDecimalKind.NegativeInfinity)
                return PositiveInfinity(value._format);

            return value;
        }

        return new FloatDecimal(value._coefficient, value._exponent, false, value._kind, value._format);
    }

    private static FloatDecimal CreateFinite(
        bool negative,
        BigInteger coefficient,
        int exponent,
        FloatDecimalFormat format,
        FloatDecimalRounding rounding = FloatDecimalRounding.HalfEven)
    {
        var ctx = GetContext(format);
        coefficient = BigInteger.Abs(coefficient);

        if (coefficient.IsZero)
        {
            var zeroExponent = Clamp(exponent, ctx.Etiny, ctx.Emax);
            return new FloatDecimal(BigInteger.Zero, zeroExponent, negative, FloatDecimalKind.Finite, format);
        }

        var digits = DigitCount(coefficient);
        if (digits > ctx.Precision)
        {
            var drop = digits - ctx.Precision;
            coefficient = RoundCoefficient(coefficient, drop, negative, rounding);
            exponent += drop;

            if (coefficient.IsZero)
                return new FloatDecimal(BigInteger.Zero, 0, negative, FloatDecimalKind.Finite, format);
        }

        digits = DigitCount(coefficient);
        var adjusted = digits - 1 + exponent;

        if (adjusted > ctx.Emax)
            return negative ? NegativeInfinity(format) : PositiveInfinity(format);

        if (exponent < ctx.Etiny)
        {
            var shift = ctx.Etiny - exponent;
            coefficient = RoundCoefficient(coefficient, shift, negative, rounding);
            exponent = ctx.Etiny;

            if (coefficient.IsZero)
                return new FloatDecimal(BigInteger.Zero, exponent, negative, FloatDecimalKind.Finite, format);

            digits = DigitCount(coefficient);
            if (digits > ctx.Precision)
            {
                coefficient /= 10;
                exponent += 1;
            }
        }

        digits = DigitCount(coefficient);
        adjusted = digits - 1 + exponent;

        if (adjusted > ctx.Emax)
            return negative ? NegativeInfinity(format) : PositiveInfinity(format);

        return new FloatDecimal(coefficient, exponent, negative, FloatDecimalKind.Finite, format);
    }

    private static FloatDecimalRounding DefaultCastRounding(FloatDecimalFormat source, FloatDecimalFormat target)
    {
        if (source == FloatDecimalFormat.Decfloat34 && target == FloatDecimalFormat.Decfloat16)
            return FloatDecimalRounding.HalfUp;

        return FloatDecimalRounding.HalfEven;
    }

    private static Context GetContext(FloatDecimalFormat format) =>
        format switch
        {
            FloatDecimalFormat.Decfloat16 => new Context(16, -383, 384),
            FloatDecimalFormat.Decfloat34 => new Context(34, -6143, 6144),
            _ => throw new ArgumentOutOfRangeException(nameof(format), format, "Unsupported FloatDecimal format.")
        };

    private static int CompareMagnitude(FloatDecimal a, FloatDecimal b)
    {
        if (a._coefficient.IsZero && b._coefficient.IsZero)
            return 0;

        var (ac, ae) = Canonicalize(a._coefficient, a._exponent);
        var (bc, be) = Canonicalize(b._coefficient, b._exponent);

        var adjustedA = DigitCount(ac) - 1 + ae;
        var adjustedB = DigitCount(bc) - 1 + be;

        if (adjustedA != adjustedB)
            return adjustedA.CompareTo(adjustedB);

        var commonExponent = Math.Min(ae, be);
        var left = ac * Pow10(ae - commonExponent);
        var right = bc * Pow10(be - commonExponent);
        return left.CompareTo(right);
    }

    private static (BigInteger coeff, int exp) Canonicalize(BigInteger coeff, int exp)
    {
        coeff = BigInteger.Abs(coeff);
        if (coeff.IsZero)
            return (BigInteger.Zero, 0);

        while ((coeff % 10) == 0)
        {
            coeff /= 10;
            exp++;
        }

        return (coeff, exp);
    }

    private static BigInteger RoundCoefficient(
        BigInteger coefficient,
        int digitsToDrop,
        bool negative,
        FloatDecimalRounding rounding)
    {
        if (digitsToDrop <= 0 || coefficient.IsZero)
            return coefficient;

        var divisor = Pow10(digitsToDrop);
        var quotient = BigInteger.DivRem(coefficient, divisor, out var remainder);

        if (remainder.IsZero)
            return quotient;

        var increment = ShouldIncrement(quotient, remainder, divisor, negative, rounding);
        return increment ? quotient + BigInteger.One : quotient;
    }

    private static bool ShouldIncrement(
        BigInteger quotient,
        BigInteger remainder,
        BigInteger divisor,
        bool negative,
        FloatDecimalRounding rounding)
    {
        switch (rounding)
        {
            case FloatDecimalRounding.Down:
                return false;

            case FloatDecimalRounding.Up:
            case FloatDecimalRounding.AwayFromZero:
                return true;

            case FloatDecimalRounding.Ceiling:
                return !negative;

            case FloatDecimalRounding.Floor:
                return negative;

            case FloatDecimalRounding.HalfUp:
            {
                var twice = remainder * 2;
                return twice >= divisor;
            }

            case FloatDecimalRounding.HalfDown:
            {
                var twice = remainder * 2;
                return twice > divisor;
            }

            case FloatDecimalRounding.HalfEven:
            default:
            {
                var twice = remainder * 2;
                var cmp = twice.CompareTo(divisor);
                if (cmp > 0)
                    return true;

                if (cmp < 0)
                    return false;

                return !quotient.IsEven;
            }
        }
    }

    private static BigInteger Pow10(int power)
    {
        if (power < 0)
            throw new ArgumentOutOfRangeException(nameof(power), "Power must be non-negative.");

        return power == 0 ? BigInteger.One : BigInteger.Pow(10, power);
    }

    private static int DigitCount(BigInteger value)
    {
        value = BigInteger.Abs(value);
        return value.IsZero ? 1 : value.ToString(CultureInfo.InvariantCulture).Length;
    }

    private static string TrimLeadingZeros(string s)
    {
        var i = 0;
        while (i < s.Length && s[i] == '0')
            i++;

        return i == s.Length ? string.Empty : s.Substring(i);
    }

    private static int Clamp(int value, int min, int max) =>
        value < min ? min : value > max ? max : value;

    private static T EnsureFinite<T>(T value) => value;
}

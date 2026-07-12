using System.Collections;
using System.Globalization;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Colossus.Infrastructure.Tiles;

/// <summary>One tile column's Arrow builder plus a boxed-value appender, chosen from the DuckDB column
/// type. Scalars and (float/int) lists are supported; the geometry/part_offsets/triangles lists flow
/// through the list path. Kept boxed on purpose — it is the managed-Arrow write path (the nanoarrow
/// native extension segfaults on DuckDB.NET 1.5.3); batching is a later optimization.
/// The field is built after the rows (<see cref="BuildField"/>): a dictionary column's index width
/// depends on the cardinality it saw.</summary>
internal sealed class ArrowColumnBuilder
{
    public required Func<Field> BuildField { get; init; }
    public required Action<object?> Append { get; init; }
    public required Func<IArrowArray> Build { get; init; }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static ArrowColumnBuilder For(string name, Type clrType, string dataTypeName,
        bool dictionaryEncode = false, IReadOnlyList<string>? canonicalOrder = null)
    {
        bool isList = dataTypeName.EndsWith("[]", StringComparison.Ordinal)
            || (clrType.IsGenericType && clrType.GetGenericTypeDefinition() == typeof(List<>))
            || (clrType.IsArray && clrType != typeof(byte[]));

        if (isList)
        {
            Type elem = clrType.IsArray ? clrType.GetElementType()!
                : clrType.IsGenericType ? clrType.GetGenericArguments()[0]
                : ElementClrType(dataTypeName);
            return List(name, elem, dataTypeName);
        }
        if (dictionaryEncode && clrType == typeof(string)) return DictionaryScalar(name, canonicalOrder);
        return Scalar(name, clrType, dataTypeName);
    }

    // Past this many distinct values the column isn't categorical in any useful sense; the tile falls
    // back to a plain string column (mirrors the client's cap, which scans instead of exploding).
    private const int DictionaryCardinalityCap = 65536;

    /// <summary>String column as Arrow dictionary: integer codes per row + one small value dictionary,
    /// so repeated categories cost bytes once per tile instead of once per row — and the client reads
    /// the codes as a zero-copy typed array. Index width (8/16/32-bit) is chosen from the final
    /// cardinality; over the cap the exact same rows are rebuilt as a plain string column.
    /// When a <paramref name="canonicalOrder"/> is given the dictionary is pre-seeded with it, so a
    /// row's code is its canonical code (format 2) — the client filters/colors by it without remapping.
    /// Unseen values still append after the seed (a no-op for a complete domain).</summary>
    private static ArrowColumnBuilder DictionaryScalar(string name, IReadOnlyList<string>? canonicalOrder = null)
    {
        var codeOf = new Dictionary<string, int>();
        var dict = new List<string>();
        if (canonicalOrder is not null)
            foreach (string s in canonicalOrder)
                if (codeOf.TryAdd(s, dict.Count)) dict.Add(s);
        var codes = new List<int>(); // -1 = null row
        bool overCap() => dict.Count > DictionaryCardinalityCap;

        IArrowType indexType() =>
            dict.Count <= sbyte.MaxValue ? Int8Type.Default : dict.Count <= short.MaxValue ? Int16Type.Default : Int32Type.Default;

        IArrowArray BuildIndices()
        {
            switch (indexType())
            {
                case Int8Type:
                {
                    var b = new Int8Array.Builder();
                    foreach (int c in codes) { if (c < 0) b.AppendNull(); else b.Append((sbyte)c); }
                    return b.Build(default);
                }
                case Int16Type:
                {
                    var b = new Int16Array.Builder();
                    foreach (int c in codes) { if (c < 0) b.AppendNull(); else b.Append((short)c); }
                    return b.Build(default);
                }
                default:
                {
                    var b = new Int32Array.Builder();
                    foreach (int c in codes) { if (c < 0) b.AppendNull(); else b.Append(c); }
                    return b.Build(default);
                }
            }
        }

        StringArray BuildPlain()
        {
            var b = new StringArray.Builder();
            foreach (int c in codes) { if (c < 0) b.AppendNull(); else b.Append(dict[c]); }
            return b.Build(default);
        }

        return new ArrowColumnBuilder
        {
            BuildField = () => new Field(name,
                overCap() ? StringType.Default : new DictionaryType(indexType(), StringType.Default, ordered: false),
                nullable: true),
            Append = v =>
            {
                if (v is null) { codes.Add(-1); return; }
                string s = v.ToString()!;
                if (!codeOf.TryGetValue(s, out int c))
                {
                    c = dict.Count;
                    codeOf.Add(s, c);
                    dict.Add(s);
                }
                codes.Add(c);
            },
            Build = () =>
            {
                if (overCap()) return BuildPlain();
                var values = new StringArray.Builder().AppendRange(dict).Build(default);
                return new DictionaryArray(new DictionaryType(indexType(), StringType.Default, ordered: false), BuildIndices(), values);
            },
        };
    }

    private static ArrowColumnBuilder Scalar(string name, Type t, string dataTypeName)
    {
        if (t == typeof(float)) { var b = new FloatArray.Builder(); return Col(name, FloatType.Default, v => b.Append(Convert.ToSingle(v, Inv)), b.AppendNull, () => b.Build(default)); }
        if (t == typeof(double)) { var b = new DoubleArray.Builder(); return Col(name, DoubleType.Default, v => b.Append(Convert.ToDouble(v, Inv)), b.AppendNull, () => b.Build(default)); }
        if (t == typeof(int) || t == typeof(uint)) { var b = new Int32Array.Builder(); return Col(name, Int32Type.Default, v => b.Append(Convert.ToInt32(v, Inv)), b.AppendNull, () => b.Build(default)); }
        if (t == typeof(long) || t == typeof(ulong)) { var b = new Int64Array.Builder(); return Col(name, Int64Type.Default, v => b.Append(Convert.ToInt64(v, Inv)), b.AppendNull, () => b.Build(default)); }
        if (t == typeof(short) || t == typeof(ushort)) { var b = new Int16Array.Builder(); return Col(name, Int16Type.Default, v => b.Append(Convert.ToInt16(v, Inv)), b.AppendNull, () => b.Build(default)); }
        if (t == typeof(byte) || t == typeof(sbyte)) { var b = new UInt8Array.Builder(); return Col(name, UInt8Type.Default, v => b.Append(Convert.ToByte(v, Inv)), b.AppendNull, () => b.Build(default)); }
        if (t == typeof(bool)) { var b = new BooleanArray.Builder(); return Col(name, BooleanType.Default, v => b.Append(Convert.ToBoolean(v, Inv)), b.AppendNull, () => b.Build(default)); }
        if (t == typeof(string)) { var b = new StringArray.Builder(); return Col(name, StringType.Default, v => b.Append(v.ToString()), b.AppendNull, () => b.Build(default)); }
        if (t == typeof(DateTime) || t == typeof(DateOnly))
        {
            var b = new Date32Array.Builder();
            return Col(name, Date32Type.Default,
                v => b.Append(v is DateOnly d ? d : DateOnly.FromDateTime(Convert.ToDateTime(v, Inv))),
                b.AppendNull, () => b.Build(default));
        }
        throw new NotSupportedException($"column '{name}': unsupported tile type '{dataTypeName}' ({t}).");
    }

    private static ArrowColumnBuilder Col(string name, IArrowType type, Action<object> appendValue, Func<IArrowArrayBuilder> appendNull, Func<IArrowArray> build)
        => new()
        {
            BuildField = () => new Field(name, type, nullable: true),
            Append = v => { if (v is null) appendNull(); else appendValue(v); },
            Build = build,
        };

    private static ArrowColumnBuilder List(string name, Type elem, string dataTypeName)
    {
        IArrowType valueType = ArrowScalarType(elem, dataTypeName);
        var lb = new ListArray.Builder(valueType);
        Action<object> appendElem = AppendElem(lb.ValueBuilder, valueType, name);
        return new ArrowColumnBuilder
        {
            BuildField = () => new Field(name, new ListType(new Field("item", valueType, true)), nullable: true),
            Append = v =>
            {
                if (v is null) { lb.AppendNull(); return; }
                lb.Append();
                foreach (object? e in (IEnumerable)v) if (e is not null) appendElem(e);
            },
            Build = () => lb.Build(default),
        };
    }

    private static Action<object> AppendElem(IArrowArrayBuilder valueBuilder, IArrowType valueType, string name) => valueType switch
    {
        FloatType => e => ((FloatArray.Builder)valueBuilder).Append(Convert.ToSingle(e, Inv)),
        DoubleType => e => ((DoubleArray.Builder)valueBuilder).Append(Convert.ToDouble(e, Inv)),
        Int32Type => e => ((Int32Array.Builder)valueBuilder).Append(Convert.ToInt32(e, Inv)),
        Int64Type => e => ((Int64Array.Builder)valueBuilder).Append(Convert.ToInt64(e, Inv)),
        _ => throw new NotSupportedException($"column '{name}': unsupported list element type '{valueType}'."),
    };

    private static IArrowType ArrowScalarType(Type elem, string dataTypeName)
    {
        if (elem == typeof(float)) return FloatType.Default;
        if (elem == typeof(double)) return DoubleType.Default;
        if (elem == typeof(int) || elem == typeof(uint)) return Int32Type.Default;
        if (elem == typeof(long) || elem == typeof(ulong)) return Int64Type.Default;
        return ElementClrType(dataTypeName) switch
        {
            var t when t == typeof(float) => FloatType.Default,
            var t when t == typeof(int) => Int32Type.Default,
            _ => throw new NotSupportedException($"unsupported list type '{dataTypeName}'."),
        };
    }

    private static Type ElementClrType(string dataTypeName)
    {
        string b = dataTypeName.Replace("[]", "").Trim().ToUpperInvariant();
        return b switch
        {
            "FLOAT" or "REAL" => typeof(float),
            "DOUBLE" => typeof(double),
            "INTEGER" or "INT" or "INT32" => typeof(int),
            "BIGINT" or "INT64" => typeof(long),
            _ => typeof(object),
        };
    }
}

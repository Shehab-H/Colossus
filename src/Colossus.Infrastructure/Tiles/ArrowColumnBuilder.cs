using System.Collections;
using System.Globalization;
using Apache.Arrow;
using Apache.Arrow.Types;

namespace Colossus.Infrastructure.Tiles;

/// <summary>One tile column's Arrow builder plus a boxed-value appender, chosen from the DuckDB column
/// type. Scalars and (float/int) lists are supported; the geometry/part_offsets/triangles lists flow
/// through the list path. Kept boxed on purpose — it is the managed-Arrow write path (the nanoarrow
/// native extension segfaults on DuckDB.NET 1.5.3); batching is a later optimization.</summary>
internal sealed class ArrowColumnBuilder
{
    public required Field Field { get; init; }
    public required Action<object?> Append { get; init; }
    public required Func<IArrowArray> Build { get; init; }

    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static ArrowColumnBuilder For(string name, Type clrType, string dataTypeName)
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
        return Scalar(name, clrType, dataTypeName);
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
            Field = new Field(name, type, nullable: true),
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
            Field = new Field(name, new ListType(new Field("item", valueType, true)), nullable: true),
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

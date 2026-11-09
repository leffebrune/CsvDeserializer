using System;
using System.Buffers;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

/// <summary>
/// Provides functionality to deserialize CSV content into a list of objects.
/// </summary>
public static class CsvDeserializer
{
    /// <summary>
    /// Specifies which members (fields or properties) to consider for binding.
    /// </summary>
    public enum BindingScope
    {
        Field,
        Property,
        Both
    }

    /// <summary>
    /// Reports a CSV syntax or cell conversion error with its one-based record and column.
    /// The header is record 1; blank records count, and quoted newlines stay in the same record.
    /// </summary>
    public sealed class CsvParseException : FormatException
    {
        public int RowNumber { get; }
        public int ColumnNumber { get; }
        public string ColumnName { get; }

        internal CsvParseException(int rowNumber, int columnNumber, string columnName, Exception innerException)
            : base($"CSV error at row {rowNumber}, column {columnNumber}" +
                (columnName == null ? "" : $" ('{columnName}')") + $": {innerException.Message}", innerException)
        {
            RowNumber = rowNumber;
            ColumnNumber = columnNumber;
            ColumnName = columnName;
        }
    }

    /// <summary>
    /// Delegate for custom value conversion from a CSV field string to a target type.
    /// </summary>
    /// <typeparam name="T">The target type to convert to.</typeparam>
    /// <param name="csvFieldValue">The ReadOnlySpan<char> representing the CSV field's content.</param>
    /// <returns>The converted value of type T.</returns>
    public delegate T CustomValueConverter<T>(ReadOnlySpan<char> csvFieldValue);

    /// <summary>
    /// Internal delegate used to store custom converters in a non-generic way.
    /// This allows the dictionary to hold converters for different types.
    /// </summary>
    internal delegate object InternalValueConverter(ReadOnlySpan<char> csvFieldValue);

    /// <summary>
    /// Gets or sets the scope for member binding (Fields, Properties, or Both).
    /// Defaults to BindingScope.Both.
    /// </summary>
    public static BindingScope MemberBindingScope { get; set; } = BindingScope.Both;

    /// <summary>
    /// Gets or sets a value indicating whether to preserve a string member's existing C# initialized value
    /// when its corresponding CSV cell is empty.
    /// If false (default), empty CSV cells for string members will be set to string.Empty.
    /// If true, the member's existing value (e.g., a C# default initializer or null if uninitialized) is preserved.
    /// </summary>
    public static bool PreserveStringMemberOnEmptyCsv { get; set; } = false;

    /// <summary>
    /// Gets or sets the character used as a delimiter between fields in a CSV row.
    /// Defaults to ','.
    /// </summary>
    public static char Delimiter { get; set; } = ','; // Corrected typo: Delimeter -> Delimiter

    private static readonly Dictionary<Type, InternalValueConverter> _customTypeConverters = new();


    #region Row Enumerator
    /// <summary>
    /// Enumerates rows from CSV content using ReadOnlySpan<char> to minimize allocations.
    /// </summary>
    private ref struct RowEnumerator
    {
        private ReadOnlySpan<char> _remaining;
        private ReadOnlySpan<char> _currentRow;

        public RowEnumerator(ReadOnlySpan<char> csvContent)
        {
            _remaining = csvContent;
            _currentRow = ReadOnlySpan<char>.Empty;
        }

        public readonly ReadOnlySpan<char> Current => _currentRow;

        public bool MoveNext()
        {
            if (_remaining.IsEmpty)
            {
                _currentRow = ReadOnlySpan<char>.Empty;
                return false;
            }

            var inQuotes = false;
            var atFieldStart = true;
            var lineEnd = -1;

            for (var i = 0; i < _remaining.Length; i++)
            {
                var c = _remaining[i];

                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < _remaining.Length && _remaining[i + 1] == '"')
                            i++; // Only quotes inside a quoted field can be escaped.
                        else
                            inQuotes = false;
                    }
                }
                else if (c == '"' && atFieldStart)
                {
                    inQuotes = true;
                    atFieldStart = false;
                }
                else if (c == '\n' || c == '\r')
                {
                    lineEnd = i;
                    break;
                }
                else
                {
                    atFieldStart = c == Delimiter;
                }
            }

            if (lineEnd == -1) // No newline found; this is the last line
            {
                _currentRow = _remaining;
                _remaining = ReadOnlySpan<char>.Empty;
            }
            else
            {
                _currentRow = _remaining[..lineEnd];
                // Advance pointer past the newline character(s)
                if (_remaining[lineEnd] == '\r' && lineEnd + 1 < _remaining.Length && _remaining[lineEnd + 1] == '\n')
                {
                    _remaining = _remaining[(lineEnd + 2)..]; // CRLF
                }
                else
                {
                    _remaining = _remaining[(lineEnd + 1)..]; // CR or LF
                }
            }
            return true;
        }

        public readonly RowEnumerator GetEnumerator() => this;
    }
    #endregion

    #region Column Enumerator
    /// <summary>
    /// Enumerates columns within a single CSV row using ReadOnlySpan<char>.
    /// </summary>
    private ref struct ColumnEnumerator
    {
        private ReadOnlySpan<char> _remaining;
        private ReadOnlySpan<char> _currentColumn;
        private bool _hasMoreColumns;

        public ColumnEnumerator(ReadOnlySpan<char> csvRow)
        {
            _remaining = csvRow;
            _currentColumn = ReadOnlySpan<char>.Empty;
            _hasMoreColumns = !csvRow.IsEmpty;
        }

        public readonly ReadOnlySpan<char> Current => _currentColumn;

        public bool MoveNext()
        {
            if (!_hasMoreColumns)
            {
                _currentColumn = ReadOnlySpan<char>.Empty;
                return false;
            }

            if (!_remaining.IsEmpty && _remaining[0] == '"')
            {
                for (var i = 1; i < _remaining.Length; i++)
                {
                    if (_remaining[i] != '"')
                        continue;
                    if (i + 1 < _remaining.Length && _remaining[i + 1] == '"')
                    {
                        i++;
                        continue;
                    }

                    var afterQuote = i + 1;
                    if (afterQuote < _remaining.Length && _remaining[afterQuote] != Delimiter)
                        throw new FormatException("Expected a delimiter or end of row after the closing quote.");

                    _currentColumn = _remaining[1..i];
                    _hasMoreColumns = afterQuote < _remaining.Length;
                    _remaining = _hasMoreColumns ? _remaining[(afterQuote + 1)..] : ReadOnlySpan<char>.Empty;
                    return true;
                }
                throw new FormatException("CSV format error: unmatched quotes.");
            }

            var fieldEnd = _remaining.IndexOf(Delimiter);
            _currentColumn = fieldEnd < 0 ? _remaining : _remaining[..fieldEnd];
            if (_currentColumn.IndexOf('"') >= 0)
                throw new FormatException("A quote in an unquoted field must be enclosed and escaped with double quotes.");

            // A delimiter leaves one more column, even when its content is empty.
            _hasMoreColumns = fieldEnd >= 0;
            _remaining = _hasMoreColumns ? _remaining[(fieldEnd + 1)..] : ReadOnlySpan<char>.Empty;
            return true;
        }

        public readonly ColumnEnumerator GetEnumerator() => this;
    }

    private static bool MoveNextColumn(ref ColumnEnumerator enumerator, int rowNumber, int columnNumber, string columnName)
    {
        try
        {
            return enumerator.MoveNext();
        }
        catch (FormatException exception)
        {
            throw new CsvParseException(rowNumber, columnNumber, columnName, exception);
        }
    }
    #endregion

    #region Accessor
    /// <summary>
    /// Helper class to provide unified access for setting values on fields or properties via reflection.
    /// </summary>
    private class Accessor
    {
        public Accessor(FieldInfo fieldInfo)
        {
            FieldInfo = fieldInfo;
            PropertyInfo = null;
        }
        public Accessor(PropertyInfo propertyInfo)
        {
            PropertyInfo = propertyInfo;
            FieldInfo = null;
        }

        public readonly FieldInfo FieldInfo;
        public readonly PropertyInfo PropertyInfo;
        public string Name => FieldInfo?.Name ?? PropertyInfo?.Name;
        public Type MemberType => FieldInfo?.FieldType ?? PropertyInfo?.PropertyType;

        public void SetValue(object instance, object value)
        {
            if (FieldInfo != null)
            {
                FieldInfo.SetValue(instance, value);
            }
            else
            {
                PropertyInfo?.SetValue(instance, value);
            }
        }
    }
    #endregion

    /// <summary>
    /// Converts a CSV field span (which might contain escaped double quotes like "")
    /// into a regular string (where "" becomes ").
    /// </summary>
    private static string GetUnescapedString(ReadOnlySpan<char> sourceSpan)
    {
        // If the span is too short to contain escaped quotes or doesn't contain quotes at all,
        // convert to string directly. IndexOf is a quick check.
        if (sourceSpan.Length < 2 || sourceSpan.IndexOf('"') < 0)
            return sourceSpan.ToString();

        const int stackAllocThreshold = 256; // Prefer stack allocation for small buffers
        char[] charBuffer = null;

        Span<char> buffer = sourceSpan.Length > stackAllocThreshold
            ? (charBuffer = ArrayPool<char>.Shared.Rent(sourceSpan.Length))
            : stackalloc char[sourceSpan.Length];

        int destIdx = 0;
        for (int i = 0; i < sourceSpan.Length; i++)
        {
            if (sourceSpan[i] == '"' && i + 1 < sourceSpan.Length && sourceSpan[i + 1] == '"')
            {
                buffer[destIdx++] = '"';
                i++; // Skip the second quote of the escaped pair
            }
            else
            {
                buffer[destIdx++] = sourceSpan[i];
            }
        }
        var result = new string(buffer[..destIdx]);

        if (charBuffer != null)
        {
            ArrayPool<char>.Shared.Return(charBuffer);
        }
        return result;
    }

    /// <summary>
    /// Sets a value on an object's member, converting the CSV string data to the member's type.
    /// </summary>
    private static void SetValue(object instance, Accessor accessor, ReadOnlySpan<char> dataFromCsv)
    {
        if (accessor == null) // No corresponding member in the target type for this CSV column.
            return;

        var memberType = accessor.MemberType;
        if (_customTypeConverters.TryGetValue(memberType, out var customConverterFunc))
        {
            accessor.SetValue(instance, customConverterFunc(dataFromCsv));
            return;
        }

        if (memberType == typeof(string))
        {
            accessor.SetValue(instance, GetUnescapedString(dataFromCsv));
            return;
        }
        else if (memberType == typeof(int))
        {
            if (int.TryParse(dataFromCsv, NumberStyles.Integer, CultureInfo.InvariantCulture, out var intValue))
                accessor.SetValue(instance, intValue);
            else
                throw new FormatException($"Failed to parse '{dataFromCsv.ToString()}' as int.");
        }
        else if (memberType == typeof(long))
        {
            if (long.TryParse(dataFromCsv, NumberStyles.Integer, CultureInfo.InvariantCulture, out var longValue))
                accessor.SetValue(instance, longValue);
            else
                throw new FormatException($"Failed to parse '{dataFromCsv.ToString()}' as long.");
        }
        else if (memberType == typeof(float))
        {
            if (float.TryParse(dataFromCsv, NumberStyles.Float, CultureInfo.InvariantCulture, out var floatValue)
                && !float.IsNaN(floatValue) && !float.IsInfinity(floatValue))
                accessor.SetValue(instance, floatValue);
            else
                throw new FormatException($"Failed to parse '{dataFromCsv.ToString()}' as float.");
        }
        else if (memberType == typeof(double))
        {
            if (double.TryParse(dataFromCsv, NumberStyles.Float, CultureInfo.InvariantCulture, out var doubleValue)
                && !double.IsNaN(doubleValue) && !double.IsInfinity(doubleValue))
                accessor.SetValue(instance, doubleValue);
            else
                throw new FormatException($"Failed to parse '{dataFromCsv.ToString()}' as double.");
        }
        else if (memberType == typeof(bool))
        {
            if (bool.TryParse(dataFromCsv, out var boolValue))
                accessor.SetValue(instance, boolValue);
            else
                throw new FormatException($"Failed to parse '{dataFromCsv.ToString()}' as bool.");
        }
        else if (memberType == typeof(byte))
        {
            if (byte.TryParse(dataFromCsv, NumberStyles.Integer, CultureInfo.InvariantCulture, out var byteValue))
                accessor.SetValue(instance, byteValue);
            else
                throw new FormatException($"Failed to parse '{dataFromCsv.ToString()}' as byte.");
        }
        else if (memberType == typeof(short))
        {
            if (short.TryParse(dataFromCsv, NumberStyles.Integer, CultureInfo.InvariantCulture, out var shortValue))
                accessor.SetValue(instance, shortValue);
            else
                throw new FormatException($"Failed to parse '{dataFromCsv.ToString()}' as short.");
        }
        else if (memberType.IsEnum)
        {
            var fieldStringValue = GetUnescapedString(dataFromCsv);
            // Retain case-insensitive names, flag combinations and underlying integer values.
            if (Enum.TryParse(memberType, fieldStringValue, true, out var enumValue))
                accessor.SetValue(instance, enumValue);
            else
                throw new FormatException($"Failed to parse '{fieldStringValue}' as enum '{memberType.FullName}'.");
        }
        else
        {
            throw new NotSupportedException($"Target member type '{memberType.FullName}' is not supported by default. Register a custom converter for it.");
        }
    }

    private static void SetColumnValue(object instance, Accessor accessor, ReadOnlySpan<char> dataFromCsv,
        int rowNumber, int columnNumber, string columnName)
    {
        try
        {
            if (!dataFromCsv.IsEmpty)
                SetValue(instance, accessor, dataFromCsv);
            else if (accessor.MemberType == typeof(string) && !PreserveStringMemberOnEmptyCsv)
                accessor.SetValue(instance, string.Empty);
        }
        catch (Exception exception)
        {
            throw new CsvParseException(rowNumber, columnNumber, columnName, exception);
        }
    }

    /// <summary>
    /// Deserializes CSV content into a list of objects of type T.
    /// Built-in numbers use InvariantCulture with no thousands separators and finite floating-point values.
    /// Invalid CSV syntax or nonempty cell conversion failures stop deserialization with location details.
    /// </summary>
    /// <typeparam name="T">The type of objects to create. Must be a class with a parameterless constructor.</typeparam>
    /// <param name="csvContent">The string containing the CSV data.</param>
    /// <returns>A list of deserialized objects of type T.</returns>
    /// <exception cref="CsvParseException">CSV syntax or cell conversion is invalid.</exception>
    public static List<T> Deserialize<T>(string csvContent) where T : class, new()
    {
        var result = new List<T>();
        var type = typeof(T); // Reflection: Get type information for T
        var membersInTarget = new List<Accessor>(); // Stores accessors for all relevant members in T

        // Determine binding flags based on MemberBindingScope
        if (MemberBindingScope == BindingScope.Field || MemberBindingScope == BindingScope.Both)
        {
            var fields = type.GetFields(BindingFlags.Public | BindingFlags.Instance);
            foreach (var field in fields)
            {
                membersInTarget.Add(new Accessor(field));
            }
        }
        if (MemberBindingScope == BindingScope.Property || MemberBindingScope == BindingScope.Both)
        {
            var properties = type.GetProperties(BindingFlags.Public | BindingFlags.Instance);
            foreach (var property in properties)
            {
                if (property.CanWrite) // Only include writable properties
                    membersInTarget.Add(new Accessor(property));
            }
        }

        // This list will hold accessors in the order of CSV headers.
        var orderedAccessors = new List<Accessor>();
        var columnNames = new List<string>();

        var rowEnumerator = new RowEnumerator(csvContent.AsSpan());
        if (!rowEnumerator.MoveNext()) // No rows at all, or empty content
            return result;

        // Process the first row as header
        var headerEnumerator = new ColumnEnumerator(rowEnumerator.Current);
        while (MoveNextColumn(ref headerEnumerator, 1, orderedAccessors.Count + 1, null))
        {
            var memberName = GetUnescapedString(headerEnumerator.Current); // Process headers once.
            columnNames.Add(memberName.Length == 0 ? null : memberName);
            // Find corresponding member in target type (case-insensitive)
            var accessor = memberName.Length == 0 ? null :
                membersInTarget.Find(f => f.Name.Equals(memberName, StringComparison.OrdinalIgnoreCase));
            orderedAccessors.Add(accessor); // Add found accessor (or null if not found)
        }

        // Process data rows
        var rowNumber = 1;
        while (rowEnumerator.MoveNext())
        {
            rowNumber++;
            var rowSpan = rowEnumerator.Current;
            if (rowSpan.IsEmpty)
                continue; // Skip empty lines between data rows

            var columnEnumerator = new ColumnEnumerator(rowSpan);
            var instance = new T(); // Create a new instance of the target type for each row
            var fieldIndex = 0;

            // Validate every column, including unmapped columns and columns beyond the header.
            while (true)
            {
                var columnName = fieldIndex < columnNames.Count ? columnNames[fieldIndex] : null;
                if (!MoveNextColumn(ref columnEnumerator, rowNumber, fieldIndex + 1, columnName))
                    break;

                var accessor = fieldIndex < orderedAccessors.Count ? orderedAccessors[fieldIndex] : null;
                if (accessor != null)
                    SetColumnValue(instance, accessor, columnEnumerator.Current, rowNumber, fieldIndex + 1, columnName);
                fieldIndex++;
            }
            result.Add(instance);
        }
        return result;
    }

    /// <summary>
    /// Registers a custom type converter for a specific target type.
    /// </summary>
    /// <typeparam name="T">The target type for which the converter is being registered.</typeparam>
    /// <param name="converter">The custom converter function.</param>
    /// <exception cref="ArgumentNullException">Thrown if the converter is null.</exception>
    /// <exception cref="InvalidOperationException">Thrown if a converter for the type is already registered.</exception>
    public static void RegisterCustomTypeConverter<T>(CustomValueConverter<T> converter)
    {
        if (converter == null)
            throw new ArgumentNullException(nameof(converter), "Converter cannot be null.");

        var type = typeof(T);
        if (_customTypeConverters.ContainsKey(type))
            // Behavior for re-registration: currently throws. Could allow update or have TryRegister.
            throw new InvalidOperationException($"A custom converter for type '{type.FullName}' is already registered.");

        _customTypeConverters[type] = csvFieldValue => converter(csvFieldValue);
    }

    /// <summary>
    /// Clears all registered custom type converters.
    /// </summary>
    public static void ClearCustomTypeConverters()
    {
        _customTypeConverters.Clear();
    }
}

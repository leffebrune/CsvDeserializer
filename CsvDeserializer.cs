using System;
using System.Buffers;
using System.Collections.Generic;
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
            var lineEnd = -1;

            for (var i = 0; i < _remaining.Length; i++)
            {
                var c = _remaining[i];

                if (c == '"')
                {
                    if (i + 1 < _remaining.Length && _remaining[i + 1] == '"')
                    {
                        i++; // Skip escaped double quote (e.g., "")
                    }
                    else
                    {
                        inQuotes = !inQuotes;
                    }
                }
                else if (!inQuotes && (c == '\n' || c == '\r'))
                {
                    lineEnd = i;
                    break;
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
        private bool _currentColumnIsQuoted;

        public ColumnEnumerator(ReadOnlySpan<char> csvRow)
        {
            _remaining = csvRow;
            _currentColumn = ReadOnlySpan<char>.Empty;
            _currentColumnIsQuoted = false;
        }

        public readonly ReadOnlySpan<char> Current => _currentColumn;
        public readonly bool CurrentColumnIsQuoted => _currentColumnIsQuoted;

        public bool MoveNext()
        {
            if (_remaining.IsEmpty)
            {
                _currentColumn = ReadOnlySpan<char>.Empty;
                return false;
            }

            _currentColumnIsQuoted = false;
            var inQuotes = false;
            var fieldEnd = -1;    // Index of the character that signifies the end of the current field.
            var fieldStart = 0;   // Index where the actual field content starts (relevant for quoted fields).

            if (_remaining.Length > 0 && _remaining[0] == '"')
            {
                inQuotes = true;
                _currentColumnIsQuoted = true;
                fieldStart = 1; // Content starts after the opening quote
            }

            for (var i = fieldStart; i < _remaining.Length; i++)
            {
                var c = _remaining[i];
                if (inQuotes)
                {
                    if (c == '"')
                    {
                        if (i + 1 < _remaining.Length && _remaining[i + 1] == '"')
                        {
                            i++; // Skip escaped double quote
                        }
                        else
                        {
                            fieldEnd = i; // This is the closing quote for the field
                            break;
                        }
                    }
                }
                else // Not inside a quoted field
                {
                    if (c == Delimiter)
                    {
                        fieldEnd = i; // Delimiter marks the end of the field
                        break;
                    }
                }
            }

            if (fieldEnd == -1) // Field extends to the end of the line
            {
                if (_currentColumnIsQuoted)
                {
                    // A quoted field must end with a quote.
                    if (_remaining.Length > fieldStart && _remaining[^1] == '"') // ^1 is C# 8.0 index from end
                    {
                        _currentColumn = _remaining[fieldStart..^1]; // Exclude the surrounding quotes
                    }
                    else
                    {
                        // Malformed: started with a quote but doesn't end with one or is unclosed.
                        throw new FormatException("CSV format error: unmatched quotes.");
                    }
                }
                else
                {
                    _currentColumn = _remaining;
                }
                _remaining = ReadOnlySpan<char>.Empty; // All content consumed
            }
            else // Delimiter or closing quote for the field was found
            {
                if (_currentColumnIsQuoted)
                {
                    // fieldEnd is the position of the closing quote
                    _currentColumn = _remaining[fieldStart..fieldEnd];

                    // After a closing quote, the next character should ideally be a delimiter or end of line.
                    int charAfterQuoteIdx = fieldEnd + 1;
                    if (charAfterQuoteIdx < _remaining.Length && _remaining[charAfterQuoteIdx] == Delimiter)
                    {
                        _remaining = _remaining[(charAfterQuoteIdx + 1)..]; // Advance past the delimiter
                    }
                    else // End of line, or no delimiter found after quoted field.
                    {
                        _remaining = ReadOnlySpan<char>.Empty;
                    }
                }
                else
                {
                    // fieldEnd is the position of the delimiter
                    _currentColumn = _remaining[..fieldEnd];
                    _remaining = _remaining[(fieldEnd + 1)..]; // Advance past the delimiter
                }
            }

            // Handles cases like an empty line or trailing commas to prevent infinite loops.
            // If _remaining became empty, _currentColumn is empty (e.g. after a trailing comma),
            // it was not quoted, and we didn't advance fieldStart or find a fieldEnd, then stop.
            if (_remaining.IsEmpty && _currentColumn.IsEmpty && !_currentColumnIsQuoted && fieldStart == 0 && fieldEnd == -1)
                return false;
            return true;
        }

        public readonly ColumnEnumerator GetEnumerator() => this;
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
            if (int.TryParse(dataFromCsv, out var intValue)) // Uses current culture
                accessor.SetValue(instance, intValue);
            else
                throw new FormatException($"Failed to parse '{dataFromCsv.ToString()}' as int.");
        }
        else if (memberType == typeof(long))
        {
            if (long.TryParse(dataFromCsv, out var longValue))
                accessor.SetValue(instance, longValue);
            else
                throw new FormatException($"Failed to parse '{dataFromCsv.ToString()}' as long.");
        }
        else if (memberType == typeof(float))
        {
            if (float.TryParse(dataFromCsv, out var floatValue))
                accessor.SetValue(instance, floatValue);
            else
                throw new FormatException($"Failed to parse '{dataFromCsv.ToString()}' as float.");
        }
        else if (memberType == typeof(double))
        {
            if (double.TryParse(dataFromCsv, out var doubleValue))
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
            if (byte.TryParse(dataFromCsv, out var byteValue))
                accessor.SetValue(instance, byteValue);
            else
                throw new FormatException($"Failed to parse '{dataFromCsv.ToString()}' as byte.");
        }
        else if (memberType == typeof(short))
        {
            if (short.TryParse(dataFromCsv, out var shortValue))
                accessor.SetValue(instance, shortValue);
            else
                throw new FormatException($"Failed to parse '{dataFromCsv.ToString()}' as short.");
        }
        else if (memberType.IsEnum)
        {
            var fieldStringValue = GetUnescapedString(dataFromCsv);
            bool parsedSuccessfully = false;

            // First, try parsing the string value as an enum member name (case-insensitive).
            if (!string.IsNullOrEmpty(fieldStringValue))
            {
                if (Enum.TryParse(memberType, fieldStringValue, true, out var enumValueAsObject))
                {
                    accessor.SetValue(instance, enumValueAsObject);
                    parsedSuccessfully = true;
                }
            }

            // If parsing by name failed or the string was empty, try parsing as underlying numeric value.
            // This 'else' block was part of the user's original structure for enum parsing.
            if (!parsedSuccessfully) // This condition ensures numeric parsing is an alternative.
            {
                var underlyingEnumType = Enum.GetUnderlyingType(memberType);
                if (underlyingEnumType == typeof(int))
                {
                    if (int.TryParse(dataFromCsv, out var numericValue))
                    {
                        accessor.SetValue(instance, Enum.ToObject(memberType, numericValue));
                        // parsedSuccessfully = true; // Can be set if tracking is needed beyond this block
                    }
                }
                else if (underlyingEnumType == typeof(long))
                {
                    if (long.TryParse(dataFromCsv, out var numericValue))
                    {
                        accessor.SetValue(instance, Enum.ToObject(memberType, numericValue));
                        // parsedSuccessfully = true;
                    }
                }
                else
                {
                    throw new NotSupportedException($"Enum '{memberType.FullName}' with underlying type '{underlyingEnumType.FullName}' is not supported for default numeric parsing.");
                }
            }
            // If !parsedSuccessfully after all attempts, the member remains unchanged or default.
            // Add logging here if desired for parse failures.
        }
        else
        {
            throw new NotSupportedException($"Target member type '{memberType.FullName}' is not supported by default. Register a custom converter for it.");
        }
    }

    /// <summary>
    /// Deserializes CSV content into a list of objects of type T.
    /// </summary>
    /// <typeparam name="T">The type of objects to create. Must be a class with a parameterless constructor.</typeparam>
    /// <param name="csvContent">The string containing the CSV data.</param>
    /// <returns>A list of deserialized objects of type T.</returns>
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

        var rowEnumerator = new RowEnumerator(csvContent.AsSpan());
        if (!rowEnumerator.MoveNext()) // No rows at all, or empty content
            return result;

        // Process the first row as header
        var headerEnumerator = new ColumnEnumerator(rowEnumerator.Current);
        while (headerEnumerator.MoveNext())
        {
            var columnHeaderSpan = headerEnumerator.Current;
            if (columnHeaderSpan.IsEmpty) // Handle empty header names if necessary (e.g., add null accessor)
            {
                orderedAccessors.Add(null);
                continue;
            }

            var memberName = columnHeaderSpan.ToString(); // Allocation for header name (processed once)
            // Find corresponding member in target type (case-insensitive)
            var accessor = membersInTarget.Find(f => f.Name.Equals(memberName, StringComparison.OrdinalIgnoreCase));
            orderedAccessors.Add(accessor); // Add found accessor (or null if not found)
        }

        // Process data rows
        while (rowEnumerator.MoveNext())
        {
            var rowSpan = rowEnumerator.Current;
            if (rowSpan.IsEmpty)
                continue; // Skip empty lines between data rows

            var columnEnumerator = new ColumnEnumerator(rowSpan);
            var instance = new T(); // Create a new instance of the target type for each row
            var fieldIndex = 0;

            // Iterate through columns in the current data row
            while (columnEnumerator.MoveNext() && fieldIndex < orderedAccessors.Count)
            {
                var columnDataSpan = columnEnumerator.Current;
                Accessor currentAccessor = orderedAccessors[fieldIndex];

                if (currentAccessor != null) // If there's a mapped member for this CSV column
                {
                    if (!columnDataSpan.IsEmpty)
                    {
                        SetValue(instance, currentAccessor, columnDataSpan);
                    }
                    else // CSV cell is empty
                    {
                        // Handle empty string fields based on PreserveStringMemberOnEmptyCsv setting
                        if (currentAccessor.MemberType == typeof(string))
                        {
                            if (!PreserveStringMemberOnEmptyCsv) // Default is false, so this block executes by default
                            {
                                currentAccessor.SetValue(instance, string.Empty);
                            }
                            // If PreserveStringMemberOnEmptyCsv is true, do nothing; C# initialized value is kept.
                        }
                    }
                }
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
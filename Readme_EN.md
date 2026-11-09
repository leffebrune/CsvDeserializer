# CsvDeserializer for Unity

## Overview

`CsvDeserializer` is a lightweight utility for Unity projects to deserialize CSV string data into a `List<T>` of C# objects. It is designed to be fast and to minimize garbage collection.

The deserializer maps CSV columns to public fields or properties of your C# class based on the header row (case-insensitive).

## Design Intent

This library was written with more emphasis on portability and maintainability than on chasing a narrow, highly specialized fast path.

The following constraints were intentional:

1. It does not use runtime code generation such as Expression Trees.
   Those approaches can become problematic in environments that enforce AoT (Ahead-of-Time) compilation, such as iOS.
2. It does not require schema attributes on the data types being parsed.
   The small performance gain was not worth the extra maintenance burden on the model definitions.
3. It does not use Source Generators.
   They raise the learning curve and make it harder for users to understand or debug generated code.

As a result, the goal of this project is to stay within the boundaries of pure `string` parsing and `reflection`, while still being as fast and lightweight as possible.

## Basic Usage

1.  **Define your data class:**

    ```csharp
    public class MyData { public string Name; public int Score; /* ... more fields/properties */ }
    ```

2.  **Deserialize (assuming `csvString` contains your CSV data):**

    ```csharp
    List<MyData> dataList = CsvDeserializer.Deserialize<MyData>(csvString);
    ```

## Advanced Features & Details

This utility supports various features, including:

*   Custom delimiters.
*   Custom type converters for complex data types.
*   Configurable binding scope (fields, properties, or both).
*   Options for preserving C# default string values on empty CSV cells.
*   Adherence to **RFC-4180** for CSV parsing, ensuring proper handling of quoted fields, escaped characters, and line breaks.
*   Error handling for type mismatches and CSV format issues.

For detailed examples demonstrating these features, error handling, and behavior with mismatched columns, **please refer to the `CsvDeserializerDemo.cs` script provided in the demonstration files.** These examples will guide you through the full capabilities of the `CsvDeserializer`.

## Numbers and error handling

* Built-in numeric conversions use `CultureInfo.InvariantCulture`. The decimal separator is `.`, so `1.5` remains `1.5` even under `de-DE`. Thousands separators and decimal commas are rejected.
* Integers use `NumberStyles.Integer`; floating-point values use `NumberStyles.Float`. Surrounding whitespace, signs, and floating-point exponents are accepted. Overflow, `NaN`, and infinity are rejected.
* Enums accept case-insensitive names, name combinations, and integers within the underlying type's range. Undefined integer values remain accepted for compatibility; invalid names and out-of-range values are errors.
* Quotes may open only at the start of a cell. A closing quote must be followed by a delimiter or the end of the record; embedded quotes must be escaped as `""`. Syntax is checked even in unmapped columns and columns beyond the header.
* Syntax and cell conversion errors immediately throw `CsvDeserializer.CsvParseException`, derived from `FormatException`. Inspect `RowNumber`, `ColumnNumber`, and `ColumnName` for the location and `InnerException` for the original cause. `ColumnName` is `null` while parsing headers or for columns without a header.

Row and column numbers are one-based, including the header as row 1. Blank records count toward row numbers; line breaks inside quoted cells stay within the same CSV record. For example, `Name,Score\n"Alice"garbage,42` fails at `row 2, column 1 ('Name')`.

Empty string cells default to `string.Empty`; set `PreserveStringMemberOnEmptyCsv = true` to keep their initializers. Empty non-string cells and missing columns keep their initializers. Custom converters control their own culture and validation policy; exceptions they throw also receive row and column context.

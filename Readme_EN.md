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

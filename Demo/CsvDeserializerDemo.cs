using UnityEngine;

namespace CsvDeserializerDemo
{
    public class CsvDeserializerDemo : MonoBehaviour
    {
        // This class serves as a demo for the CsvDeserializer functionality.
        // It includes examples of deserializing simple data types, enums, and custom value converters.
        // Each method demonstrates a specific feature of the CsvDeserializer.
        private void Start()
        {
            RunAllDemos();
        }

        

        // Basic data structure for deserialization
        public class MyData
        {
            public int Id;
            public string Name;
            public float Value;
        }

        // CSV data: Header row followed by data rows.
        // Matches the public fields/properties of MyData.
        private static readonly string simpleCsvData =
@"Id,Name,Value
1,Item1,10.5
2,Item2,20.0
3,Item3,30.75
4,Item4,40.0
";

        /// <summary>
        /// Demonstrates basic deserialization of simple data types.
        /// </summary>
        public static void Simple()
        {
            var myDataList = CsvDeserializer.Deserialize<MyData>(simpleCsvData);

            foreach (var data in myDataList)
            {
                Debug.Log($"Id: {data.Id}, Name: {data.Name}, Value: {data.Value}");
            }
        }

        public enum MyEnum
        {
            Value1,
            Value2,
            Value3
        }

        // CSV data where 'EnumValue' column contains string representations of MyEnum members.
        private static readonly string enumCsvData =
@"Id,EnumValue
1,Value1
2,Value2
3,Value3
";

        public class MyEnumData
        {
            public int Id;
            public MyEnum EnumValue;
        }

        /// <summary>
        /// Demonstrates deserialization of enum types (parsed by name or underlying value).
        /// </summary>
        public static void Enum()
        {
            var enumDataList = CsvDeserializer.Deserialize<MyEnumData>(enumCsvData);

            foreach (var data in enumDataList)
            {
                Debug.Log($"Id: {data.Id}, EnumValue: {data.EnumValue}");
            }
        }

        /// <summary>
        /// Shows how to adjust global deserializer options.
        /// Note: These settings are static and affect all subsequent Deserialize calls.
        /// Consider resetting them if other parts of your application rely on defaults.
        /// </summary>
        public static void AdjustOptions()
        {
            // Example: Bind only to public properties
            CsvDeserializer.MemberBindingScope = CsvDeserializer.BindingScope.Property;
            // CsvDeserializer.Delimiter = ';'; // Example: Change delimiter if needed
            // Preserve C# initialized string values if CSV cell is empty
            CsvDeserializer.PreserveStringMemberOnEmptyCsv = true;

            // To see the effect, you would typically call Deserialize here with appropriate CSV and data class.
            // For this demo, it just shows how to set them.
            Debug.Log("CsvDeserializer options adjusted. (MemberBindingScope=Property, PreserveStringMemberOnEmptyCsv=true)");

            // It's good practice to reset to defaults if these changes are temporary for a specific operation
            // CsvDeserializer.MemberBindingScope = CsvDeserializer.BindingScope.Both; // Default
            // CsvDeserializer.PreserveStringMemberOnEmptyCsv = false; // Default
        }

        public class MyVector3Data
        {
            public int Id;
            public Vector3 Position; // Unity's Vector3 struct
        }

        // CSV data where 'Position' is a string like "x,y,z", quoted because it contains commas.
        private static readonly string vector3CsvData =
@"Id,Position
1,""1.0,2.0,3.0""
2,""4.0,5.0,6.0""
";

        /// <summary>
        /// Demonstrates registering and using a custom value converter for a type like Vector3.
        /// </summary>
        public static void CustomValueConverter()
        {
            // Reset to default binding scope for this specific demo if changed by AdjustOptions()
            CsvDeserializer.MemberBindingScope = CsvDeserializer.BindingScope.Both;
            // Clear any previously registered converters to avoid conflicts if this demo is run multiple times.
            CsvDeserializer.ClearCustomTypeConverters();

            // Register a custom converter for Vector3.
            // The lambda takes a ReadOnlySpan<char> (the CSV field's content) and returns a Vector3.
            CsvDeserializer.RegisterCustomTypeConverter(static value => // C# 9 static anonymous function
            {
                // CSV field for Vector3 is expected to be "x,y,z"
                var parts = value.ToString().Split(','); // In a real high-perf scenario, avoid ToString() here if possible
                if (parts.Length == 3 &&
                    float.TryParse(parts[0], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var x) &&
                    float.TryParse(parts[1], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var y) &&
                    float.TryParse(parts[2], System.Globalization.NumberStyles.Any, System.Globalization.CultureInfo.InvariantCulture, out var z))
                {
                    return new Vector3(x, y, z);
                }
                Debug.LogWarning($"Failed to parse '{value.ToString()}' as Vector3. Returning Vector3.zero.");
                return Vector3.zero; // Return a default value if parsing fails
            });

            var vector3DataList = CsvDeserializer.Deserialize<MyVector3Data>(vector3CsvData);

            foreach (var data in vector3DataList)
            {
                Debug.Log($"Id: {data.Id}, Position: {data.Position}");
            }

            // Important: Clear custom converters if they are not needed globally
            // or if other deserialization tasks expect default behavior for Vector3 (or other custom types).
            CsvDeserializer.ClearCustomTypeConverters();
        }

        /// <summary>
        /// Runs all demonstration methods.
        /// </summary>
        public static void RunAllDemos()
        {
            Debug.Log("--- Running Simple Demo ---");
            Simple();
            Debug.Log("\n--- Running Enum Demo ---");
            Enum();
            Debug.Log("\n--- Running AdjustOptions Demo (settings will be modified) ---");
            AdjustOptions(); // This will change global settings
            Debug.Log("\n--- Running CustomValueConverter Demo (will reset some settings) ---");
            CustomValueConverter(); // This demo internally resets some options and manages its converters

            // Reset global options to sensible defaults after all demos if they were changed
            CsvDeserializer.MemberBindingScope = CsvDeserializer.BindingScope.Both;
            CsvDeserializer.PreserveStringMemberOnEmptyCsv = false;
            CsvDeserializer.Delimiter = ',';
            CsvDeserializer.ClearCustomTypeConverters(); // Ensure all custom converters are cleared
            Debug.Log("\n--- All Demos Finished. CsvDeserializer options reset to defaults. ---");
        }
    }
}
# Native bridge interop checks

Build `CalculatorNative` with graphing enabled, then run this .NET 10 console project against its absolute library path:

```sh
dotnet run --project src/Calculator.Desktop.Tests -- /absolute/path/to/libCalculatorNative.dylib
```

Use the corresponding `.so` or `.dll` on Linux or Windows.
Dependent engine libraries must be beside the native library or discoverable through the platform loader.
Omit the path when `CalculatorNative` is already on the native library search path.

The process returns nonzero for failed assertions or loading errors.
Checks cover real arithmetic, memory, history, locale input, conversion, dates, graph mutations, scene geometry, tracing, and PNG exports.

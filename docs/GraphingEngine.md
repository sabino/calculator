# Build Calculator with the portable graphing engine

The engine lives in the private [sabino/graphing-engine](https://github.com/sabino/graphing-engine) repository.
It implements the common interface with real parsing, graph geometry, and bounded analysis.
Microsoft behavioral parity and retail DLL replacement remain unverified.

## Shared desktop application

Keep the engine checkout beside Calculator, or configure its source path:

```sh
cmake -S . -B out/desktop -G Ninja -DCMAKE_BUILD_TYPE=Release \
  -DCALCULATOR_GRAPHING_ENGINE_ROOT="../graphing-engine"
cmake --build out/desktop --parallel 4
ctest --test-dir out/desktop -LE gui --output-on-failure
cmake --build out/desktop --target calculator_package
```

Launch the platform's Calculator package and choose Graphing from the left navigation menu.
See [desktop builds](NativeDesktop.md) for package paths and GUI checks.
The engine links into `CalculatorNative`; the shared Uno interface draws its geometry with Skia on all three platforms.
The engine checkout supplies the source dependency. Published packages carry their native library and runtime.
An empty `CALCULATOR_GRAPHING_ENGINE_ROOT` disables graphing. An invalid supplied path fails configuration.

Enter equations in separate cards. Try `y=x^2-4`, `y=a*sin(x)`, or `x^2+y^2=25`.
The graphing keypad includes function banks, trigonometry, inequalities, and constants.
Parameters have fields, sliders, and adjustable bounds. Equation controls provide selection, visibility, color, and line style.
Settings control the axes, grid, palette, line width, and supported feature markers.
Hiding curves preserves the viewport and parameter values. Tracing follows visible equations.
Copy and save actions export the shared drawing as PNG.

The integration follows the Windows source's MathML graph requests, normalized viewport operations, and independent equation analysis.
Mathematical coverage is bounded. Two-dimensional mathematical editing and exact Microsoft retail behavior remain unverified.

## Original Windows UWP application

`GraphingImpl.vcxproj` compiles the sibling `graphing-engine` checkout into `GraphingImpl.dll` when its `CMakeLists.txt` exists.
Set `GraphingEngineRoot` to select another checkout. An invalid supplied path fails the build.
Without either source location, the project builds its original mock implementation.

From a Visual Studio developer command prompt, at the Calculator repository root:

```bat
msbuild src\GraphingImpl\GraphingImpl.vcxproj /restore /p:Configuration=Release /p:Platform=x64 /p:GraphingEngineRoot="C:\Source Trees\graphing-engine"
```

Use `x64`, `Win32`, or `ARM64` for the native project. Use `Debug` or `Release` to match the consumer.
For the complete solution, use `src\Calculator.slnx` with `x64` or `ARM64`; its 32-bit solution platform is `x86`.
Keep the same configuration, architecture, toolset, and runtime across the engine and its consumers.
The project uses C++20 and toolset `v145`.

MSBuild compiles six engine sources instead of `Mocks\MathSolver.cpp` and removes mock headers from the project items.
It retains the project's DLL entry point, precompiled header, and version resources.
The output remains `GraphingImpl.dll`, with the matching import library in the project's standard output directory.
The engine exports its interface factories and links Direct2D through `d2d1.lib`.
MSBuild compiles the engine directly, without invoking CMake or copying a prebuilt library.

The engine interfaces originate from Microsoft Calculator revision `d125246`.
This integration preserves the consumer's interface method signatures; C++ DLL consumers require matching MSVC build settings.
The standalone engine's [CI](https://github.com/sabino/graphing-engine/actions/workflows/build.yml) checks Windows DLL consumers, Direct2D drawing, and installed packages.
The complete Calculator UWP solution still requires Windows validation.

For a standalone library, build the engine's CMake project separately.
Its default shared build produces a Windows DLL and import library, a macOS dylib, or a Linux shared object.
CMake installs the public headers and package configuration for external consumers.

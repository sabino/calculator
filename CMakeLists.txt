cmake_minimum_required(VERSION 3.21)
project(Calculator LANGUAGES CXX)

include(CTest)
include(GNUInstallDirs)
option(CALCULATOR_BUILD_UI "Build the shared Uno/Skia desktop application" ON)
option(CALCULATOR_BUILD_BRIDGE "Build the portable CalculatorNative library" ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)
if(MSVC)
    # No C++ allocations cross the C ABI, so the DLL can carry its own CRT.
    set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")
endif()

add_subdirectory(src/CalcManager)
if(CALCULATOR_BUILD_UI OR CALCULATOR_BUILD_BRIDGE OR BUILD_TESTING)
    add_subdirectory(src/CalculatorDesktop)
endif()

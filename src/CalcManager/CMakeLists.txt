add_library(calc_manager STATIC
    CalculatorHistory.cpp
    CalculatorManager.cpp
    ExpressionCommand.cpp
    NumberFormattingUtils.cpp
    UnitConverter.cpp
    CEngine/calc.cpp
    CEngine/CalcInput.cpp
    CEngine/CalcUtils.cpp
    CEngine/History.cpp
    CEngine/Number.cpp
    CEngine/Rational.cpp
    CEngine/RationalMath.cpp
    CEngine/scicomm.cpp
    CEngine/scidisp.cpp
    CEngine/scifunc.cpp
    CEngine/scioper.cpp
    CEngine/sciset.cpp
    Ratpack/basex.cpp
    Ratpack/conv.cpp
    Ratpack/exp.cpp
    Ratpack/fact.cpp
    Ratpack/itrans.cpp
    Ratpack/itransh.cpp
    Ratpack/logic.cpp
    Ratpack/num.cpp
    Ratpack/rat.cpp
    Ratpack/support.cpp
    Ratpack/trans.cpp
    Ratpack/transh.cpp
)
target_compile_features(calc_manager PUBLIC cxx_std_17)
target_include_directories(calc_manager PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}")
find_package(Threads REQUIRED)
target_link_libraries(calc_manager PUBLIC Threads::Threads)
if(MSVC)
    target_compile_options(calc_manager PRIVATE /W4 /utf-8)
else()
    # Ratpack retains its MSVC pragmas when built with another compiler.
    target_compile_options(calc_manager PRIVATE -Wall -Wextra -Wno-unknown-pragmas)
endif()

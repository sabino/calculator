find_package(Python3 3.9 REQUIRED COMPONENTS Interpreter)
set(license_file "${PROJECT_SOURCE_DIR}/LICENSE")
set(package_manifest_file "${PROJECT_SOURCE_DIR}/src/Calculator/Package.appxmanifest")
set_property(DIRECTORY APPEND PROPERTY CMAKE_CONFIGURE_DEPENDS "${license_file}" "${package_manifest_file}")
file(READ "${license_file}" desktop_license)
file(READ "${package_manifest_file}" package_manifest)
string(REGEX MATCH "<Identity[^>]*Version=\"([0-9]+)\\.([0-9]+)\\.([0-9]+)\\.([0-9]+)\"" package_identity "${package_manifest}")
if(NOT package_identity)
    message(FATAL_ERROR "The Windows package identity must contain a four-component version.")
endif()
set(desktop_version "${CMAKE_MATCH_1}.${CMAKE_MATCH_2}.${CMAKE_MATCH_3}.${CMAKE_MATCH_4}")
string(REGEX MATCH "^[^\r\n]+" desktop_copyright "${desktop_license}")
configure_file(BuildInfo.h.in BuildInfo.h @ONLY)
set(resource_dir "${PROJECT_SOURCE_DIR}/src/Calculator/Resources")
set(loader_source "${PROJECT_SOURCE_DIR}/src/Calculator.ViewModels/DataLoaders/UnitConverterDataLoader.cs")
set(nav_source "${PROJECT_SOURCE_DIR}/src/Calculator.ViewModels/Common/NavCategory.cs")
file(GLOB_RECURSE resource_files CONFIGURE_DEPENDS "${resource_dir}/*.resw")
set(resource_header "${CMAKE_CURRENT_BINARY_DIR}/DesktopResources.h")
set(resource_source "${CMAKE_CURRENT_BINARY_DIR}/DesktopResources.cpp")
add_custom_command(
    OUTPUT "${resource_header}" "${resource_source}"
    COMMAND Python3::Interpreter "${CMAKE_CURRENT_SOURCE_DIR}/generate_resources.py"
        "${resource_dir}" "${loader_source}" "${nav_source}" "${resource_header}"
    DEPENDS generate_resources.py "${loader_source}" "${nav_source}" ${resource_files}
    VERBATIM
)
add_library(calculator_desktop STATIC
    AboutInfo.cpp
    CurrencyDataLoader.cpp
    DateCalculator.cpp
    DesktopModel.cpp
    ResourceProvider.cpp
    UnitDataLoader.cpp
    "${resource_header}"
    "${resource_source}"
)
target_include_directories(calculator_desktop PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}" PRIVATE "${CMAKE_CURRENT_BINARY_DIR}")
target_link_libraries(calculator_desktop PUBLIC calc_manager)
if(MSVC)
    target_compile_options(calculator_desktop PRIVATE /utf-8)
endif()

set(graphing_engine_default_root "")
if(EXISTS "${PROJECT_SOURCE_DIR}/../graphing-engine/CMakeLists.txt")
    get_filename_component(graphing_engine_default_root "${PROJECT_SOURCE_DIR}/../graphing-engine" ABSOLUTE)
endif()
set(CALCULATOR_GRAPHING_ENGINE_ROOT "${graphing_engine_default_root}" CACHE PATH "Portable graphing engine checkout; empty disables graphing")
if(CALCULATOR_GRAPHING_ENGINE_ROOT AND NOT EXISTS "${CALCULATOR_GRAPHING_ENGINE_ROOT}/CMakeLists.txt")
    message(FATAL_ERROR "CALCULATOR_GRAPHING_ENGINE_ROOT must contain the graphing-engine CMakeLists.txt.")
endif()

if(CALCULATOR_BUILD_BRIDGE OR CALCULATOR_BUILD_UI)
    add_library(calculator_native SHARED Bridge.cpp)
    target_link_libraries(calculator_native PRIVATE calculator_desktop)
    target_include_directories(calculator_native PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}")
    target_compile_definitions(calculator_native PRIVATE CALCULATOR_NATIVE_BUILD=1)
    set_target_properties(calculator_native PROPERTIES OUTPUT_NAME CalculatorNative)
    if(MSVC)
        target_compile_options(calculator_native PRIVATE /utf-8)
    endif()
    if(CALCULATOR_GRAPHING_ENGINE_ROOT)
        # The desktop bridge is one portable library; drawing belongs to the shared Skia frontend.
        set(BUILD_SHARED_LIBS OFF)
        set(GRAPHING_BUILD_NATIVE_DRAWING OFF)
        add_subdirectory("${CALCULATOR_GRAPHING_ENGINE_ROOT}" "${CMAKE_CURRENT_BINARY_DIR}/graphing-engine")
        target_sources(calculator_native PRIVATE GraphBridge.cpp)
        target_link_libraries(calculator_native PRIVATE Graphing::GraphingImpl)
        get_target_property(graphing_engine_version GraphingImpl VERSION)
        target_compile_definitions(calculator_native PRIVATE CALCULATOR_HAS_GRAPHING=1 CALCULATOR_GRAPHING_VERSION="${graphing_engine_version}")
    endif()
    install(TARGETS calculator_native RUNTIME DESTINATION bin LIBRARY DESTINATION lib ARCHIVE DESTINATION lib)
    install(FILES Bridge.h GraphBridge.h DESTINATION include/CalculatorDesktop)
endif()

if(CALCULATOR_BUILD_UI)
    find_program(DOTNET_EXECUTABLE dotnet REQUIRED)
    set(desktop_project "${PROJECT_SOURCE_DIR}/src/Calculator.Desktop/Calculator.Desktop.csproj")
    set(native_runtime_dir "${PROJECT_BINARY_DIR}/native-runtime")
    set(managed_output_dir "${PROJECT_BINARY_DIR}/desktop")
    set(managed_intermediate_dir "${PROJECT_BINARY_DIR}/managed-obj/")
    set(managed_configuration "$<IF:$<CONFIG:Debug>,Debug,Release>")
    add_custom_target(calculator_native_runtime
        COMMAND "${CMAKE_COMMAND}" -E make_directory "${native_runtime_dir}"
        COMMAND "${CMAKE_COMMAND}" -E copy_if_different "$<TARGET_FILE:calculator_native>" "${native_runtime_dir}/$<TARGET_FILE_NAME:calculator_native>"
        DEPENDS calculator_native
        VERBATIM
    )
    if(CALCULATOR_GRAPHING_ENGINE_ROOT)
        add_custom_command(TARGET calculator_native_runtime POST_BUILD
            COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${CALCULATOR_GRAPHING_ENGINE_ROOT}/LICENSE" "${native_runtime_dir}/GraphingEngine-LICENSE.txt"
            COMMAND "${CMAKE_COMMAND}" -E copy_if_different "${CALCULATOR_GRAPHING_ENGINE_ROOT}/docs/Microsoft-LICENSE" "${native_runtime_dir}/GraphingInterfaces-LICENSE.txt"
            VERBATIM
        )
    endif()
    add_custom_target(calculator ALL
        COMMAND "${CMAKE_COMMAND}" -E env DOTNET_CLI_TELEMETRY_OPTOUT=1
            "${DOTNET_EXECUTABLE}" restore "${desktop_project}" --locked-mode "-p:Configuration=${managed_configuration}"
            "-p:BaseIntermediateOutputPath=${managed_intermediate_dir}"
        COMMAND "${CMAKE_COMMAND}" -E env DOTNET_CLI_TELEMETRY_OPTOUT=1
            "${DOTNET_EXECUTABLE}" build "${desktop_project}" --no-restore -f net10.0-desktop
            -c "${managed_configuration}" -o "${managed_output_dir}" "-p:CalculatorNativeDir=${native_runtime_dir}"
            "-p:BaseIntermediateOutputPath=${managed_intermediate_dir}"
        DEPENDS calculator_native_runtime
        WORKING_DIRECTORY "${PROJECT_SOURCE_DIR}/src/Calculator.Desktop"
        VERBATIM
    )
    if(CMAKE_SYSTEM_PROCESSOR MATCHES "^(arm64|aarch64|ARM64)$" OR CMAKE_GENERATOR_PLATFORM STREQUAL "ARM64")
        set(desktop_arch "arm64")
    else()
        set(desktop_arch "x64")
    endif()
    if(APPLE)
        set(desktop_os "osx")
        set(desktop_package_options "-p:PackageFormat=app" "-p:CodesignKey=-")
    elseif(WIN32)
        set(desktop_os "win")
    else()
        set(desktop_os "linux")
    endif()
    set(CALCULATOR_RUNTIME_ID "${desktop_os}-${desktop_arch}" CACHE STRING "Desktop .NET runtime matching the native library")
    add_custom_target(calculator_package
        COMMAND "${CMAKE_COMMAND}" -E env DOTNET_CLI_TELEMETRY_OPTOUT=1
            "${DOTNET_EXECUTABLE}" publish "${desktop_project}" -f net10.0-desktop -c "${managed_configuration}"
            -r "${CALCULATOR_RUNTIME_ID}" --self-contained true "-p:RestoreLockedMode=true" ${desktop_package_options}
            "-p:BaseIntermediateOutputPath=${managed_intermediate_dir}" "-p:CalculatorNativeDir=${native_runtime_dir}"
            -o "${PROJECT_BINARY_DIR}/package"
        DEPENDS calculator_native_runtime
        WORKING_DIRECTORY "${PROJECT_SOURCE_DIR}/src/Calculator.Desktop"
        VERBATIM
    )
    if(APPLE)
        add_custom_command(TARGET calculator_package POST_BUILD
            COMMAND Python3::Interpreter "${CMAKE_CURRENT_SOURCE_DIR}/finalize_macos_bundle.py"
                --bundle "${PROJECT_BINARY_DIR}/package/Calculator.app"
                --entitlements "${PROJECT_SOURCE_DIR}/src/Calculator.Desktop/Assets/Calculator.entitlements"
            VERBATIM
        )
        install(DIRECTORY "${PROJECT_BINARY_DIR}/package/Calculator.app" DESTINATION . USE_SOURCE_PERMISSIONS)
    elseif(UNIX)
        configure_file("${PROJECT_SOURCE_DIR}/src/Calculator.Desktop/Assets/Calculator.sh.in"
            "${CMAKE_CURRENT_BINARY_DIR}/Calculator" @ONLY)
        install(DIRECTORY "${PROJECT_BINARY_DIR}/package/" DESTINATION "${CMAKE_INSTALL_LIBEXECDIR}/Calculator" USE_SOURCE_PERMISSIONS)
        install(PROGRAMS "${CMAKE_CURRENT_BINARY_DIR}/Calculator" DESTINATION "${CMAKE_INSTALL_BINDIR}")
        install(FILES "${PROJECT_SOURCE_DIR}/src/Calculator.Desktop/Assets/io.github.sabino.calculator.desktop"
            DESTINATION "${CMAKE_INSTALL_DATADIR}/applications")
        foreach(icon_size IN ITEMS 16 24 32 48 64 128 256 512 1024)
            install(FILES "${CMAKE_CURRENT_SOURCE_DIR}/assets/calculator-${icon_size}.png"
                DESTINATION "${CMAKE_INSTALL_DATADIR}/icons/hicolor/${icon_size}x${icon_size}/apps"
                RENAME io.github.sabino.calculator.png)
        endforeach()
        install(FILES "${CMAKE_CURRENT_SOURCE_DIR}/assets/icon.svg"
            DESTINATION "${CMAKE_INSTALL_DATADIR}/icons/hicolor/scalable/apps" RENAME io.github.sabino.calculator.svg)
    else()
        install(DIRECTORY "${PROJECT_BINARY_DIR}/package/" DESTINATION "${CMAKE_INSTALL_BINDIR}" USE_SOURCE_PERMISSIONS)
    endif()
endif()

if(BUILD_TESTING)
    add_executable(desktop_tests tests/DesktopTests.cpp)
    target_link_libraries(desktop_tests PRIVATE calculator_desktop)
    add_test(NAME desktop_engine COMMAND desktop_tests)
    add_executable(date_tests tests/DateTests.cpp)
    target_link_libraries(date_tests PRIVATE calculator_desktop)
    add_test(NAME date_engine COMMAND date_tests)
    if(MSVC)
        target_compile_options(desktop_tests PRIVATE /utf-8)
        target_compile_options(date_tests PRIVATE /utf-8)
    endif()
    if(TARGET calculator_native)
        add_executable(bridge_tests tests/BridgeTests.cpp)
        target_link_libraries(bridge_tests PRIVATE calculator_native calculator_desktop)
        add_test(NAME calculator_bridge COMMAND bridge_tests)
        if(MSVC)
            target_compile_options(bridge_tests PRIVATE /utf-8)
        endif()
        if(CALCULATOR_GRAPHING_ENGINE_ROOT)
            add_executable(graph_bridge_tests tests/GraphBridgeTests.cpp)
            target_link_libraries(graph_bridge_tests PRIVATE calculator_native)
            add_test(NAME graph_bridge COMMAND graph_bridge_tests)
            if(MSVC)
                target_compile_options(graph_bridge_tests PRIVATE /utf-8)
            endif()
        endif()
    endif()
    if(CALCULATOR_BUILD_UI)
        set(interop_project "${PROJECT_SOURCE_DIR}/src/Calculator.Desktop.Tests/Calculator.Desktop.Tests.csproj")
        set(interop_output_dir "${PROJECT_BINARY_DIR}/interop-tests")
        add_custom_target(calculator_interop_tests ALL
            COMMAND "${CMAKE_COMMAND}" -E env DOTNET_CLI_TELEMETRY_OPTOUT=1
                "${DOTNET_EXECUTABLE}" build "${interop_project}" -c "${managed_configuration}" -o "${interop_output_dir}"
                "-p:BaseIntermediateOutputPath=${PROJECT_BINARY_DIR}/interop-obj/"
            DEPENDS calculator_native
            WORKING_DIRECTORY "${PROJECT_SOURCE_DIR}/src/Calculator.Desktop"
            VERBATIM
        )
        add_test(NAME desktop_interop COMMAND "${DOTNET_EXECUTABLE}" "${interop_output_dir}/Calculator.Desktop.Tests.dll" "$<TARGET_FILE:calculator_native>")
        add_test(NAME desktop_ui COMMAND "${DOTNET_EXECUTABLE}" "${managed_output_dir}/Calculator.dll" --smoke-test)
        set_tests_properties(desktop_ui PROPERTIES LABELS gui TIMEOUT 60)
    endif()
endif()

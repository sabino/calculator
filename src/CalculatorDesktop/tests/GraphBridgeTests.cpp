// Licensed under the MIT License.
#include "GraphBridge.h"
#include <cmath>
#include <cstring>
#include <iostream>
#include <limits>
#include <memory>
#include <stdexcept>
#include <string>
#include <vector>

namespace
{
void Require(bool condition, const char* message)
{
    if (!condition) throw std::runtime_error(message);
}
using Session = std::unique_ptr<CalcGraphSession, decltype(&calc_graph_destroy)>;
Session Create()
{
    Session session(calc_graph_create(), &calc_graph_destroy);
    Require(static_cast<bool>(session), "Graph session creation failed");
    return session;
}
std::string State(CalcGraphSession* session)
{
    const auto* state = calc_graph_get_state(session);
    Require(state != nullptr, "Graph state must be available");
    return state;
}
std::vector<unsigned char> Render(CalcGraphSession* session, int width = 320, int height = 240)
{
    const unsigned char* bytes = nullptr; size_t length = 0;
    Require(calc_graph_render_png(session, width, height, &bytes, &length) == 1, "Portable PNG rendering failed");
    Require(bytes && length > 32, "PNG buffer must contain encoded image bytes");
    const unsigned char expected[] = {137, 80, 78, 71, 13, 10, 26, 10};
    Require(std::memcmp(bytes, expected, sizeof(expected)) == 0, "PNG signature is invalid");
    return {bytes, bytes + length};
}
std::string EquationState(const std::string& state, const std::string& input)
{
    const auto start = state.find("{\"input\":\"" + input + "\"");
    Require(start != std::string::npos, "Expected equation is absent from the snapshot");
    const auto next = state.find(",{\"input\":", start);
    const auto end = next == std::string::npos ? state.find("],\"selectedIndex\":", start) : next;
    Require(end != std::string::npos, "Equation snapshot is incomplete");
    return state.substr(start, end - start);
}
void TestRowEditsAndRemoval()
{
    auto session = Create();
    Require(calc_graph_update(session.get(), "y=a*x\ny=b*x+1\ny=a*x-2", 0) == 1, "Row identity setup failed");
    Require(calc_graph_set_selected(session.get(), 2) == 1 && calc_graph_set_visible(session.get(), 1, 0) == 1,
            "Row identity selection and visibility failed");
    Require(calc_graph_set_color(session.get(), 0, 0xFA8010FFu) == 1 && calc_graph_set_color(session.get(), 1, 0x108090FFu) == 1
            && calc_graph_set_color(session.get(), 2, 0x8040C0FFu) == 1, "Distinct row colors failed");
    Require(calc_graph_set_line_style(session.get(), 0, 1) == 1 && calc_graph_set_line_style(session.get(), 1, 2) == 1
            && calc_graph_set_line_style(session.get(), 2, 3) == 1, "Distinct row styles failed");
    Require(calc_graph_set_parameter(session.get(), 0, "a", 7) == 1 && calc_graph_set_parameter(session.get(), 1, "b", 3) == 1,
            "Shared row parameters failed");
    Require(calc_graph_set_parameter_bounds(session.get(), 0, "a", -2, 2, 0.25) == 1, "Shared parameter bounds failed");
    Require(calc_graph_set_range(session.get(), -3, 5, -7, 9) == 1, "Row identity manual viewport failed");
    auto accepted = State(session.get());
    const auto acceptedPng = Render(session.get());
    Require(calc_graph_set_equation(session.get(), 1, "y=sin(") == 0 && State(session.get()) == accepted
            && Render(session.get()) == acceptedPng, "Invalid row edit must preserve the complete graph and image");
    Require(calc_graph_set_equation(session.get(), 1, "y=x\ny=x+1") == 0 && State(session.get()) == accepted,
            "A row edit must reject multiple equations without mutation");
    Require(calc_graph_set_equation(session.get(), 1, "y=b*x+9") == 1, "Hidden row edit failed");
    auto state = State(session.get());
    Require(EquationState(state, "y=a*x") == EquationState(accepted, "y=a*x")
            && EquationState(state, "y=a*x-2") == EquationState(accepted, "y=a*x-2"),
            "Editing a row must preserve both neighboring rows' complete appearance and parameters");
    const auto edited = EquationState(state, "y=b*x+9");
    Require(edited.find("\"color\":\"#108090\",\"visible\":false") != std::string::npos
            && edited.find("\"value\":3") != std::string::npos && edited.find("\"lineStyle\":2") != std::string::npos,
            "Editing a hidden row must preserve its appearance, visibility, and named value");
    accepted = state;
    Require(calc_graph_remove_equation(session.get(), 0) == 1, "First row removal failed");
    state = State(session.get());
    Require(EquationState(state, "y=b*x+9") == EquationState(accepted, "y=b*x+9")
            && EquationState(state, "y=a*x-2") == EquationState(accepted, "y=a*x-2")
            && state.find("\"selectedIndex\":1") != std::string::npos,
            "Removing a preceding row must retain surviving rows and adjust selected index");
    Require(state.find("\"value\":7,\"min\":-2,\"max\":2,\"step\":0.25") != std::string::npos,
            "Shared parameter values and metadata must survive while another row uses them");
    Require(calc_graph_remove_equation(session.get(), 1) == 1, "Selected last row removal failed");
    state = State(session.get());
    Require(state.find("\"selectedIndex\":0") != std::string::npos && state.find("\"visible\":false,\"selected\":true") != std::string::npos
            && state.find("\"name\":\"a\"") == std::string::npos, "Removal must select the neighbor and prune only unused parameters");
    Require(state.find("\"range\":{\"left\":-3,\"right\":5,\"bottom\":-7,\"top\":9}") != std::string::npos,
            "Row editing and removal must preserve manual ranges");
    Require(calc_graph_remove_equation(session.get(), 0) == 1, "Last row removal failed");
    state = State(session.get());
    Require(state.find("{\"equations\":[],\"selectedIndex\":-1") == 0,
            "Removing the last equation must expose an empty unselected graph");
    Require(calc_graph_trace(session.get(), 0, 0, 0.001) == nullptr, "An empty graph must not trace stale equations");
    Require(calc_graph_update(session.get(), "y=a*x", 0) == 1
            && State(session.get()).find("\"value\":1,\"min\":-5,\"max\":5,\"step\":0.1") != std::string::npos,
            "Reintroduced pruned parameters must use default values and metadata");
}
void TestParameterMetadataAndGraphSettings()
{
    auto session = Create();
    Require(calc_graph_update(session.get(), "y=a*x\ny=a*x+1", 0) == 1 && calc_graph_set_parameter(session.get(), 0, "a", 7) == 1,
            "Metadata setup failed");
    const std::string analysis = calc_graph_analyze(session.get(), 0);
    Require(calc_graph_set_parameter_bounds(session.get(), 1, "a", -2, 2, 0.25) == 1, "Parameter metadata update failed");
    auto state = State(session.get());
    const std::string metadata = "\"value\":7,\"min\":-2,\"max\":2,\"step\":0.25";
    Require(EquationState(state, "y=a*x").find(metadata) != std::string::npos
            && EquationState(state, "y=a*x+1").find(metadata) != std::string::npos,
            "Metadata must be shared by name without clamping the mathematical value");
    Require(std::string(calc_graph_analyze(session.get(), 0)) == analysis, "Metadata must leave equation analysis unchanged");
    Require(calc_graph_set_line_style(session.get(), 0, 1) == 1 && calc_graph_set_line_style(session.get(), 1, 2) == 1,
            "Settings row styles failed");
    Require(calc_graph_set_settings(session.get(), 3, 0x102030FFu, 0xAABBCCFFu, 0x405060FFu, 0x708090FFu) == 1,
            "Graph settings update failed");
    const std::string settings = "\"settings\":{\"lineWidth\":3,\"background\":\"#FF102030\",\"text\":\"#FFAABBCC\",\"axis\":\"#FF405060\",\"grid\":\"#FF708090\",\"featuresMask\":0,\"showAxes\":true,\"showGrid\":true}";
    Require(State(session.get()).find(settings) != std::string::npos, "Graph settings must expose alpha-first colors");
    const auto* sceneText = calc_graph_get_scene(session.get(), 320, 240);
    Require(sceneText != nullptr, "Configured graph scene must be available");
    const std::string scene = sceneText;
    Require(scene.find("\"background\":\"#FF102030\"") != std::string::npos
            && scene.find("\"color\":\"#FFAABBCC\"") != std::string::npos
            && scene.find("\"color\":\"#FF405060\"") != std::string::npos
            && scene.find("\"color\":\"#FF708090\"") != std::string::npos,
            "Scene background, labels, axes, and grid must use configured colors");
    Require(scene.find("\"width\":5,\"style\":1,\"equationId\":0") != std::string::npos
            && scene.find("\"width\":3,\"style\":2,\"equationId\":1") != std::string::npos,
            "Scene curves must use persistent styles, ordinary width, and selected width");
    Require(calc_graph_set_equation(session.get(), 0, "y=a*x-1") == 1 && calc_graph_set_visible(session.get(), 1, 0) == 1
            && calc_graph_set_parameter(session.get(), 0, "a", 8) == 1 && State(session.get()).find(settings) != std::string::npos,
            "Graph rebuilds must preserve settings and metadata");
    const auto accepted = State(session.get());
    Require(calc_graph_set_parameter_bounds(session.get(), 0, "a", 2, -2, 0.25) == 0
            && calc_graph_set_parameter_bounds(session.get(), 0, "a", -2, 2, 0) == 0
            && calc_graph_set_parameter_bounds(session.get(), 0, "a", -2, 2, 5) == 0
            && calc_graph_set_parameter_bounds(session.get(), 0, "a", -2, 2, std::numeric_limits<double>::quiet_NaN()) == 0
            && calc_graph_set_parameter_bounds(session.get(), 0, "missing", -2, 2, 0.25) == 0,
            "Invalid parameter metadata must be rejected");
    Require(calc_graph_set_settings(session.get(), std::numeric_limits<double>::infinity(), 0, 0, 0, 0) == 0
            && calc_graph_set_settings(session.get(), 0, 0, 0, 0, 0) == 0 && calc_graph_set_line_style(session.get(), 0, 5) == 0
            && calc_graph_remove_equation(session.get(), 99) == 0 && State(session.get()) == accepted,
            "Invalid metadata, settings, styles, and row indices must preserve the accepted state");
    Require(calc_graph_remove_equation(session.get(), 1) == 1 && calc_graph_remove_equation(session.get(), 0) == 1,
            "Settings empty graph setup failed");
    const auto* emptySceneText = calc_graph_get_scene(session.get(), 320, 240);
    Require(emptySceneText != nullptr, "Configured empty scene must be available");
    const std::string emptyScene = emptySceneText;
    Require(emptyScene.find("\"background\":\"#FF102030\"") != std::string::npos, "Empty graphs must retain their configured background");
    Require(calc_graph_update(session.get(), "y=x", 0) == 1 && State(session.get()).find(settings) != std::string::npos,
            "Bulk updates after removal must preserve graph settings");
}
void TestInvalidPreservationAndIndependentSessions()
{
    auto session = Create(); auto other = Create();
    Require(calc_graph_update(session.get(), "y=x^2-4\ny=x", 0) == 1, "Initial equations failed");
    Require(calc_graph_set_range(session.get(), -3, 5, -7, 9) == 1, "Manual viewport failed");
    const auto accepted = State(session.get());
    const auto acceptedPng = Render(session.get());
    const auto* scene = calc_graph_get_scene(session.get(), 320, 240);
    Require(scene && std::string(scene).find("\"background\":\"#FFFFFFFF\"") != std::string::npos
                && std::string(scene).find("\"strokes\":[{") != std::string::npos
                && std::string(scene).find("\"labels\":[{") != std::string::npos,
            "Portable scene must expose geometry, text labels, and alpha-first colors");
    Require(calc_graph_update(session.get(), "y=sin(", 1) == 0, "Invalid equation must be rejected");
    Require(std::strlen(calc_graph_last_error(session.get())) > 0, "Rejected update must explain the error");
    Require(State(session.get()) == accepted, "Invalid update must preserve equations, angle, viewport, and analysis");
    Require(Render(session.get()) == acceptedPng, "Invalid update must preserve its rendered graph");
    Require(calc_graph_update(session.get(), "x != y", 0) == 0, "Unsupported graph relation must be rejected");
    Require(State(session.get()) == accepted, "Unsupported initialization must preserve accepted graph state");
    Require(calc_graph_update(session.get(), "y=x", 7) == 0 && State(session.get()) == accepted, "Invalid angle must preserve the graph");
    const char invalidUtf8[] = {'y', '=', char(0xc0), char(0xaf), 0};
    Require(calc_graph_update(session.get(), invalidUtf8, 0) == 0, "Invalid UTF-8 must be rejected at the bridge edge");
    Require(calc_graph_update(other.get(), "y=sin(x)", 1) == 1, "Independent session failed");
    Require(State(session.get()) == accepted && State(other.get()).find("\"angleUnit\":1") != std::string::npos,
            "Graph sessions must keep their angle and equation state independent");
}
void TestParametersVisibilityAndRanges()
{
    auto session = Create();
    Require(calc_graph_update(session.get(), "y=a*x^2-4\ny=a*x", 0) == 1, "Parameterized equations failed");
    auto state = State(session.get());
    Require(state.find("\"name\":\"a\",\"value\":1,\"min\":-5,\"max\":5,\"step\":0.1") != std::string::npos,
            "Parameter controls must expose their defaults and slider bounds");
    const std::string originalAnalysis = calc_graph_analyze(session.get(), 0);
    Require(calc_graph_set_parameter(session.get(), 0, "a", 2) == 1, "Parameter value failed");
    const std::string changedAnalysis = calc_graph_analyze(session.get(), 0);
    Require(originalAnalysis != changedAnalysis && changedAnalysis.find("Domain\n") == 0, "Parameter mutation must refresh mathematical analysis");
    Require(calc_graph_set_selected(session.get(), 1) == 1, "Row selection failed");
    Require(calc_graph_set_range(session.get(), -3, 5, -7, 9) == 1, "Manual range failed");
    Require(calc_graph_set_color(session.get(), 1, 0xDC2828FFu) == 1, "Row color failed");
    const std::string selectedAnalysis = calc_graph_analyze(session.get(), 1);
    Require(calc_graph_set_visible(session.get(), 1, 0) == 1, "Selected row visibility failed");
    state = State(session.get());
    Require(state.find("\"visible\":false,\"selected\":true") != std::string::npos && state.find("\"selectedIndex\":1") != std::string::npos,
            "Hiding a row must retain selection independently");
    Require(state.find("\"color\":\"#DC2828\"") != std::string::npos && state.find("\"name\":\"a\",\"value\":2") != std::string::npos,
            "Hidden accepted rows must retain colors and parameter values");
    Require(std::string(calc_graph_analyze(session.get(), 1)) == selectedAnalysis, "Hidden rows must retain independent analysis");
    Require(calc_graph_set_visible(session.get(), 0, 0) == 1, "All-hidden graph failed");
    const auto blank = Render(session.get());
    const auto* hiddenScene = calc_graph_get_scene(session.get(), 320, 240);
    Require(hiddenScene && std::string(hiddenScene).find("\"strokes\":[],\"fills\":[],\"labels\":[]") != std::string::npos,
            "All-hidden scenes must not expose retained curve geometry");
    Require(calc_graph_trace(session.get(), 0, 0, 0.001) == nullptr, "All-hidden graphs must not trace old curves");
    Require(calc_graph_set_parameter(session.get(), 1, "a", 3) == 1, "Hidden parameter mutation failed");
    Require(calc_graph_set_visible(session.get(), 0, 1) == 1, "Visibility restoration failed");
    Require(Render(session.get()) != blank, "Restoring visibility must render the accepted curve");
    state = State(session.get());
    Require(state.find("\"range\":{\"left\":-3,\"right\":5,\"bottom\":-7,\"top\":9}") != std::string::npos,
            "Visibility and parameters must preserve manual viewport ranges");
    Require(calc_graph_update(session.get(), "y=a*x^2-3\ny=a*x", 0) == 1, "Equation edit failed");
    state = State(session.get());
    Require(state.find("\"value\":3") != std::string::npos && state.find("\"left\":-3") != std::string::npos,
            "Equation edits must preserve named parameter values and manual ranges");
    Require(state.find("\"visible\":false,\"selected\":true") != std::string::npos, "An unchanged hidden row must stay hidden through edits");
    const auto accepted = state;
    Require(calc_graph_set_parameter(session.get(), 0, "missing", 2) == 0 && State(session.get()) == accepted,
            "Unknown row parameter must be rejected without mutation");
    Require(calc_graph_set_parameter(session.get(), 0, "a", std::numeric_limits<double>::infinity()) == 0,
            "Nonfinite parameter values must be rejected");
    Require(calc_graph_set_visible(session.get(), 0, 2) == 0 && calc_graph_set_selected(session.get(), 99) == 0,
            "Visibility and selection inputs require valid values");
}
void TestPngTransformsAndTracing()
{
    auto session = Create();
    Require(calc_graph_update(session.get(), "y=x", 0) == 1, "Tracing equation failed");
    Require(calc_graph_set_range(session.get(), -2, 2, -2, 2) == 1, "Tracing viewport failed");
    Render(session.get(), 401, 301);
    const auto* trace = calc_graph_trace(session.get(), 0, 0, 0.0001);
    Require(trace != nullptr && std::string(trace).find("\"row\":0") != std::string::npos && std::string(trace).find("\"t\":null") != std::string::npos,
            "Tracing must return row identity, coordinates, and explicit absent parameter values");
    Require(calc_graph_set_range(session.get(), 2, 10, -8, 4) == 1, "Asymmetric viewport failed");
    Require(calc_graph_zoom(session.get(), 0.5, 0.5, -0.5) == 1, "Normalized zoom failed");
    Require(State(session.get()).find("\"range\":{\"left\":5,\"right\":9,\"bottom\":-6.5,\"top\":-0.5}") != std::string::npos,
            "Normalized zoom must keep the pointer's mathematical location fixed");
    Require(calc_graph_set_range(session.get(), 2, 10, -8, 4) == 1 && calc_graph_pan(session.get(), 1, -1) == 1,
            "Half-screen pan failed");
    Require(State(session.get()).find("\"range\":{\"left\":6,\"right\":14,\"bottom\":-14,\"top\":-2}") != std::string::npos,
            "Normalized pan must translate half the viewport span");
    const auto accepted = State(session.get());
    Require(calc_graph_set_range(session.get(), 10, 2, -8, 4) == 0 && State(session.get()) == accepted, "Invalid ranges must preserve accepted bounds");
    Require(calc_graph_zoom(session.get(), 0, 0, 0) == 0 && calc_graph_pan(session.get(), 2, 0) == 0, "Transform bounds must be validated");
    const unsigned char placeholder = 0;
    const unsigned char* bytes = &placeholder; size_t length = 1;
    Require(calc_graph_render_png(session.get(), 100000, 100000, &bytes, &length) == 0 && bytes == nullptr && length == 0,
            "Oversized PNG requests must be bounded and initialize their outputs");
    Require(calc_graph_render_png(session.get(), 320, 240, nullptr, &length) == 0, "PNG output pointers are required");
    Require(calc_graph_get_scene(session.get(), 100000, 100000) == nullptr, "Scene dimensions must obey the same memory bounds");
    Require(calc_graph_trace(session.get(), 0, 0, -1) == nullptr, "Invalid tracing precision must be rejected");
    Require(calc_graph_update(nullptr, "y=x", 0) == 0 && calc_graph_get_state(nullptr) == nullptr, "Null handles must be rejected without exceptions");
    calc_graph_destroy(nullptr);
}
void TestFeatureMarkersAndGuides()
{
    auto session = Create();
    Require(calc_graph_update(session.get(), "y=x^2-4", 0) == 1
            && calc_graph_set_range(session.get(), -3, 3, -5, 5) == 1, "Marker graph setup failed");
    const auto* originalText = calc_graph_get_scene(session.get(), 320, 240);
    Require(originalText != nullptr, "Original marker scene is unavailable");
    const std::string original = originalText;
    Require(State(session.get()).find("\"featuresMask\":0,\"showAxes\":true,\"showGrid\":true") != std::string::npos,
            "Marker and guide defaults must be explicit");
    Require(calc_graph_set_features(session.get(), 7) == 1, "Zeros, y-intercept, and minimum markers failed");
    const auto* markedText = calc_graph_get_scene(session.get(), 320, 240);
    Require(markedText != nullptr && std::string(markedText) != original, "Supported polynomial features must add scene geometry");
    Require(calc_graph_set_settings(session.get(), 3, 0xFFFFFFFFu, 0x2D2D2DFFu, 0x646464FFu, 0xDCDCDCFFu) == 1
            && calc_graph_set_equation(session.get(), 0, "y=x^2-3") == 1
            && State(session.get()).find("\"featuresMask\":7") != std::string::npos,
            "Color, width, and equation rebuilds must preserve selected marker flags");
    Require(calc_graph_set_features(session.get(), 0) == 1 && calc_graph_set_grid(session.get(), 0, 0) == 1,
            "Disabling markers and guides failed");
    const auto* bareText = calc_graph_get_scene(session.get(), 320, 240);
    Require(bareText != nullptr && std::string(bareText).find("\"labels\":[]") != std::string::npos
            && std::string(bareText).find("\"equationId\":0") != std::string::npos,
            "Hiding axes and grid must leave the equation curve without guide labels");
    const auto accepted = State(session.get());
    Require(calc_graph_set_features(session.get(), 256) == 0 && calc_graph_set_grid(session.get(), 2, 1) == 0
            && State(session.get()) == accepted, "Invalid marker bits or guide flags must preserve accepted settings");
    Require(calc_graph_set_features(session.get(), 255) == 1 && calc_graph_set_grid(session.get(), 1, 0) == 1,
            "Every supported marker flag and independent guide setting must be accepted");
    Require(State(session.get()).find("\"featuresMask\":255,\"showAxes\":true,\"showGrid\":false") != std::string::npos,
            "Marker flags and independent guides must reach the settings snapshot");
}
void TestSelectedCurveTracing()
{
    auto session = Create();
    Require(calc_graph_update(session.get(), "y=0\ny=5", 0) == 1
            && calc_graph_set_range(session.get(), -10, 10, -10, 10) == 1, "Selected trace setup failed");
    Render(session.get(), 401, 301);
    const auto accepted = State(session.get());
    const auto* any = calc_graph_trace(session.get(), 0.2, 0.5, 0.001);
    Require(any && std::string(any).find("\"row\":1") != std::string::npos, "Ordinary tracing must choose the nearest visible curve");
    const auto* selected = calc_graph_trace_selected(session.get(), 0.2, 0.5, 0.001);
    Require(selected && std::string(selected).find("\"row\":0") != std::string::npos
            && std::string(selected).find("\"y\":0,") != std::string::npos,
            "Active tracing must stay on the selected curve when another curve is nearer");
    Require(State(session.get()) == accepted, "Selected tracing must preserve accepted state, selection, and range");
    Require(calc_graph_set_selected(session.get(), 1) == 1, "Changing active trace selection failed");
    selected = calc_graph_trace_selected(session.get(), -0.2, 0, 0.001);
    Require(selected && std::string(selected).find("\"row\":1") != std::string::npos, "Active tracing must use the changed selected row");
    Require(calc_graph_set_visible(session.get(), 1, 0) == 1 && calc_graph_trace_selected(session.get(), 0, 0, 0.001) == nullptr,
            "A hidden selected curve must clear active tracing instead of choosing another row");
    const auto hidden = State(session.get());
    Require(calc_graph_trace_selected(session.get(), 2, 0, 0.001) == nullptr
            && calc_graph_trace_selected(session.get(), 0, 0, 0) == nullptr && State(session.get()) == hidden,
            "Selected trace inputs require the same bounded validation and preservation");
}
}
int main()
{
    try
    {
        TestInvalidPreservationAndIndependentSessions(); TestParametersVisibilityAndRanges(); TestPngTransformsAndTracing();
        TestRowEditsAndRemoval(); TestParameterMetadataAndGraphSettings();
        TestFeatureMarkersAndGuides(); TestSelectedCurveTracing();
        for (const auto* input : {"y=x^2-4\ny=x", "y=x^2-4\ry=x", "y=x^2-4\r\ny=x"})
        {
            auto session = Create();
            Require(calc_graph_update(session.get(), input, 0) == 1, "Portable line endings must preserve separate equations");
            Require(State(session.get()).find("\"input\":\"y=x\"") != std::string::npos,
                "The second equation was lost while reading line endings");
        }
        std::cout << "Graph C ABI state, preservation, parameter, visibility, PNG, and tracing checks passed\n";
        return 0;
    }
    catch (const std::exception& error) { std::cerr << error.what() << '\n'; return 1; }
}

// Licensed under the MIT License.
#include "GraphBridge.h"
#include "BridgeJson.h"
#include "GraphingInterfaces/IMathSolver.h"
#include "graphing/Scene.h"
#include <algorithm>
#include <array>
#include <cmath>
#include <cstddef>
#include <cstdint>
#include <iomanip>
#include <limits>
#include <locale>
#include <map>
#include <memory>
#include <optional>
#include <sstream>
#include <stdexcept>
#include <string>
#include <string_view>
#include <utility>
#include <vector>

namespace
{
using Desktop::BridgeJson::DecodeUtf8;
using Desktop::BridgeJson::EncodeUtf8;
using Desktop::BridgeJson::EscapeJson;
constexpr std::size_t MaxInputBytes = 65536;
constexpr std::size_t MaxPixels = 4 * 1024 * 1024;
constexpr double MaxCoordinate = 1e12;
struct GraphRow
{
    std::string input;
    std::wstring request;
    Graphing::Color color{0, 120, 212};
    Graphing::Renderer::LineStyle lineStyle = Graphing::Renderer::LineStyle::Solid;
    bool visible = true;
    std::vector<std::wstring> parameters;
    std::size_t equationCount = 0;
};
struct ViewRange { double left = -10; double right = 10; double bottom = -10; double top = 10; };
struct ParameterControl { double minimum = -5; double maximum = 5; double step = 0.1; };
struct GraphSettings
{
    double lineWidth = 2;
    Graphing::Color background{255, 255, 255};
    Graphing::Color text{45, 45, 45};
    Graphing::Color axis{100, 100, 100};
    Graphing::Color grid{220, 220, 220};
    unsigned featuresMask = 0;
    bool showAxes = true;
    bool showGrid = true;
};
struct GraphRuntime
{
    std::unique_ptr<Graphing::IMathSolver> solver;
    std::shared_ptr<Graphing::IGraph> graph;
    std::vector<std::shared_ptr<Graphing::IEquation>> equations;
    std::vector<std::size_t> equationRows;
};
std::string ReadText(const char* text, std::size_t maximum = MaxInputBytes)
{
    if (!text) throw std::invalid_argument("Text is required.");
    std::size_t size = 0;
    while (size <= maximum && text[size]) ++size;
    if (size > maximum) throw std::invalid_argument("Text exceeds the supported length.");
    return std::string(text, size);
}
std::string Quote(std::string_view text) { return "\"" + EscapeJson(text) + "\""; }
std::string Number(double value)
{
    if (!std::isfinite(value)) return "null";
    std::ostringstream output; output.imbue(std::locale::classic());
    output << std::setprecision(std::numeric_limits<double>::max_digits10) << (value == 0 ? 0 : value);
    return output.str();
}
void RequireSuccess(HRESULT result, const char* error)
{
    if (result < 0) throw std::runtime_error(error);
}
Graphing::EvalTrigUnitMode Angle(int unit)
{
    if (unit == 0) return Graphing::EvalTrigUnitMode::Radians;
    if (unit == 1) return Graphing::EvalTrigUnitMode::Degrees;
    if (unit == 2) return Graphing::EvalTrigUnitMode::Grads;
    throw std::invalid_argument("Angle unit must be radians, degrees, or grads.");
}
void ValidateRange(const ViewRange& range)
{
    if (!std::isfinite(range.left) || !std::isfinite(range.right) || !std::isfinite(range.bottom) || !std::isfinite(range.top)
        || std::abs(range.left) > MaxCoordinate || std::abs(range.right) > MaxCoordinate
        || std::abs(range.bottom) > MaxCoordinate || std::abs(range.top) > MaxCoordinate
        || range.right - range.left < 1e-12 || range.top - range.bottom < 1e-12)
        throw std::invalid_argument("Ranges need finite bounds, positive spans, and coordinates within ±1e12.");
}
std::unique_ptr<Graphing::IMathSolver> CreateSolver(int unit)
{
    auto solver = Graphing::IMathSolver::CreateMathSolver();
    if (!solver) throw std::runtime_error("Graphing engine is unavailable.");
    solver->ParsingOptions().SetFormatType(Graphing::FormatType::Linear);
    solver->ParsingOptions().SetLocalizationType(Graphing::LocalizationType::DecimalPointAndListComma);
    solver->EvalOptions().SetTrigUnitMode(Angle(unit));
    return solver;
}
std::unique_ptr<Graphing::IExpression> Parse(Graphing::IMathSolver& solver, const std::wstring& text)
{
    int code = 0, type = 0;
    auto expression = solver.ParseInput(text, code, type);
    if (!expression) throw std::invalid_argument("Check the equation syntax. The accepted graph is preserved.");
    return expression;
}
std::wstring WrapRequest(const std::wstring& content)
{
    return L"<math xmlns=\"http://www.w3.org/1998/Math/MathML\"><mrow><mi>show2d</mi><mfenced separators=\"\">"
         + content + L"</mfenced></mrow></math>";
}
void ConfigureGraph(Graphing::IGraph& graph, const GraphSettings& settings = {})
{
    auto& options = graph.GetOptions();
    options.SetAllowKeyGraphFeaturesForFunctionsWithParameters(true);
    options.SetForceProportional(false);
    options.SetMaxExecutionTime(1000);
    options.SetBackColor(settings.background);
    options.SetFontColor(settings.text);
    options.SetAxisColor(settings.axis);
    options.SetGridColor(settings.grid);
    options.SetMarkZeros((settings.featuresMask & 1u) != 0);
    options.SetMarkYIntercept((settings.featuresMask & 2u) != 0);
    options.SetMarkMinima((settings.featuresMask & 4u) != 0);
    options.SetMarkMaxima((settings.featuresMask & 8u) != 0);
    options.SetMarkInflectionPoints((settings.featuresMask & 16u) != 0);
    options.SetMarkVerticalAsymptotes((settings.featuresMask & 32u) != 0);
    options.SetMarkHorizontalAsymptotes((settings.featuresMask & 64u) != 0);
    options.SetMarkObliqueAsymptotes((settings.featuresMask & 128u) != 0);
    options.SetShowAxis(settings.showAxes);
    options.SetShowGrid(settings.showGrid);
}
std::vector<std::string> SplitEquations(const std::string& input)
{
    DecodeUtf8(input);
    std::vector<std::string> lines;
    std::size_t start = 0;
    while (start <= input.size())
    {
        const auto end = input.find_first_of("\n\r", start);
        const auto line = input.substr(start, end - start);
        const auto first = line.find_first_not_of(" \t\r");
        if (first != std::string::npos) lines.push_back(line.substr(first, line.find_last_not_of(" \t\r") - first + 1));
        if (end == std::string::npos) break;
        start = end + 1;
    }
    if (lines.empty()) throw std::invalid_argument("Enter an equation to plot.");
    if (lines.size() > 32) throw std::invalid_argument("Use at most 32 equations.");
    return lines;
}
bool IsIndependentVariable(const std::wstring& name)
{
    return name == L"x" || name == L"y";
}
std::vector<GraphRow> ParseRows(const std::vector<std::string>& lines, int unit, const std::vector<GraphRow>& previous,
                              std::map<std::wstring, double>& parameters)
{
    auto solver = CreateSolver(unit);
    solver->FormatOptions().SetFormatType(Graphing::FormatType::MathMLNoWrapper);
    solver->FormatOptions().SetMathMLPrefix(L"");
    const std::array<Graphing::Color, 6> palette = {{{0, 120, 212}, {220, 40, 40}, {25, 145, 65}, {140, 70, 190}, {220, 130, 20}, {30, 30, 30}}};
    std::vector<GraphRow> rows;
    auto retained = parameters;
    for (std::size_t index = 0; index < lines.size(); ++index)
    {
        GraphRow row; row.input = lines[index]; row.color = index < previous.size() ? previous[index].color : palette[index % palette.size()];
        row.visible = index < previous.size() && previous[index].input == row.input ? previous[index].visible : true;
        auto expression = Parse(*solver, DecodeUtf8(row.input));
        auto graph = solver->CreateGrapher(expression.get());
        if (!graph) throw std::invalid_argument("This equation cannot be plotted.");
        const auto initialized = graph->TryInitialize();
        if (!initialized || initialized->empty()) throw std::invalid_argument("This equation cannot be plotted. The accepted graph is preserved.");
        row.equationCount = initialized->size();
        for (const auto& variable : graph->GetVariables())
        {
            if (!variable) throw std::runtime_error("The graph contains an unavailable variable.");
            const auto name = variable->GetVariableName();
            if (IsIndependentVariable(name)) continue;
            row.parameters.push_back(name);
            const auto existing = parameters.find(name);
            retained[name] = existing == parameters.end() ? 1 : existing->second;
        }
        std::sort(row.parameters.begin(), row.parameters.end());
        const auto serialized = solver->Serialize(expression.get());
        if (serialized.empty()) throw std::invalid_argument("The equation cannot be serialized for graphing.");
        const auto wide = DecodeUtf8(row.input);
        const bool inequality = wide.find_first_of(L"<>≤≥") != std::wstring::npos;
        row.lineStyle = index < previous.size() ? previous[index].lineStyle
            : inequality ? Graphing::Renderer::LineStyle::Dash : Graphing::Renderer::LineStyle::Solid;
        const auto wrapper = inequality ? L"plotIneq2D" : wide.find(L'=') != std::wstring::npos ? L"plotEq2d" : L"plot2d";
        row.request = L"<mrow><mi>" + std::wstring(wrapper) + L"</mi><mfenced separators=\"\">" + serialized + L"</mfenced></mrow>";
        rows.push_back(std::move(row));
    }
    parameters = std::move(retained);
    return rows;
}
GraphRuntime BuildRuntime(const std::vector<GraphRow>& rows, int unit, const std::map<std::wstring, double>& parameters,
                          const ViewRange& range, int selected, const GraphSettings& settings)
{
    GraphRuntime runtime; runtime.solver = CreateSolver(unit);
    std::wstring request;
    for (std::size_t row = 0; row < rows.size(); ++row)
    {
        if (!rows[row].visible) continue;
        if (!request.empty()) request += L"<mo>,</mo>";
        request += rows[row].request;
        runtime.equationRows.insert(runtime.equationRows.end(), rows[row].equationCount, row);
    }
    if (request.empty()) return runtime;
    runtime.solver->ParsingOptions().SetFormatType(Graphing::FormatType::MathML);
    auto expression = Parse(*runtime.solver, WrapRequest(request));
    runtime.graph = runtime.solver->CreateGrapher(expression.get());
    if (!runtime.graph) throw std::runtime_error("The graph could not be created.");
    const auto initialized = runtime.graph->TryInitialize();
    if (!initialized || initialized->size() != runtime.equationRows.size()) throw std::runtime_error("The graph could not be updated.");
    runtime.equations = *initialized;
    ConfigureGraph(*runtime.graph, settings);
    for (const auto& parameter : parameters) runtime.graph->SetArgValue(parameter.first, parameter.second);
    for (std::size_t index = 0; index < runtime.equations.size(); ++index)
    {
        const auto row = runtime.equationRows[index];
        if (!runtime.equations[index]) throw std::runtime_error("The graph contains an unavailable equation.");
        const auto options = runtime.equations[index]->GetGraphEquationOptions();
        options->SetGraphColor(rows[row].color);
        options->SetLineStyle(rows[row].lineStyle);
        options->SetLineWidth(static_cast<float>(settings.lineWidth));
        options->SetSelectedEquationLineWidth(static_cast<float>(settings.lineWidth + (settings.lineWidth <= 2 ? 1 : 2)));
    }
    runtime.graph->TryResetSelection();
    for (std::size_t index = 0; index < runtime.equations.size(); ++index)
        if (static_cast<int>(runtime.equationRows[index]) == selected) { runtime.equations[index]->TrySelectEquation(); break; }
    RequireSuccess(runtime.graph->GetRenderer()->SetDisplayRanges(range.left, range.right, range.bottom, range.top), "The viewport bounds were rejected.");
    return runtime;
}
std::string DecodeMathEntities(const std::string& text)
{
    std::string output;
    for (std::size_t index = 0; index < text.size();)
    {
        if (text[index] != '&') { output += text[index++]; continue; }
        const auto end = text.find(';', index);
        if (end == std::string::npos || end - index > 16) throw std::invalid_argument("Invalid math text entity.");
        const auto entity = text.substr(index + 1, end - index - 1);
        if (entity == "lt") output += '<'; else if (entity == "gt") output += '>'; else if (entity == "amp") output += '&';
        else if (entity == "quot") output += '"'; else if (entity == "apos") output += '\'';
        else if (!entity.empty() && entity[0] == '#')
        {
            const bool hexadecimal = entity.size() > 1 && (entity[1] == 'x' || entity[1] == 'X');
            std::size_t consumed; const auto value = std::stoul(entity.substr(hexadecimal ? 2 : 1), &consumed, hexadecimal ? 16 : 10);
            if (consumed != entity.size() - (hexadecimal ? 2 : 1) || value > 0x10ffff || (value >= 0xd800 && value <= 0xdfff))
                throw std::invalid_argument("Invalid math text code point.");
            std::wstring wide;
            if (sizeof(wchar_t) == 2 && value > 0xffff)
            { wide.push_back(static_cast<wchar_t>(0xd800 + ((value - 0x10000) >> 10))); wide.push_back(static_cast<wchar_t>(0xdc00 + ((value - 0x10000) & 1023))); }
            else wide.push_back(static_cast<wchar_t>(value));
            output += EncodeUtf8(wide);
        }
        else throw std::invalid_argument("Unsupported math text entity.");
        index = end + 1;
    }
    return output;
}
std::string ReadMath(const std::wstring& value)
{
    if (value.empty()) return "Unavailable";
    const auto xml = EncodeUtf8(value);
    if (xml[0] != '<') return xml;
    if (xml.size() > MaxInputBytes) return "Unavailable";
    struct Frame { std::string name; std::vector<std::string> children; };
    std::vector<Frame> stack;
    std::string output;
    try
    {
        for (std::size_t index = 0; index < xml.size();)
        {
            if (xml[index] != '<')
            {
                const auto end = xml.find('<', index);
                const auto text = DecodeMathEntities(xml.substr(index, end - index));
                if (!stack.empty()) stack.back().children.push_back(text);
                else if (text.find_first_not_of(" \t\n\r") != std::string::npos) return "Unavailable";
                index = end == std::string::npos ? xml.size() : end; continue;
            }
            const auto end = xml.find('>', index);
            if (end == std::string::npos) return "Unavailable";
            const bool closing = index + 1 < xml.size() && xml[index + 1] == '/';
            auto tag = xml.substr(index + (closing ? 2 : 1), end - index - (closing ? 2 : 1));
            tag = tag.substr(0, tag.find_first_of(" \t\r\n"));
            const auto colon = tag.find(':'); if (colon != std::string::npos) tag = tag.substr(colon + 1);
            if (!closing) { stack.push_back({tag, {}}); if (stack.size() > 64) return "Unavailable"; }
            else
            {
                if (stack.empty() || stack.back().name != tag) return "Unavailable";
                auto frame = std::move(stack.back()); stack.pop_back();
                std::string text;
                if ((tag == "mfrac" || tag == "msup" || tag == "msub") && frame.children.size() == 2)
                    text = "(" + frame.children[0] + ")" + (tag == "mfrac" ? "/" : tag == "msup" ? "^" : "_") + "(" + frame.children[1] + ")";
                else
                {
                    for (const auto& child : frame.children) text += child;
                    if (tag == "msqrt") text = "sqrt(" + text + ")";
                    if (tag == "mfenced") text = "(" + text + ")";
                }
                if (stack.empty()) output += text; else stack.back().children.push_back(std::move(text));
            }
            index = end + 1;
        }
    }
    catch (const std::exception&) { return "Unavailable"; }
    return stack.empty() && !output.empty() ? output : "Unavailable";
}
std::string AnalyzeRow(const GraphRow& row, int unit, const std::map<std::wstring, double>& parameters)
{
    auto solver = CreateSolver(unit);
    solver->ParsingOptions().SetFormatType(Graphing::FormatType::MathML);
    auto expression = Parse(*solver, WrapRequest(row.request));
    auto graph = solver->CreateGrapher(expression.get());
    if (!graph || !graph->TryInitialize()) return "Analysis unavailable for this equation.";
    ConfigureGraph(*graph);
    for (const auto& parameter : parameters) graph->SetArgValue(parameter.first, parameter.second);
    auto analyzer = graph->GetAnalyzer();
    if (!analyzer) return "Analysis unavailable for this equation.";
    bool notX = false;
    if (!analyzer->CanFunctionAnalysisBePerformed(notX)) return "Analysis unavailable for this equation.";
    if (notX) return "Analysis is available for functions of x.";
    if (analyzer->PerformFunctionAnalysis(255) < 0) return "Analysis unavailable for this equation.";
    const auto data = solver->Analyze(analyzer.get());
    std::string report = "Domain\n" + ReadMath(data.Domain) + "\n\nRange\n" + ReadMath(data.Range)
                       + "\n\nZeros\n" + ReadMath(data.Zeros) + "\n\nY-intercept\n" + ReadMath(data.YIntercept) + '\n';
    const auto append = [&](const char* name, const std::vector<std::wstring>& values, unsigned bit) {
        report += '\n' + std::string(name) + '\n';
        if (values.empty()) report += (static_cast<unsigned>(data.TooComplexFeatures) & bit) ? "Unavailable\n" : "None\n";
        else for (const auto& value : values) report += ReadMath(value) + '\n';
    };
    append("Minima", data.Minima, 64); append("Maxima", data.Maxima, 128); append("Inflection points", data.InflectionPoints, 256);
    append("Vertical asymptotes", data.VerticalAsymptotes, 512); append("Horizontal asymptotes", data.HorizontalAsymptotes, 1024);
    append("Oblique asymptotes", data.ObliqueAsymptotes, 2048);
    const auto parity = data.Parity == 2 ? "Even" : data.Parity == 1 ? "Odd" : data.Parity == 3 ? "Neither even nor odd" : "Unavailable";
    report += "\nParity\n" + std::string(parity) + "\n\nPeriod\n" + (data.PeriodicityDirection == 2 ? std::string("Not periodic") : ReadMath(data.PeriodicityExpression));
    report += "\n\nMonotonicity\n";
    if (data.MonotoneIntervals.empty()) report += "Unavailable\n";
    for (const auto& interval : data.MonotoneIntervals)
        report += ReadMath(interval.first) + ": " + (interval.second == 1 ? "increasing" : interval.second == 2 ? "decreasing" : interval.second == 3 ? "constant" : "unavailable") + '\n';
    return report;
}
std::string ColorText(Graphing::Color color)
{
    std::ostringstream output; output << '#' << std::hex << std::uppercase << std::setfill('0')
        << std::setw(2) << static_cast<unsigned>(color.R) << std::setw(2) << static_cast<unsigned>(color.G) << std::setw(2) << static_cast<unsigned>(color.B);
    return output.str();
}
std::string SceneColor(Graphing::Color color)
{
    std::ostringstream output; output << '#' << std::hex << std::uppercase << std::setfill('0')
        << std::setw(2) << static_cast<unsigned>(color.A) << std::setw(2) << static_cast<unsigned>(color.R)
        << std::setw(2) << static_cast<unsigned>(color.G) << std::setw(2) << static_cast<unsigned>(color.B);
    return output.str();
}
void ValidateSize(int width, int height)
{
    if (width < 16 || height < 16 || width > 4096 || height > 4096 || static_cast<std::size_t>(width) * static_cast<std::size_t>(height) > MaxPixels)
        throw std::invalid_argument("Graph size must be 16..4096 per axis, with at most 4194304 pixels.");
}
std::string PointJson(graphing::Point point) { return "{\"x\":" + Number(point.x) + ",\"y\":" + Number(point.y) + '}'; }
std::string PointsJson(const std::vector<graphing::Point>& points)
{
    std::string output = "[";
    for (std::size_t index = 0; index < points.size(); ++index) { if (index) output += ','; output += PointJson(points[index]); }
    return output + ']';
}
std::string SceneJson(const graphing::Scene& scene)
{
    std::string output = "{\"width\":" + std::to_string(scene.width) + ",\"height\":" + std::to_string(scene.height)
                       + ",\"background\":" + Quote(SceneColor(scene.background)) + ",\"strokes\":[";
    for (std::size_t index = 0; index < scene.strokes.size(); ++index)
    {
        if (index) output += ',';
        const auto& stroke = scene.strokes[index];
        output += "{\"points\":" + PointsJson(stroke.points) + ",\"color\":" + Quote(SceneColor(stroke.color))
                + ",\"width\":" + Number(stroke.width) + ",\"style\":" + std::to_string(static_cast<int>(stroke.style))
                + ",\"equationId\":" + std::to_string(stroke.equation_id) + '}';
        if (output.size() > 32 * 1024 * 1024) throw std::runtime_error("Graph geometry exceeds the scene transfer limit.");
    }
    output += "],\"fills\":[";
    for (std::size_t index = 0; index < scene.fills.size(); ++index)
    {
        if (index) output += ',';
        const auto& fill = scene.fills[index];
        output += "{\"points\":" + PointsJson(fill.polygon) + ",\"color\":" + Quote(SceneColor(fill.color)) + '}';
        if (output.size() > 32 * 1024 * 1024) throw std::runtime_error("Graph geometry exceeds the scene transfer limit.");
    }
    output += "],\"labels\":[";
    for (std::size_t index = 0; index < scene.labels.size(); ++index)
    {
        if (index) output += ',';
        const auto& label = scene.labels[index];
        output += "{\"x\":" + Number(label.position.x) + ",\"y\":" + Number(label.position.y)
                + ",\"text\":" + Quote(EncodeUtf8(label.text)) + ",\"color\":" + Quote(SceneColor(label.color)) + '}';
    }
    return output + "],\"hasMissingData\":" + (scene.has_missing_data ? "true" : "false") + '}';
}
void ValidateNormalized(double x, double y)
{
    if (!std::isfinite(x) || !std::isfinite(y) || std::abs(x) > 1 || std::abs(y) > 1)
        throw std::invalid_argument("Normalized coordinates must be finite and within [-1, 1].");
}
}

struct CalcGraphSession
{
    std::vector<GraphRow> rows;
    std::map<std::wstring, double> parameters;
    std::map<std::wstring, ParameterControl> parameterControls;
    GraphSettings settings;
    GraphRuntime runtime;
    ViewRange range;
    int selected = -1;
    int angleUnit = 0;
    int width = 640;
    int height = 480;
    std::string error;
    bool errorStorageFailed = false;
    std::string json;
    std::string analysis;
    std::string renderWarning;
    std::vector<unsigned char> png;
};

namespace
{
std::size_t ValidateRow(const CalcGraphSession& session, int row)
{
    if (row < 0 || static_cast<std::size_t>(row) >= session.rows.size()) throw std::invalid_argument("Select an accepted equation row.");
    return static_cast<std::size_t>(row);
}
std::wstring ValidateParameter(const CalcGraphSession& session, int row, const char* name)
{
    const auto index = ValidateRow(session, row);
    const auto parameter = DecodeUtf8(ReadText(name, 512));
    const auto& allowed = session.rows[index].parameters;
    if (std::find(allowed.begin(), allowed.end(), parameter) == allowed.end())
        throw std::invalid_argument("This equation does not contain that parameter.");
    return parameter;
}
void StoreError(CalcGraphSession& session, const char* error) noexcept
{
    try { session.error = error; session.errorStorageFailed = false; }
    catch (...) { session.errorStorageFailed = true; }
}
template<class Action> int Mutate(CalcGraphSession* handle, Action action)
{
    if (!handle) return 0;
    try { action(*handle); handle->error.clear(); handle->errorStorageFailed = false; return 1; }
    catch (const std::exception& error) { StoreError(*handle, error.what()); }
    catch (...) { StoreError(*handle, "The graph operation failed."); }
    return 0;
}
void CommitRows(CalcGraphSession& session, std::vector<GraphRow> rows, std::map<std::wstring, double> parameters, int angle, int selected,
                const GraphSettings* requestedSettings = nullptr)
{
    std::size_t inputBytes = 0;
    std::map<std::wstring, double> retained;
    std::map<std::wstring, ParameterControl> controls;
    for (const auto& row : rows)
    {
        if (inputBytes) ++inputBytes;
        inputBytes += row.input.size();
        if (inputBytes > MaxInputBytes) throw std::invalid_argument("Equations exceed the supported total length.");
        for (const auto& name : row.parameters)
        {
            retained[name] = parameters.at(name);
            const auto previous = session.parameterControls.find(name);
            controls[name] = previous == session.parameterControls.end() ? ParameterControl{} : previous->second;
        }
    }
    parameters = std::move(retained);
    const auto settings = requestedSettings ? *requestedSettings : session.settings;
    auto runtime = BuildRuntime(rows, angle, parameters, session.range, selected, settings);
    auto analysis = selected < 0 ? std::string("Select an equation to analyze.") : AnalyzeRow(rows[static_cast<std::size_t>(selected)], angle, parameters);
    session.rows = std::move(rows); session.parameters = std::move(parameters); session.runtime = std::move(runtime);
    session.parameterControls = std::move(controls); session.settings = settings;
    session.angleUnit = angle; session.selected = selected; session.analysis = std::move(analysis); session.renderWarning.clear();
}
void CommitRange(CalcGraphSession& session, const ViewRange& range)
{
    ValidateRange(range);
    if (session.runtime.graph)
        RequireSuccess(session.runtime.graph->GetRenderer()->SetDisplayRanges(range.left, range.right, range.bottom, range.top), "The viewport bounds were rejected.");
    session.range = range;
}
graphing::Scene ReadScene(CalcGraphSession& session, int width, int height)
{
    ValidateSize(width, height);
    graphing::Scene scene;
    if (session.runtime.graph)
    {
        auto renderer = session.runtime.graph->GetRenderer();
        RequireSuccess(renderer->SetGraphSize(static_cast<unsigned>(width), static_cast<unsigned>(height)), "Graph dimensions were rejected.");
        RequireSuccess(graphing::GetScene(*renderer, scene), "The graph could not be rendered.");
    }
    else { scene.width = static_cast<unsigned>(width); scene.height = static_cast<unsigned>(height); scene.background = session.settings.background; }
    return scene;
}
std::string StateJson(const CalcGraphSession& session)
{
    std::string output = "{\"equations\":[";
    for (std::size_t index = 0; index < session.rows.size(); ++index)
    {
        if (index) output += ',';
        const auto& row = session.rows[index];
        output += "{\"input\":" + Quote(row.input) + ",\"color\":" + Quote(ColorText(row.color))
                + ",\"visible\":" + (row.visible ? "true" : "false") + ",\"selected\":" + (session.selected == static_cast<int>(index) ? "true" : "false") + ",\"parameters\":[";
        for (std::size_t parameter = 0; parameter < row.parameters.size(); ++parameter)
        {
            if (parameter) output += ',';
            const auto& name = row.parameters[parameter];
            const auto& control = session.parameterControls.at(name);
            output += "{\"name\":" + Quote(EncodeUtf8(name)) + ",\"value\":" + Number(session.parameters.at(name))
                    + ",\"min\":" + Number(control.minimum) + ",\"max\":" + Number(control.maximum)
                    + ",\"step\":" + (control.step == 0.1 ? std::string("0.1") : Number(control.step)) + '}';
        }
        output += "],\"lineStyle\":" + std::to_string(static_cast<int>(row.lineStyle)) + '}';
    }
    output += "],\"selectedIndex\":" + std::to_string(session.selected) + ",\"angleUnit\":" + std::to_string(session.angleUnit)
            + ",\"range\":{\"left\":" + Number(session.range.left) + ",\"right\":" + Number(session.range.right)
            + ",\"bottom\":" + Number(session.range.bottom) + ",\"top\":" + Number(session.range.top) + "},\"analysis\":" + Quote(session.analysis)
            + ",\"renderWarning\":" + Quote(session.renderWarning)
            + ",\"settings\":{\"lineWidth\":" + Number(session.settings.lineWidth)
            + ",\"background\":" + Quote(SceneColor(session.settings.background)) + ",\"text\":" + Quote(SceneColor(session.settings.text))
            + ",\"axis\":" + Quote(SceneColor(session.settings.axis)) + ",\"grid\":" + Quote(SceneColor(session.settings.grid))
            + ",\"featuresMask\":" + std::to_string(session.settings.featuresMask)
            + ",\"showAxes\":" + (session.settings.showAxes ? "true" : "false")
            + ",\"showGrid\":" + (session.settings.showGrid ? "true" : "false") + '}'
            + ",\"renderCapabilities\":{\"backend\":\"portable-scene-and-png\",\"axes\":true,\"grid\":true,\"sceneLabels\":\"Unicode text for frontend fonts\",\"pngLabels\":\"limited bitmap glyph coverage\"}}";
    return output;
}
std::optional<std::string> ReadTrace(const CalcGraphSession& session, const GraphRuntime& runtime,
                                   double xNormalized, double yNormalized, double precision)
{
    ValidateNormalized(xNormalized, yNormalized);
    if (!std::isfinite(precision) || precision <= 0 || precision > MaxCoordinate)
        throw std::invalid_argument("Tracing precision must be finite, positive, and bounded.");
    if (!runtime.graph) return std::nullopt;
    auto renderer = runtime.graph->GetRenderer();
    RequireSuccess(renderer->SetGraphSize(static_cast<unsigned>(session.width), static_cast<unsigned>(session.height)), "Tracing dimensions were rejected.");
    const double sx = (xNormalized + 1) / 2 * (session.width - 1), sy = (1 - yNormalized) / 2 * (session.height - 1);
    int id = -1; float screenX, screenY; double x, y, rho, theta, t;
    const auto result = renderer->GetClosePointData(sx, sy, precision, id, screenX, screenY, x, y, rho, theta, t);
    if (result == S_FALSE) return std::nullopt;
    RequireSuccess(result, "Tracing could not find a curve point.");
    if (id < 0 || !std::isfinite(x) || !std::isfinite(y)) return std::nullopt;
    std::size_t row = session.rows.size();
    for (std::size_t index = 0; index < runtime.equations.size(); ++index)
        if (runtime.equations[index]->GetGraphEquationID() == static_cast<unsigned>(id)) { row = runtime.equationRows[index]; break; }
    if (row == session.rows.size()) return std::nullopt;
    return "{\"row\":" + std::to_string(row) + ",\"x\":" + Number(x) + ",\"y\":" + Number(y)
         + ",\"screenX\":" + Number(screenX) + ",\"screenY\":" + Number(screenY) + ",\"rho\":" + Number(rho)
         + ",\"theta\":" + Number(theta) + ",\"t\":" + Number(t) + '}';
}
}

extern "C"
{
CalcGraphSession* calc_graph_create(void)
{
    try { return new CalcGraphSession; } catch (...) { return nullptr; }
}
void calc_graph_destroy(CalcGraphSession* handle)
{
    try { delete handle; } catch (...) {}
}
const char* calc_graph_last_error(CalcGraphSession* handle)
{
    if (!handle) return "Graph session is required.";
    return handle->errorStorageFailed ? "The graph operation failed." : handle->error.c_str();
}
int calc_graph_update(CalcGraphSession* handle, const char* equations, int angleUnit)
{
    return Mutate(handle, [&](CalcGraphSession& session) {
        Angle(angleUnit);
        auto parameters = session.parameters;
        auto rows = ParseRows(SplitEquations(ReadText(equations)), angleUnit, session.rows, parameters);
        const int selected = session.selected < 0 || static_cast<std::size_t>(session.selected) >= rows.size() ? 0 : session.selected;
        CommitRows(session, std::move(rows), std::move(parameters), angleUnit, selected);
    });
}
int calc_graph_set_equation(CalcGraphSession* handle, int row, const char* text)
{
    return Mutate(handle, [&](CalcGraphSession& session) {
        const auto index = ValidateRow(session, row);
        const auto lines = SplitEquations(ReadText(text));
        if (lines.size() != 1) throw std::invalid_argument("Enter one equation for this row.");
        auto parameters = session.parameters;
        auto replacement = ParseRows(lines, session.angleUnit, {session.rows[index]}, parameters);
        replacement[0].visible = session.rows[index].visible;
        auto rows = session.rows; rows[index] = std::move(replacement[0]);
        CommitRows(session, std::move(rows), std::move(parameters), session.angleUnit, session.selected);
    });
}
int calc_graph_remove_equation(CalcGraphSession* handle, int row)
{
    return Mutate(handle, [&](CalcGraphSession& session) {
        const auto index = ValidateRow(session, row);
        auto rows = session.rows; rows.erase(rows.begin() + static_cast<std::ptrdiff_t>(index));
        int selected = session.selected;
        if (rows.empty()) selected = -1;
        else if (selected > row) --selected;
        else if (selected == row) selected = std::min(row, static_cast<int>(rows.size()) - 1);
        CommitRows(session, std::move(rows), session.parameters, session.angleUnit, selected);
    });
}
const char* calc_graph_get_state(CalcGraphSession* handle)
{
    if (!handle) return nullptr;
    if (!Mutate(handle, [](CalcGraphSession& session) { session.json = StateJson(session); })) return nullptr;
    return handle->json.c_str();
}
int calc_graph_set_parameter(CalcGraphSession* handle, int row, const char* name, double value)
{
    return Mutate(handle, [&](CalcGraphSession& session) {
        if (!std::isfinite(value) || std::abs(value) > MaxCoordinate) throw std::invalid_argument("Parameter values must be finite and within ±1e12.");
        const auto parameter = ValidateParameter(session, row, name);
        auto parameters = session.parameters; parameters[parameter] = value;
        CommitRows(session, session.rows, std::move(parameters), session.angleUnit, session.selected);
    });
}
int calc_graph_set_parameter_bounds(CalcGraphSession* handle, int row, const char* name, double minimum, double maximum, double step)
{
    return Mutate(handle, [&](CalcGraphSession& session) {
        const auto parameter = ValidateParameter(session, row, name);
        if (!std::isfinite(minimum) || !std::isfinite(maximum) || !std::isfinite(step)
            || std::abs(minimum) > MaxCoordinate || std::abs(maximum) > MaxCoordinate
            || maximum - minimum < 1e-12 || step <= 0 || step > maximum - minimum)
            throw std::invalid_argument("Parameter bounds need finite ordered limits within ±1e12 and a positive step no larger than their span.");
        auto controls = session.parameterControls;
        controls[parameter] = {minimum, maximum, step};
        session.parameterControls = std::move(controls);
    });
}
int calc_graph_set_visible(CalcGraphSession* handle, int row, int visible)
{
    return Mutate(handle, [&](CalcGraphSession& session) {
        const auto index = ValidateRow(session, row);
        if (visible != 0 && visible != 1) throw std::invalid_argument("Visibility must be zero or one.");
        auto rows = session.rows; rows[index].visible = visible == 1;
        CommitRows(session, std::move(rows), session.parameters, session.angleUnit, session.selected);
    });
}
int calc_graph_set_selected(CalcGraphSession* handle, int row)
{
    return Mutate(handle, [&](CalcGraphSession& session) {
        const auto index = ValidateRow(session, row);
        CommitRows(session, session.rows, session.parameters, session.angleUnit, static_cast<int>(index));
    });
}
int calc_graph_set_color(CalcGraphSession* handle, int row, unsigned rgba)
{
    return Mutate(handle, [&](CalcGraphSession& session) {
        const auto index = ValidateRow(session, row);
        auto rows = session.rows; rows[index].color = Graphing::Color{static_cast<std::uint32_t>(rgba)};
        CommitRows(session, std::move(rows), session.parameters, session.angleUnit, session.selected);
    });
}
int calc_graph_set_line_style(CalcGraphSession* handle, int row, int style)
{
    return Mutate(handle, [&](CalcGraphSession& session) {
        const auto index = ValidateRow(session, row);
        if (style < 0 || style > 4) throw std::invalid_argument("Line style must be within [0,4].");
        auto rows = session.rows; rows[index].lineStyle = static_cast<Graphing::Renderer::LineStyle>(style);
        CommitRows(session, std::move(rows), session.parameters, session.angleUnit, session.selected);
    });
}
int calc_graph_set_settings(CalcGraphSession* handle, double lineWidth, unsigned backgroundRgba, unsigned textRgba, unsigned axisRgba, unsigned gridRgba)
{
    return Mutate(handle, [&](CalcGraphSession& session) {
        if (!std::isfinite(lineWidth) || lineWidth < 1 || lineWidth > 4)
            throw std::invalid_argument("Line width must be finite and within [1,4].");
        auto settings = session.settings;
        settings.lineWidth = lineWidth;
        settings.background = Graphing::Color{static_cast<std::uint32_t>(backgroundRgba)};
        settings.text = Graphing::Color{static_cast<std::uint32_t>(textRgba)};
        settings.axis = Graphing::Color{static_cast<std::uint32_t>(axisRgba)};
        settings.grid = Graphing::Color{static_cast<std::uint32_t>(gridRgba)};
        CommitRows(session, session.rows, session.parameters, session.angleUnit, session.selected, &settings);
    });
}
int calc_graph_set_features(CalcGraphSession* handle, unsigned mask)
{
    return Mutate(handle, [&](CalcGraphSession& session) {
        if (mask > 255) throw std::invalid_argument("Feature markers must use only bits 0..7.");
        auto settings = session.settings; settings.featuresMask = mask;
        CommitRows(session, session.rows, session.parameters, session.angleUnit, session.selected, &settings);
    });
}
int calc_graph_set_grid(CalcGraphSession* handle, int showAxes, int showGrid)
{
    return Mutate(handle, [&](CalcGraphSession& session) {
        if ((showAxes != 0 && showAxes != 1) || (showGrid != 0 && showGrid != 1))
            throw std::invalid_argument("Axes and grid visibility must be zero or one.");
        auto settings = session.settings; settings.showAxes = showAxes == 1; settings.showGrid = showGrid == 1;
        CommitRows(session, session.rows, session.parameters, session.angleUnit, session.selected, &settings);
    });
}
int calc_graph_set_range(CalcGraphSession* handle, double left, double right, double bottom, double top)
{
    return Mutate(handle, [&](CalcGraphSession& session) { CommitRange(session, {left, right, bottom, top}); });
}
int calc_graph_zoom(CalcGraphSession* handle, double factor, double centerXNormalized, double centerYNormalized)
{
    return Mutate(handle, [&](CalcGraphSession& session) {
        ValidateNormalized(centerXNormalized, centerYNormalized);
        if (!std::isfinite(factor) || factor < 0.001 || factor > 1000) throw std::invalid_argument("Zoom factor must be within [0.001, 1000].");
        const auto& r = session.range;
        const double x = r.left / 2 + r.right / 2 + centerXNormalized * (r.right - r.left) / 2;
        const double y = r.bottom / 2 + r.top / 2 + centerYNormalized * (r.top - r.bottom) / 2;
        CommitRange(session, {x + (r.left - x) * factor, x + (r.right - x) * factor, y + (r.bottom - y) * factor, y + (r.top - y) * factor});
    });
}
int calc_graph_pan(CalcGraphSession* handle, double xNormalized, double yNormalized)
{
    return Mutate(handle, [&](CalcGraphSession& session) {
        ValidateNormalized(xNormalized, yNormalized);
        const auto& r = session.range;
        const double x = xNormalized * (r.right - r.left) / 2, y = yNormalized * (r.top - r.bottom) / 2;
        CommitRange(session, {r.left + x, r.right + x, r.bottom + y, r.top + y});
    });
}
const char* calc_graph_get_scene(CalcGraphSession* handle, int width, int height)
{
    if (!handle) return nullptr;
    if (!Mutate(handle, [&](CalcGraphSession& session) {
        const auto scene = ReadScene(session, width, height);
        auto json = SceneJson(scene);
        session.json = std::move(json); session.width = width; session.height = height;
        session.renderWarning = scene.has_missing_data ? "Some graph data could not be drawn within the rendering limits." : "";
    })) return nullptr;
    return handle->json.c_str();
}
int calc_graph_render_png(CalcGraphSession* handle, int width, int height, const unsigned char** bytes, size_t* length)
{
    if (bytes) *bytes = nullptr;
    if (length) *length = 0;
    return Mutate(handle, [&](CalcGraphSession& session) {
        if (!bytes || !length) throw std::invalid_argument("PNG output pointers are required.");
        const auto scene = ReadScene(session, width, height);
        auto png = graphing::EncodePng(scene);
        if (png.empty()) throw std::runtime_error("PNG encoding failed.");
        session.png = std::move(png); session.width = width; session.height = height;
        session.renderWarning = scene.has_missing_data ? "Some graph data could not be drawn within the rendering limits." : "";
        *bytes = session.png.data(); *length = session.png.size();
    });
}
const char* calc_graph_trace(CalcGraphSession* handle, double xNormalized, double yNormalized, double precision)
{
    if (!handle) return nullptr;
    bool found = false;
    const int success = Mutate(handle, [&](CalcGraphSession& session) {
        auto trace = ReadTrace(session, session.runtime, xNormalized, yNormalized, precision);
        if (trace) { session.json = std::move(*trace); found = true; }
    });
    return success && found ? handle->json.c_str() : nullptr;
}
const char* calc_graph_trace_selected(CalcGraphSession* handle, double xNormalized, double yNormalized, double precision)
{
    if (!handle) return nullptr;
    bool found = false;
    const int success = Mutate(handle, [&](CalcGraphSession& session) {
        ValidateNormalized(xNormalized, yNormalized);
        if (!std::isfinite(precision) || precision <= 0 || precision > MaxCoordinate)
            throw std::invalid_argument("Tracing precision must be finite, positive, and bounded.");
        if (session.selected < 0 || !session.rows[static_cast<std::size_t>(session.selected)].visible) return;
        auto rows = session.rows;
        for (std::size_t row = 0; row < rows.size(); ++row) rows[row].visible = static_cast<int>(row) == session.selected;
        auto settings = session.settings; settings.featuresMask = 0;
        const auto runtime = BuildRuntime(rows, session.angleUnit, session.parameters, session.range, session.selected, settings);
        auto trace = ReadTrace(session, runtime, xNormalized, yNormalized, precision);
        if (trace) { session.json = std::move(*trace); found = true; }
    });
    return success && found ? handle->json.c_str() : nullptr;
}
const char* calc_graph_analyze(CalcGraphSession* handle, int row)
{
    if (!handle) return nullptr;
    if (!Mutate(handle, [&](CalcGraphSession& session) {
        const auto index = ValidateRow(session, row);
        session.analysis = AnalyzeRow(session.rows[index], session.angleUnit, session.parameters);
    })) return nullptr;
    return handle->analysis.c_str();
}
}

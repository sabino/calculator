// Licensed under the MIT License.
#pragma once

#include "Bridge.h"
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef struct CalcGraphSession CalcGraphSession;

CALCULATOR_NATIVE_API CalcGraphSession* calc_graph_create(void);
CALCULATOR_NATIVE_API void calc_graph_destroy(CalcGraphSession* handle);
CALCULATOR_NATIVE_API const char* calc_graph_last_error(CalcGraphSession* handle);
CALCULATOR_NATIVE_API int calc_graph_update(CalcGraphSession* handle, const char* equations, int angleUnit);
// Row edits preserve the other rows and the edited row's appearance. Removing the last row is allowed.
CALCULATOR_NATIVE_API int calc_graph_set_equation(CalcGraphSession* handle, int row, const char* text);
CALCULATOR_NATIVE_API int calc_graph_remove_equation(CalcGraphSession* handle, int row);
CALCULATOR_NATIVE_API const char* calc_graph_get_state(CalcGraphSession* handle);
CALCULATOR_NATIVE_API int calc_graph_set_parameter(CalcGraphSession* handle, int row, const char* name, double value);
// Bounds belong to the named parameter across all rows. Values outside the bounds are preserved.
CALCULATOR_NATIVE_API int calc_graph_set_parameter_bounds(CalcGraphSession* handle, int row, const char* name, double minimum, double maximum, double step);
CALCULATOR_NATIVE_API int calc_graph_set_visible(CalcGraphSession* handle, int row, int visible);
CALCULATOR_NATIVE_API int calc_graph_set_selected(CalcGraphSession* handle, int row);
CALCULATOR_NATIVE_API int calc_graph_set_color(CalcGraphSession* handle, int row, unsigned rgba);
// Styles match Graphing::Renderer::LineStyle: solid=0, dot=1, dash=2, dash-dot=3, dash-dot-dot=4.
CALCULATOR_NATIVE_API int calc_graph_set_line_style(CalcGraphSession* handle, int row, int style);
// Width is within [1,4]. Color arguments use 0xRRGGBBAA; state settings use #AARRGGBB.
CALCULATOR_NATIVE_API int calc_graph_set_settings(CalcGraphSession* handle, double lineWidth, unsigned backgroundRgba, unsigned textRgba, unsigned axisRgba, unsigned gridRgba);
// Bits 0..7: zeros, y-intercept, minima, maxima, inflections, vertical, horizontal, oblique asymptotes.
CALCULATOR_NATIVE_API int calc_graph_set_features(CalcGraphSession* handle, unsigned mask);
CALCULATOR_NATIVE_API int calc_graph_set_grid(CalcGraphSession* handle, int showAxes, int showGrid);
CALCULATOR_NATIVE_API int calc_graph_set_range(CalcGraphSession* handle, double left, double right, double bottom, double top);
CALCULATOR_NATIVE_API int calc_graph_zoom(CalcGraphSession* handle, double factor, double centerXNormalized, double centerYNormalized);
CALCULATOR_NATIVE_API int calc_graph_pan(CalcGraphSession* handle, double xNormalized, double yNormalized);
// Borrowed UTF-8 strings and PNG bytes belong to this session. Copy before the next call or destruction.
CALCULATOR_NATIVE_API const char* calc_graph_get_scene(CalcGraphSession* handle, int width, int height);
CALCULATOR_NATIVE_API int calc_graph_render_png(CalcGraphSession* handle, int width, int height, const unsigned char** bytes, size_t* length);
CALCULATOR_NATIVE_API const char* calc_graph_trace(CalcGraphSession* handle, double xNormalized, double yNormalized, double precision);
// Same JSON as trace, restricted to the selected visible row. Hidden or absent curves return null.
CALCULATOR_NATIVE_API const char* calc_graph_trace_selected(CalcGraphSession* handle, double xNormalized, double yNormalized, double precision);
CALCULATOR_NATIVE_API const char* calc_graph_analyze(CalcGraphSession* handle, int row);

#ifdef __cplusplus
}
#endif

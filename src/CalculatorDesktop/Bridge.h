// Licensed under the MIT License.
#pragma once

#ifndef CALCULATOR_NATIVE_API
#if defined(_WIN32) && !defined(CALCULATOR_NATIVE_STATIC)
#if defined(CALCULATOR_NATIVE_BUILD)
#define CALCULATOR_NATIVE_API __declspec(dllexport)
#else
#define CALCULATOR_NATIVE_API __declspec(dllimport)
#endif
#elif defined(__GNUC__)
#define CALCULATOR_NATIVE_API __attribute__((visibility("default")))
#else
#define CALCULATOR_NATIVE_API
#endif
#endif

#ifdef __cplusplus
extern "C" {
#endif

typedef struct CalcSession* CalcHandle;

// Strings are UTF-8 and NUL-terminated. Null creation settings use defaults:
// en-US locale, US region, period decimal, and no grouping separator.
// Empty locale/region/decimal also select defaults. Inputs are bounded to 65,536 bytes.
CALCULATOR_NATIVE_API CalcHandle calc_create(const char* locale, const char* region,
    const char* decimal, const char* group_separator);
CALCULATOR_NATIVE_API int calc_destroy(CalcHandle handle);

// Borrowed JSON/error strings remain valid until the next call for this session.
// Copy them before another call. Clients must synchronize calls for each session.
// A null handle retrieves the calling thread's creation or invalid-handle error.
CALCULATOR_NATIVE_API const char* calc_get_state(CalcHandle handle);
CALCULATOR_NATIVE_API const char* calc_last_error(CalcHandle handle);
CALCULATOR_NATIVE_API const char* calc_get_about(CalcHandle handle);

// Modes: 0 Standard, 1 Scientific, 2 Programmer, 3 Converter, 4 Date.
// Commands must be exposed by the selected mode's shared button/menu helpers.
CALCULATOR_NATIVE_API int calc_set_mode(CalcHandle handle, int mode);
CALCULATOR_NATIVE_API int calc_send_command(CalcHandle handle, int command);
CALCULATOR_NATIVE_API int calc_press_key(CalcHandle handle, const char* key);
CALCULATOR_NATIVE_API int calc_paste_number(CalcHandle handle, const char* number);
CALCULATOR_NATIVE_API int calc_set_category(CalcHandle handle, int category);
CALCULATOR_NATIVE_API int calc_set_units(CalcHandle handle, int from, int to);

// Single-date ECB reference rates: strict YYYY-MM-DD date, YYYY-MM-DDTHH:mm:ssZ fetch time,
// and newline-separated CODE=positive-invariant-number rates. EUR=1 is required.
// from_cache is 0 or 1. Invalid batches preserve the previous data and selections.
CALCULATOR_NATIVE_API int calc_currency_set_rates(CalcHandle handle, const char* reference_date,
    const char* fetched_utc, const char* rates, int from_cache);
// Provider metadata: 0 ECB, 1 Frankfurter. Prefer the atomic rates operation below
// when publishing data with mixed observation dates. Limits: 256 codes, 65,536 rate bytes.
CALCULATOR_NATIVE_API int calc_currency_set_provider(CalcHandle handle, int provider);
CALCULATOR_NATIVE_API int calc_currency_set_rates_ex(CalcHandle handle, int provider,
    const char* reference_date, const char* reference_end_date, const char* fetched_utc,
    const char* rates, int from_cache);
// Status: 0 unavailable, 1 loading, 4 refreshFailed. Valid rates remain usable
// during loading/failure; injection sets ready(2) or cached(3).
CALCULATOR_NATIVE_API int calc_currency_set_status(CalcHandle handle, int status, const char* message);

// Memory: 0 recall, 1 add, 2 subtract, 3 remove. History: 0 recall, 1 remove,
// 2 clear (index ignored). These operations require modes 0 through 2.
CALCULATOR_NATIVE_API int calc_memory_action(CalcHandle handle, int operation, int index);
CALCULATOR_NATIVE_API int calc_history_action(CalcHandle handle, int operation, int index);

// Gregorian civil dates, inclusive range 1601-01-01 through 9999-12-31.
// Duration fields must be nonnegative. subtract must be 0 or 1.
CALCULATOR_NATIVE_API const char* calc_date_difference(CalcHandle handle,
    int year1, int month1, int day1, int year2, int month2, int day2);
CALCULATOR_NATIVE_API const char* calc_date_shift(CalcHandle handle,
    int year, int month, int day, int years, int months, int days, int subtract);

#ifdef __cplusplus
}
#endif

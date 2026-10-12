// Licensed under the MIT License.
#pragma once

#include <optional>

namespace Desktop
{
    // Gregorian civil dates, inclusive range 1601-01-01 through 9999-12-31.
    // Date-only arithmetic has no timezone. Other Windows calendar systems are unsupported.
    struct CivilDate
    {
        int year;
        unsigned month;
        unsigned day;
    };

    struct DateDuration
    {
        unsigned years;
        unsigned months;
        unsigned days;
    };

    struct DateDifference
    {
        unsigned years;
        unsigned months;
        unsigned weeks;
        unsigned days;
        int total_days;
    };

    bool operator==(CivilDate left, CivilDate right);
    bool operator!=(CivilDate left, CivilDate right);
    bool operator==(DateDuration left, DateDuration right);
    bool operator!=(DateDuration left, DateDuration right);
    bool operator==(DateDifference left, DateDifference right);
    bool operator!=(DateDifference left, DateDifference right);

    bool IsValidDate(CivilDate date);

    // Matches Common/DateCalculator.cs: addition uses years, months, then days;
    // subtraction uses days, months, then years. Year/month shifts clamp month ends.
    std::optional<CivilDate> AddDateDuration(CivilDate start, DateDuration duration);
    std::optional<CivilDate> SubtractDateDuration(CivilDate start, DateDuration duration);

    // Dates are sorted; each larger unit is maximized before the next unit.
    // Leap-day clamping can yield 12 months after the full-year portion.
    std::optional<DateDifference> GetDateDifference(CivilDate first, CivilDate second);
}

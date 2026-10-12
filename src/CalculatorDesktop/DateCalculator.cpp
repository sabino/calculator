// Licensed under the MIT License.
#include "DateCalculator.h"
#include <algorithm>
#include <cstdint>

namespace Desktop
{
    namespace
    {
        constexpr int MIN_YEAR = 1601;
        constexpr int MAX_YEAR = 9999;

        bool IsLeapYear(int year)
        {
            return year % 4 == 0 && (year % 100 != 0 || year % 400 == 0);
        }

        unsigned GetDaysInMonth(int year, unsigned month)
        {
            constexpr unsigned lengths[] = { 31, 28, 31, 30, 31, 30, 31, 31, 30, 31, 30, 31 };
            return month == 2 && IsLeapYear(year) ? 29 : lengths[month - 1];
        }

        int GetDaysBeforeYear(int year)
        {
            const int previous = year - 1;
            return previous * 365 + previous / 4 - previous / 100 + previous / 400;
        }

        int GetDayNumber(CivilDate date)
        {
            constexpr int preceding[] = { 0, 31, 59, 90, 120, 151, 181, 212, 243, 273, 304, 334 };
            return GetDaysBeforeYear(date.year) + preceding[date.month - 1] + (date.month > 2 && IsLeapYear(date.year) ? 1 : 0) + static_cast<int>(date.day)
                   - 1;
        }

        CivilDate GetDateFromDayNumber(int dayNumber)
        {
            int lower = MIN_YEAR;
            int upper = MAX_YEAR + 1;
            while (upper - lower > 1)
            {
                const int middle = lower + (upper - lower) / 2;
                if (GetDaysBeforeYear(middle) <= dayNumber)
                {
                    lower = middle;
                }
                else
                {
                    upper = middle;
                }
            }

            unsigned day = static_cast<unsigned>(dayNumber - GetDaysBeforeYear(lower)) + 1;
            unsigned month = 1;
            while (day > GetDaysInMonth(lower, month))
            {
                day -= GetDaysInMonth(lower, month);
                ++month;
            }
            return { lower, month, day };
        }

        std::optional<CivilDate> ShiftYears(CivilDate date, std::int64_t years)
        {
            const auto year = date.year + years;
            if (year < MIN_YEAR || year > MAX_YEAR)
            {
                return std::nullopt;
            }
            const int resultYear = static_cast<int>(year);
            return CivilDate{ resultYear, date.month, std::min(date.day, GetDaysInMonth(resultYear, date.month)) };
        }

        std::optional<CivilDate> ShiftMonths(CivilDate date, std::int64_t months)
        {
            const auto monthNumber = static_cast<std::int64_t>(date.year) * 12 + date.month - 1 + months;
            if (monthNumber < MIN_YEAR * 12 || monthNumber >= (MAX_YEAR + 1) * 12)
            {
                return std::nullopt;
            }
            const int year = static_cast<int>(monthNumber / 12);
            const unsigned month = static_cast<unsigned>(monthNumber % 12) + 1;
            return CivilDate{ year, month, std::min(date.day, GetDaysInMonth(year, month)) };
        }

        std::optional<CivilDate> ShiftDays(CivilDate date, std::int64_t days)
        {
            const auto dayNumber = GetDayNumber(date) + days;
            if (dayNumber < GetDaysBeforeYear(MIN_YEAR) || dayNumber >= GetDaysBeforeYear(MAX_YEAR + 1))
            {
                return std::nullopt;
            }
            return GetDateFromDayNumber(static_cast<int>(dayNumber));
        }
    }

    bool operator==(CivilDate left, CivilDate right)
    {
        return left.year == right.year && left.month == right.month && left.day == right.day;
    }

    bool operator!=(CivilDate left, CivilDate right)
    {
        return !(left == right);
    }

    bool operator==(DateDuration left, DateDuration right)
    {
        return left.years == right.years && left.months == right.months && left.days == right.days;
    }

    bool operator!=(DateDuration left, DateDuration right)
    {
        return !(left == right);
    }

    bool operator==(DateDifference left, DateDifference right)
    {
        return left.years == right.years && left.months == right.months && left.weeks == right.weeks && left.days == right.days
               && left.total_days == right.total_days;
    }

    bool operator!=(DateDifference left, DateDifference right)
    {
        return !(left == right);
    }

    bool IsValidDate(CivilDate date)
    {
        return date.year >= MIN_YEAR && date.year <= MAX_YEAR && date.month >= 1 && date.month <= 12 && date.day >= 1
               && date.day <= GetDaysInMonth(date.year, date.month);
    }

    std::optional<CivilDate> AddDateDuration(CivilDate start, DateDuration duration)
    {
        if (!IsValidDate(start))
        {
            return std::nullopt;
        }
        const auto afterYears = ShiftYears(start, duration.years);
        if (!afterYears)
        {
            return std::nullopt;
        }
        const auto afterMonths = ShiftMonths(*afterYears, duration.months);
        if (!afterMonths)
        {
            return std::nullopt;
        }
        return ShiftDays(*afterMonths, duration.days);
    }

    std::optional<CivilDate> SubtractDateDuration(CivilDate start, DateDuration duration)
    {
        if (!IsValidDate(start))
        {
            return std::nullopt;
        }
        const auto afterDays = ShiftDays(start, -static_cast<std::int64_t>(duration.days));
        if (!afterDays)
        {
            return std::nullopt;
        }
        const auto afterMonths = ShiftMonths(*afterDays, -static_cast<std::int64_t>(duration.months));
        if (!afterMonths)
        {
            return std::nullopt;
        }
        return ShiftYears(*afterMonths, -static_cast<std::int64_t>(duration.years));
    }

    std::optional<DateDifference> GetDateDifference(CivilDate first, CivilDate second)
    {
        if (!IsValidDate(first) || !IsValidDate(second))
        {
            return std::nullopt;
        }
        if (GetDayNumber(first) > GetDayNumber(second))
        {
            std::swap(first, second);
        }
        const int endDay = GetDayNumber(second);
        const int totalDays = endDay - GetDayNumber(first);

        unsigned years = static_cast<unsigned>(second.year - first.year);
        CivilDate pivot = *ShiftYears(first, years);
        if (GetDayNumber(pivot) > endDay)
        {
            pivot = *ShiftYears(first, --years);
        }

        unsigned months = static_cast<unsigned>((second.year - pivot.year) * 12 + static_cast<int>(second.month) - static_cast<int>(pivot.month));
        CivilDate monthPivot = *ShiftMonths(pivot, months);
        if (GetDayNumber(monthPivot) > endDay)
        {
            monthPivot = *ShiftMonths(pivot, --months);
        }

        const unsigned remainingDays = static_cast<unsigned>(endDay - GetDayNumber(monthPivot));
        return DateDifference{ years, months, remainingDays / 7, remainingDays % 7, totalDays };
    }
}

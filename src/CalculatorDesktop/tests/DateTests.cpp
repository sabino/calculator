// Licensed under the MIT License.
#include "DateCalculator.h"
#include <initializer_list>
#include <iostream>
#include <limits>
#include <stdexcept>

using Desktop::CivilDate;
using Desktop::DateDifference;
using Desktop::DateDuration;

namespace
{
    void Require(bool condition, const char* message)
    {
        if (!condition)
        {
            throw std::runtime_error(message);
        }
    }

    void CheckWindowsDurationCases()
    {
        // Gregorian fixtures from Calculator.Tests/DateCalculatorTests.cs.
        Require(Desktop::AddDateDuration({ 2008, 1, 31 }, { 0, 1, 0 }) == CivilDate{ 2008, 2, 29 }, "Adding a month must clamp January 31 to leap February");
        Require(Desktop::AddDateDuration({ 2008, 3, 31 }, { 0, 1, 10 }) == CivilDate{ 2008, 5, 10 }, "Addition must shift months before days");
        Require(Desktop::AddDateDuration({ 2008, 1, 31 }, { 0, 1, 10 }) == CivilDate{ 2008, 3, 10 }, "Addition must count days from the clamped month end");
        Require(
            Desktop::SubtractDateDuration({ 2008, 3, 31 }, { 0, 1, 0 }) == CivilDate{ 2008, 2, 29 },
            "Subtracting a month must clamp March 31 to leap February");
        Require(Desktop::SubtractDateDuration({ 2008, 3, 10 }, { 0, 1, 10 }) == CivilDate{ 2008, 1, 29 }, "Subtraction must shift days before months");
        Require(Desktop::SubtractDateDuration({ 2007, 3, 10 }, { 0, 1, 10 }) == CivilDate{ 2007, 1, 28 }, "Subtraction must respect common February");
    }

    void CheckCalendarOrderAndLeapYears()
    {
        Require(Desktop::AddDateDuration({ 2020, 2, 29 }, { 1, 1, 0 }) == CivilDate{ 2021, 3, 28 }, "Addition must clamp years before adding months");
        Require(
            Desktop::SubtractDateDuration({ 2021, 3, 31 }, { 1, 1, 0 }) == CivilDate{ 2020, 2, 28 }, "Subtraction must clamp months before subtracting years");
        Require(Desktop::AddDateDuration({ 1900, 2, 28 }, { 0, 0, 1 }) == CivilDate{ 1900, 3, 1 }, "Century years outside the 400-year cycle are common years");
        Require(Desktop::AddDateDuration({ 2000, 2, 28 }, { 0, 0, 1 }) == CivilDate{ 2000, 2, 29 }, "Years divisible by 400 are leap years");
        Require(Desktop::AddDateDuration({ 2024, 12, 31 }, { 0, 1, 0 }) == CivilDate{ 2025, 1, 31 }, "Adding months must cross a year boundary");
        Require(Desktop::SubtractDateDuration({ 2025, 1, 31 }, { 0, 1, 0 }) == CivilDate{ 2024, 12, 31 }, "Subtracting months must cross a year boundary");
    }

    void CheckDifferences()
    {
        Require(Desktop::GetDateDifference({ 2008, 3, 10 }, { 2008, 3, 10 }) == DateDifference{ 0, 0, 0, 0, 0 }, "Equal dates must have a zero difference");
        Require(
            Desktop::GetDateDifference({ 2008, 2, 29 }, { 2008, 3, 31 }) == DateDifference{ 0, 1, 0, 2, 31 },
            "Leap month difference must match the Windows fixture");
        Require(
            Desktop::GetDateDifference({ 2007, 2, 28 }, { 2008, 1, 29 }) == DateDifference{ 0, 11, 0, 1, 335 },
            "Difference must maximize calendar months before days");
        const auto forward = Desktop::GetDateDifference({ 2007, 5, 10 }, { 2008, 3, 10 });
        Require(forward == DateDifference{ 0, 10, 0, 0, 305 }, "Ten calendar months must retain the Windows total-day result");
        Require(forward == Desktop::GetDateDifference({ 2008, 3, 10 }, { 2007, 5, 10 }), "Reversed dates must return the same positive difference");
        Require(
            Desktop::GetDateDifference({ 2019, 3, 10 }, { 2019, 3, 11 }) == DateDifference{ 0, 0, 0, 1, 1 },
            "Spring daylight-saving boundary must remain one civil day");
        Require(
            Desktop::GetDateDifference({ 2019, 11, 3 }, { 2019, 11, 4 }) == DateDifference{ 0, 0, 0, 1, 1 },
            "Autumn daylight-saving boundary must remain one civil day");
        Require(
            Desktop::GetDateDifference({ 2019, 3, 10 }, { 2019, 3, 17 }) == DateDifference{ 0, 0, 1, 0, 7 },
            "Seven remaining days must decompose into one week");
        Require(
            Desktop::GetDateDifference({ 2020, 2, 29 }, { 2024, 2, 28 }) == DateDifference{ 3, 12, 0, 0, 1460 },
            "Full units must be computed from each anchor with leap-day clamping");
        Require(
            Desktop::GetDateDifference({ 2021, 1, 31 }, { 2021, 3, 30 }) == DateDifference{ 0, 1, 4, 2, 58 },
            "Month differences must avoid repeated month-end clamping");
    }

    void CheckBounds()
    {
        constexpr CivilDate minimum{ 1601, 1, 1 };
        constexpr CivilDate maximum{ 9999, 12, 31 };
        constexpr DateDuration zero{ 0, 0, 0 };
        Require(Desktop::IsValidDate(minimum) && Desktop::IsValidDate(maximum), "Both supported range endpoints must be valid");
        Require(
            Desktop::AddDateDuration(maximum, zero) == maximum && Desktop::SubtractDateDuration(minimum, zero) == minimum,
            "Zero duration must preserve range endpoints");
        Require(
            Desktop::GetDateDifference(minimum, maximum) == DateDifference{ 8398, 11, 4, 2, 3067670 },
            "The full supported range must include weeks and total days");
        Require(
            Desktop::GetDateDifference({ 2008, 1, 31 }, maximum) == DateDifference{ 7991, 11, 0, 0, 2918987 },
            "Maximum-ending range must match the Windows regression fixture");
        Require(
            Desktop::GetDateDifference({ 9998, 12, 31 }, maximum) == DateDifference{ 1, 0, 0, 0, 365 },
            "Year difference at the upper bound must not probe beyond the range");
        Require(
            !Desktop::AddDateDuration(maximum, { 0, 0, 1 }) && !Desktop::AddDateDuration(maximum, { 0, 1, 0 })
                && !Desktop::AddDateDuration(maximum, { 1, 0, 0 }),
            "Each addition unit must reject upper-bound overflow");
        Require(
            !Desktop::SubtractDateDuration(minimum, { 0, 0, 1 }) && !Desktop::SubtractDateDuration(minimum, { 0, 1, 0 })
                && !Desktop::SubtractDateDuration(minimum, { 1, 0, 0 }),
            "Each subtraction unit must reject lower-bound overflow");
        const unsigned huge = std::numeric_limits<unsigned>::max();
        for (const DateDuration duration : { DateDuration{ huge, 0, 0 }, DateDuration{ 0, huge, 0 }, DateDuration{ 0, 0, huge } })
        {
            Require(
                !Desktop::AddDateDuration(minimum, duration) && !Desktop::SubtractDateDuration(maximum, duration),
                "Large unsigned durations must reject overflow without wrapping");
        }
        for (const CivilDate invalid : { CivilDate{ 1600, 12, 31 },
                                         CivilDate{ 10000, 1, 1 },
                                         CivilDate{ 2024, 0, 1 },
                                         CivilDate{ 2024, 13, 1 },
                                         CivilDate{ 2024, 1, 0 },
                                         CivilDate{ 2024, 4, 31 },
                                         CivilDate{ 1900, 2, 29 } })
        {
            Require(!Desktop::IsValidDate(invalid), "Invalid civil dates must be rejected");
            Require(
                !Desktop::AddDateDuration(invalid, zero) && !Desktop::SubtractDateDuration(invalid, zero),
                "Duration arithmetic must reject invalid inputs before calendar arithmetic");
            Require(
                !Desktop::GetDateDifference(invalid, minimum) && !Desktop::GetDateDifference(maximum, invalid),
                "Difference must reject either invalid endpoint");
        }
    }
}

int main()
{
    try
    {
        CheckWindowsDurationCases();
        CheckCalendarOrderAndLeapYears();
        CheckDifferences();
        CheckBounds();
        std::cout << "Date calculator tests passed\n";
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << error.what() << '\n';
        return 1;
    }
}

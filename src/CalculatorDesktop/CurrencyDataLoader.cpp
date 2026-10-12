// Licensed under the MIT License.
#include "CurrencyDataLoader.h"
#include "DateCalculator.h"
#include <algorithm>
#include <cmath>
#include <locale>
#include <map>
#include <sstream>
#include <stdexcept>

namespace Desktop
{
    namespace
    {
        int ReadDigits(std::string_view text)
        {
            int value = 0;
            for (const char character : text)
            {
                if (character < '0' || character > '9')
                    throw std::invalid_argument("Currency dates must use ISO digits");
                value = value * 10 + character - '0';
            }
            return value;
        }

        void ValidateDate(std::string_view text)
        {
            if (text.size() != 10 || text[4] != '-' || text[7] != '-' ||
                !IsValidDate({ReadDigits(text.substr(0, 4)), static_cast<unsigned>(ReadDigits(text.substr(5, 2))),
                              static_cast<unsigned>(ReadDigits(text.substr(8, 2)))}))
                throw std::invalid_argument("Currency reference date must be YYYY-MM-DD in the supported calendar");
        }

        double ParseRate(std::string_view text)
        {
            if (text.empty() || text.size() > 32 || text.front() < '0' || text.front() > '9')
                throw std::invalid_argument("Currency rates must be positive invariant numbers");
            for (const char character : text)
                if ((character < '0' || character > '9') && character != '.' && character != 'e' && character != 'E' &&
                    character != '+' && character != '-')
                    throw std::invalid_argument("Currency rates must be positive invariant numbers");
            std::istringstream input{std::string(text)};
            input.imbue(std::locale::classic());
            double value = 0;
            input >> value;
            // Bound external rates so ordinary converter input cannot overflow a cross-rate calculation.
            if (!input || input.peek() != std::char_traits<char>::eof() || !std::isfinite(value) || value < 1e-12 || value > 1e12)
                throw std::invalid_argument("Currency rates must be finite and between 1e-12 and 1e12");
            return value;
        }
    }

    void CurrencyDataLoader::SetRates(std::string_view referenceDate, std::string_view fetchedAt, std::string_view lines, bool fromCache,
                                      int provider, std::string_view referenceEndDate)
    {
        if (provider != 0 && provider != 1)
            throw std::invalid_argument("Currency provider must be ECB or Frankfurter");
        if (referenceEndDate.empty())
            referenceEndDate = referenceDate;
        ValidateDate(referenceDate);
        ValidateDate(referenceEndDate);
        if (referenceEndDate < referenceDate)
            throw std::invalid_argument("Currency reference date range must be ordered");
        if (provider == 0 && referenceEndDate != referenceDate)
            throw std::invalid_argument("ECB rates must have one common reference date");
        if (fetchedAt.size() != 20 || fetchedAt[10] != 'T' || fetchedAt[13] != ':' || fetchedAt[16] != ':' || fetchedAt[19] != 'Z')
            throw std::invalid_argument("Currency fetch time must be YYYY-MM-DDTHH:mm:ssZ");
        ValidateDate(fetchedAt.substr(0, 10));
        if (fetchedAt.substr(0, 10) < referenceEndDate || ReadDigits(fetchedAt.substr(11, 2)) > 23 ||
            ReadDigits(fetchedAt.substr(14, 2)) > 59 || ReadDigits(fetchedAt.substr(17, 2)) > 59)
            throw std::invalid_argument("Currency fetch time must follow the reference date and contain a valid UTC time");
        if (lines.empty() || lines.size() > 65536)
            throw std::invalid_argument("Currency rates must contain bounded dated data");

        std::map<std::string, double> rates;
        std::istringstream input{std::string(lines)};
        std::string line;
        while (std::getline(input, line))
        {
            if (!line.empty() && line.back() == '\r')
                line.pop_back();
            if (line.size() < 5 || line[3] != '=')
                throw std::invalid_argument("Currency rates must use CODE=rate lines");
            const auto code = line.substr(0, 3);
            if (!std::all_of(code.begin(), code.end(), [](char character) { return character >= 'A' && character <= 'Z'; }))
                throw std::invalid_argument("Currency codes must contain three uppercase ASCII letters");
            if (!rates.emplace(code, ParseRate(std::string_view(line).substr(4))).second)
                throw std::invalid_argument("Currency codes must be unique");
            if (rates.size() > 256)
                throw std::invalid_argument("Currency rates exceed the 256-currency limit");
        }
        if (rates.size() < 2 || rates.find("EUR") == rates.end() || rates.at("EUR") != 1)
            throw std::invalid_argument("Currency rates must include EUR=1 and at least one quoted currency");

        using namespace UnitConversionManager;
        std::vector<Unit> units;
        UnitToUnitToConversionDataMap ratios;
        for (const auto& rate : rates)
        {
            const auto& code = rate.first;
            // IDs remain stable across refreshes and do not overlap static units 0 through 168.
            const int id = 169 + (code[0] - 'A') * 676 + (code[1] - 'A') * 26 + code[2] - 'A';
            const std::wstring label(code.begin(), code.end());
            units.emplace_back(id, label, label, code == "USD", code == "EUR", false);
        }
        for (const auto& from : units)
            for (const auto& to : units)
            {
                const std::string fromCode(from.abbreviation.begin(), from.abbreviation.end());
                const std::string toCode(to.abbreviation.begin(), to.abbreviation.end());
                const double ratio = rates.at(toCode) / rates.at(fromCode);
                if (!std::isfinite(ratio) || ratio <= 0)
                    throw std::invalid_argument("Currency cross rates exceed the supported numeric range");
                ratios[from][to] = ConversionData{ratio, 0, false};
            }
        CurrencyState state{true, std::string(referenceDate), std::string(fetchedAt), fromCache ? "cached" : "ready", "",
                            provider, std::string(referenceEndDate)};
        m_units.swap(units);
        m_ratios.swap(ratios);
        m_state = std::move(state);
    }

    void CurrencyDataLoader::SetProvider(int provider)
    {
        if (provider != 0 && provider != 1)
            throw std::invalid_argument("Currency provider must be ECB or Frankfurter");
        m_state.provider_id = provider;
    }

    void CurrencyDataLoader::SetStatus(int status, std::string message)
    {
        if (status != 0 && status != 1 && status != 4)
            throw std::invalid_argument("Currency status must be unavailable, loading, or refresh failed");
        if (status == 0 && m_state.available)
            throw std::invalid_argument("Valid cached currency data must be retained after refresh failure");
        if (message.size() > 1024)
            throw std::invalid_argument("Currency status message exceeds the size limit");
        std::string name = status == 1 ? "loading" : status == 4 ? "refreshFailed" : "unavailable";
        m_state.status = std::move(name);
        m_state.error = std::move(message);
    }

    const CurrencyState& CurrencyDataLoader::GetState() const { return m_state; }
    void CurrencyDataLoader::LoadData() {}
    std::vector<UnitConversionManager::Category> CurrencyDataLoader::GetOrderedCategories() { return {}; }
    std::vector<UnitConversionManager::Unit> CurrencyDataLoader::GetOrderedUnits(const UnitConversionManager::Category& category)
    {
        return SupportsCategory(category) ? m_units : std::vector<UnitConversionManager::Unit>{};
    }
    std::unordered_map<UnitConversionManager::Unit, UnitConversionManager::ConversionData, UnitConversionManager::UnitHash>
    CurrencyDataLoader::LoadOrderedRatios(const UnitConversionManager::Unit& unit)
    {
        const auto found = m_ratios.find(unit);
        if (found == m_ratios.end())
            return {};
        return found->second;
    }
    bool CurrencyDataLoader::SupportsCategory(const UnitConversionManager::Category& category) { return category.id == CURRENCY_CATEGORY_ID; }
}

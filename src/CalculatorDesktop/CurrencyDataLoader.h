// Licensed under the MIT License.
#pragma once

#include "UnitConverter.h"
#include <string_view>

namespace Desktop
{
    inline constexpr int CURRENCY_CATEGORY_ID = 16;

    struct CurrencyState
    {
        bool available = false;
        std::string reference_date;
        std::string fetched_at;
        std::string status = "unavailable";
        std::string error;
        int provider_id = 0;
        std::string reference_end_date;
    };

    // Network/cache effects belong to the shared frontend. This loader publishes validated rates.
    class CurrencyDataLoader final : public UnitConversionManager::IConverterDataLoader
    {
      public:
        void SetRates(std::string_view referenceDate, std::string_view fetchedAt, std::string_view lines, bool fromCache,
                      int provider = 0, std::string_view referenceEndDate = {});
        void SetProvider(int provider);
        void SetStatus(int status, std::string message);
        const CurrencyState& GetState() const;
        void LoadData() override;
        std::vector<UnitConversionManager::Category> GetOrderedCategories() override;
        std::vector<UnitConversionManager::Unit> GetOrderedUnits(const UnitConversionManager::Category& category) override;
        std::unordered_map<UnitConversionManager::Unit, UnitConversionManager::ConversionData, UnitConversionManager::UnitHash>
        LoadOrderedRatios(const UnitConversionManager::Unit& unit) override;
        bool SupportsCategory(const UnitConversionManager::Category& category) override;

      private:
        CurrencyState m_state;
        std::vector<UnitConversionManager::Unit> m_units;
        UnitConversionManager::UnitToUnitToConversionDataMap m_ratios;
    };
}

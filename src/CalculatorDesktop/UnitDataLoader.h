// Licensed under the MIT License.
#pragma once

#include "ResourceProvider.h"
#include "UnitConverter.h"

namespace Desktop
{
    class UnitDataLoader final : public UnitConversionManager::IConverterDataLoader
    {
    public:
        explicit UnitDataLoader(const ResourceProvider& resources, const std::string& region = "US");
        void LoadData() override;
        std::vector<UnitConversionManager::Category> GetOrderedCategories() override;
        std::vector<UnitConversionManager::Unit> GetOrderedUnits(const UnitConversionManager::Category& category) override;
        std::unordered_map<UnitConversionManager::Unit, UnitConversionManager::ConversionData, UnitConversionManager::UnitHash>
        LoadOrderedRatios(const UnitConversionManager::Unit& unit) override;
        bool SupportsCategory(const UnitConversionManager::Category& category) override;

    private:
        std::vector<UnitConversionManager::Category> m_categories;
        UnitConversionManager::CategoryToUnitVectorMap m_units;
        UnitConversionManager::UnitToUnitToConversionDataMap m_ratios;
    };
}

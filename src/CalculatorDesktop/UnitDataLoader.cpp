// Licensed under the MIT License.
#include "UnitDataLoader.h"
#include "CurrencyDataLoader.h"
#include "DesktopResources.h"
#include <algorithm>

namespace Desktop
{
    namespace
    {
        bool MatchesRegion(int rule, const std::string& region)
        {
            const bool usCustomaryAndFahrenheit = region == "US" || region == "FM" || region == "MH" || region == "PW";
            const bool usCustomary = usCustomaryAndFahrenheit || region == "LR";
            const bool fahrenheit = usCustomaryAndFahrenheit || region == "BS" || region == "KY" || region == "LR";
            switch (rule)
            {
            case 0:
                return false;
            case 1:
                return true;
            case 2:
                return !usCustomary;
            case 3:
                return usCustomary;
            case 4:
                return fahrenheit;
            case 5:
                return !fahrenheit;
            case 6:
                return region == "GB";
            case 7:
                return region != "GB";
            case 8:
                return usCustomary && region != "GB";
            case 9:
                return usCustomary && region == "GB";
            default:
                return false;
            }
        }
    }

    UnitDataLoader::UnitDataLoader(const ResourceProvider& resources, const std::string& region)
    {
        using namespace UnitConversionManager;
        for (const auto& category : Generated::CATEGORY_DEFINITIONS)
        {
            m_categories.emplace_back(category.id, resources.GetAppString(category.name_key), category.supports_negative);
            std::vector<const Generated::UnitDefinition*> definitions;
            for (const auto& definition : Generated::UNIT_DEFINITIONS)
            {
                if (definition.category_id == category.id)
                {
                    definitions.push_back(&definition);
                }
            }
            std::sort(definitions.begin(), definitions.end(), [](const auto* left, const auto* right) { return left->order < right->order; });
            auto& units = m_units[category.id];
            for (const auto* definition : definitions)
            {
                units.emplace_back(
                    definition->id,
                    resources.GetAppString(definition->name_key),
                    resources.GetAppString(definition->abbreviation_key),
                    MatchesRegion(definition->source_rule, region),
                    MatchesRegion(definition->target_rule, region),
                    definition->whimsical);
            }
            for (size_t from = 0; from < definitions.size(); ++from)
            {
                for (size_t to = 0; to < definitions.size(); ++to)
                {
                    ConversionData data{ definitions[from]->factor / definitions[to]->factor, 0, false };
                    for (const auto& temperature : Generated::TEMPERATURE_DEFINITIONS)
                    {
                        if (temperature.from_id == units[from].id && temperature.to_id == units[to].id)
                        {
                            data = { temperature.ratio, temperature.offset, temperature.offset_first };
                            break;
                        }
                    }
                    m_ratios[units[from]][units[to]] = data;
                }
            }
        }
        // Append Currency so the twelve established converter indices stay stable.
        m_categories.emplace_back(CURRENCY_CATEGORY_ID, resources.GetAppString(L"CategoryName_CurrencyText"), false);
        m_units[CURRENCY_CATEGORY_ID] = {};
    }

    void UnitDataLoader::LoadData()
    {
    }

    std::vector<UnitConversionManager::Category> UnitDataLoader::GetOrderedCategories()
    {
        return m_categories;
    }

    std::vector<UnitConversionManager::Unit> UnitDataLoader::GetOrderedUnits(const UnitConversionManager::Category& category)
    {
        return m_units.at(category.id);
    }

    std::unordered_map<UnitConversionManager::Unit, UnitConversionManager::ConversionData, UnitConversionManager::UnitHash>
    UnitDataLoader::LoadOrderedRatios(const UnitConversionManager::Unit& unit)
    {
        return m_ratios.at(unit);
    }

    bool UnitDataLoader::SupportsCategory(const UnitConversionManager::Category& category)
    {
        return m_units.find(category.id) != m_units.end();
    }
}

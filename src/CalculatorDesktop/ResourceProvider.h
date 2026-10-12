// Licensed under the MIT License.
#pragma once

#include "CalculatorResource.h"
#include <string>
#include <unordered_map>

namespace Desktop
{
    struct NumberFormat
    {
        std::wstring decimal = L".";
        std::wstring thousands = L",";
        std::wstring grouping = L"3;0";
    };

    class ResourceProvider final : public CalculationManager::IResourceProvider
    {
    public:
        explicit ResourceProvider(std::string locale = "en-US", NumberFormat format = {});
        std::wstring GetCEngineString(std::wstring_view id) override;
        std::wstring GetAppString(std::wstring_view id) const;
        const NumberFormat& GetNumberFormat() const;

    private:
        void LoadLocale(std::string_view locale);
        NumberFormat m_format;
        std::unordered_map<std::wstring, std::wstring> m_engineStrings;
        std::unordered_map<std::wstring, std::wstring> m_appStrings;
    };
}

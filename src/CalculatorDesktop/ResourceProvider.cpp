// Licensed under the MIT License.
#include "ResourceProvider.h"
#include "DesktopResources.h"
#include <algorithm>
#include <stdexcept>

namespace Desktop
{
    ResourceProvider::ResourceProvider(std::string locale, NumberFormat format)
        : m_format(std::move(format))
    {
        if (m_format.decimal.size() != 1 || m_format.thousands.size() > 1)
        {
            throw std::invalid_argument("The calculator needs single-character number separators");
        }
        std::replace(locale.begin(), locale.end(), '_', '-');
        LoadLocale("en-US");
        LoadLocale(locale);
    }

    void ResourceProvider::LoadLocale(std::string_view locale)
    {
        for (const auto& resource : Generated::RESOURCE_STRINGS)
        {
            if (resource.locale == locale)
            {
                auto& strings = resource.is_engine ? m_engineStrings : m_appStrings;
                strings[std::wstring(resource.key)] = resource.value;
            }
        }
    }

    std::wstring ResourceProvider::GetCEngineString(std::wstring_view id)
    {
        if (id == L"sDecimal" || id == L"4")
        {
            return m_format.decimal;
        }
        if (id == L"sThousand")
        {
            return m_format.thousands;
        }
        if (id == L"sGrouping")
        {
            return m_format.grouping;
        }
        const auto found = m_engineStrings.find(std::wstring(id));
        return found == m_engineStrings.end() ? L"" : found->second;
    }

    std::wstring ResourceProvider::GetAppString(std::wstring_view id) const
    {
        const auto found = m_appStrings.find(std::wstring(id));
        return found == m_appStrings.end() ? std::wstring(id) : found->second;
    }

    const NumberFormat& ResourceProvider::GetNumberFormat() const
    {
        return m_format;
    }
}

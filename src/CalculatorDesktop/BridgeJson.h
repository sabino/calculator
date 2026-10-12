// Licensed under the MIT License.
#pragma once

#include <cstdint>
#include <stdexcept>
#include <string>
#include <string_view>

namespace Desktop::BridgeJson
{
    // Strict Unicode conversion; malformed UTF-8 or unpaired surrogates throw.
    inline std::wstring DecodeUtf8(std::string_view value)
    {
        std::wstring result;
        result.reserve(value.size());
        for (std::size_t index = 0; index < value.size();)
        {
            const auto first = static_cast<unsigned char>(value[index++]);
            std::uint32_t scalar = 0;
            unsigned continuation = 0;
            std::uint32_t minimum = 0;
            if (first <= 0x7f)
            {
                scalar = first;
            }
            else if (first >= 0xc2 && first <= 0xdf)
            {
                scalar = first & 0x1f;
                continuation = 1;
                minimum = 0x80;
            }
            else if (first >= 0xe0 && first <= 0xef)
            {
                scalar = first & 0x0f;
                continuation = 2;
                minimum = 0x800;
            }
            else if (first >= 0xf0 && first <= 0xf4)
            {
                scalar = first & 0x07;
                continuation = 3;
                minimum = 0x10000;
            }
            else
            {
                throw std::invalid_argument("Invalid UTF-8 leading byte");
            }
            if (continuation > value.size() - index)
            {
                throw std::invalid_argument("Truncated UTF-8 scalar");
            }
            for (unsigned count = 0; count < continuation; ++count)
            {
                const auto next = static_cast<unsigned char>(value[index++]);
                if ((next & 0xc0) != 0x80)
                {
                    throw std::invalid_argument("Invalid UTF-8 continuation byte");
                }
                scalar = (scalar << 6) | (next & 0x3f);
            }
            if (scalar < minimum || scalar > 0x10ffff || (scalar >= 0xd800 && scalar <= 0xdfff))
            {
                throw std::invalid_argument("Invalid Unicode scalar in UTF-8");
            }
            if constexpr (sizeof(wchar_t) == 2)
            {
                if (scalar > 0xffff)
                {
                    result += static_cast<wchar_t>(0xd800 + ((scalar - 0x10000) >> 10));
                    result += static_cast<wchar_t>(0xdc00 + ((scalar - 0x10000) & 0x3ff));
                }
                else
                {
                    result += static_cast<wchar_t>(scalar);
                }
            }
            else
            {
                result += static_cast<wchar_t>(scalar);
            }
        }
        return result;
    }

    inline std::string EncodeUtf8(std::wstring_view value)
    {
        std::string result;
        for (std::size_t index = 0; index < value.size(); ++index)
        {
            std::uint32_t scalar = static_cast<std::uint32_t>(value[index]);
            if constexpr (sizeof(wchar_t) == 2)
            {
                if (scalar >= 0xd800 && scalar <= 0xdbff)
                {
                    if (++index == value.size())
                    {
                        throw std::invalid_argument("Unpaired UTF-16 high surrogate");
                    }
                    const auto low = static_cast<std::uint32_t>(value[index]);
                    if (low < 0xdc00 || low > 0xdfff)
                    {
                        throw std::invalid_argument("Unpaired UTF-16 high surrogate");
                    }
                    scalar = 0x10000 + ((scalar - 0xd800) << 10) + low - 0xdc00;
                }
            }
            if (scalar > 0x10ffff || (scalar >= 0xd800 && scalar <= 0xdfff))
            {
                throw std::invalid_argument("Invalid Unicode scalar in wide string");
            }
            if (scalar <= 0x7f)
            {
                result += static_cast<char>(scalar);
            }
            else if (scalar <= 0x7ff)
            {
                result += static_cast<char>(0xc0 | (scalar >> 6));
                result += static_cast<char>(0x80 | (scalar & 0x3f));
            }
            else if (scalar <= 0xffff)
            {
                result += static_cast<char>(0xe0 | (scalar >> 12));
                result += static_cast<char>(0x80 | ((scalar >> 6) & 0x3f));
                result += static_cast<char>(0x80 | (scalar & 0x3f));
            }
            else
            {
                result += static_cast<char>(0xf0 | (scalar >> 18));
                result += static_cast<char>(0x80 | ((scalar >> 12) & 0x3f));
                result += static_cast<char>(0x80 | ((scalar >> 6) & 0x3f));
                result += static_cast<char>(0x80 | (scalar & 0x3f));
            }
        }
        return result;
    }

    // Returns escaped JSON string content without surrounding quotes.
    inline std::string EscapeJson(std::string_view value)
    {
        DecodeUtf8(value);
        constexpr char HEX[] = "0123456789abcdef";
        std::string result;
        for (const unsigned char character : value)
        {
            switch (character)
            {
            case '"': result += "\\\""; break;
            case '\\': result += "\\\\"; break;
            case '\b': result += "\\b"; break;
            case '\f': result += "\\f"; break;
            case '\n': result += "\\n"; break;
            case '\r': result += "\\r"; break;
            case '\t': result += "\\t"; break;
            default:
                if (character < 0x20)
                {
                    result += "\\u00";
                    result += HEX[character >> 4];
                    result += HEX[character & 0x0f];
                }
                else
                {
                    result += static_cast<char>(character);
                }
                break;
            }
        }
        return result;
    }
}

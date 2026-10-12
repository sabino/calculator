// Licensed under the MIT License.
#pragma once
#include <string_view>

namespace Desktop
{
    struct AboutInfo
    {
        std::string_view version;
        std::string_view copyright;
        std::string_view license;
        std::string_view source_url;
        std::string_view privacy_url;
        std::string_view services_url;
    };

    AboutInfo GetAboutInfo();
}

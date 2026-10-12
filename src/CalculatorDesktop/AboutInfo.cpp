// Licensed under the MIT License.
#include "AboutInfo.h"
#include "BuildInfo.h"

namespace Desktop
{
    AboutInfo GetAboutInfo()
    {
        return { Generated::VERSION,
                 Generated::COPYRIGHT,
                 Generated::LICENSE,
                 "https://github.com/microsoft/calculator",
                 "https://go.microsoft.com/fwlink/?LinkID=521839",
                 "https://go.microsoft.com/fwlink/?LinkID=822631" };
    }
}

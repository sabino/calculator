#pragma once

#if defined(_WIN32) && defined(_MSC_VER)

#include <sal.h>
#ifndef _Frees_ptr_opt_
#define _Frees_ptr_opt_ _In_opt_ _Post_invalid_
#endif

#else

// Empty macro definitions for source annotations

#define _In_opt_
#define _Out_opt_
#define _In_
#define _Out_
#define _Inout_
#define __in_opt
#define _Frees_ptr_opt_

#endif

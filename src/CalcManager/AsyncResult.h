// Copyright (c) Microsoft Corporation. All rights reserved.
// Licensed under the MIT License.

#pragma once

#if defined(_WIN32) && defined(_MSC_VER)
#include <ppltasks.h>
#else
#include <future>
#endif

namespace UnitConversionManager
{
    // Keep the UWP ABI while allowing desktop loaders to use standard futures.
    template <typename T>
    using AsyncResult =
#if defined(_WIN32) && defined(_MSC_VER)
        concurrency::task<T>;
#else
        std::future<T>;
#endif
}

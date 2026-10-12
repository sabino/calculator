// Licensed under the MIT License.
#include "Bridge.h"
#include "BridgeJson.h"
#include "Command.h"
#include "ResourceProvider.h"
#include <cmath>
#include <cstdint>
#include <iostream>
#include <memory>
#include <stdexcept>
#include <string>

using CalculationManager::Command;

namespace
{
    void Require(bool condition, const char* message)
    {
        if (!condition)
            throw std::runtime_error(message);
    }

    std::string ReadJson(const char* text)
    {
        Require(text != nullptr, "Bridge JSON getter failed");
        return text;
    }

    std::string ReadState(CalcHandle handle)
    {
        return ReadJson(calc_get_state(handle));
    }

    void Send(CalcHandle handle, Command command)
    {
        Require(calc_send_command(handle, static_cast<int>(command)) == 1, "Bridge command failed");
    }

    void CheckSnapshotAndCalculator(CalcHandle handle)
    {
        const auto initial = ReadState(handle);
        for (const char* field : {"mode",
                                  "primary",
                                  "expression",
                                  "converted",
                                  "notice",
                                  "isError",
                                  "memory",
                                  "history",
                                  "buttonRows",
                                  "memoryButtons",
                                  "scientificMenus",
                                  "scientificShift",
                                  "scientificFormat",
                                  "scientificFormatButton",
                                  "radix",
                                  "wordWidth",
                                  "angleCommand",
                                  "angleButtons",
                                  "baseButtons",
                                  "wordButtons",
                                  "bitValues",
                                  "bitButtons",
                                  "categories",
                                  "units",
                                  "categoryIndex",
                                  "fromUnitIndex",
                                  "toUnitIndex"})
        {
            Require(initial.find(std::string("\"") + field + "\":") != std::string::npos, "Snapshot omitted a frontend contract field");
        }
        Require(initial.find("\"mode\":0") != std::string::npos && initial.find("\"primary\":\"0\"") != std::string::npos,
                "Session must begin in Standard mode with a zero result");
        Require(initial.find("\"accessibleName\":") != std::string::npos && initial.find("\"enabled\":") != std::string::npos,
                "Shared button records must include accessibility and enabled state");
        Require(calc_press_key(handle, "2") == 1 && calc_press_key(handle, "+") == 1 && calc_press_key(handle, "3") == 1,
                "Single scalar key input must reach the existing calculator engine");
        Send(handle, Command::CommandEQU);
        Require(ReadState(handle).find("\"primary\":\"5\"") != std::string::npos, "C ABI arithmetic returned the wrong value");
        Require(calc_send_command(handle, 85) == 0 && calc_send_command(handle, -1) == 0 && calc_send_command(handle, 999999) == 0,
                "Command holes and arbitrary integers must be rejected before enum conversion");
        Require(calc_send_command(handle, static_cast<int>(Command::ModeScientific)) == 0 &&
                    calc_send_command(handle, static_cast<int>(Command::CommandSET_RESULT)) == 0,
                "Internal mode and result commands must not bypass bridge invariants");
        Require(std::string(calc_last_error(handle)).size() > 0, "Failed action must expose a diagnostic");
        Require(ReadState(handle).find("\"primary\":\"5\"") != std::string::npos, "Rejected command changed the result");
        Require(std::string(calc_last_error(handle)).empty(), "Successful bridge call must clear the previous diagnostic");
        Require(calc_set_mode(handle, -1) == 0 && calc_set_mode(handle, 5) == 0, "Only the five documented modes are accepted");
        Require(calc_press_key(handle, nullptr) == 0 && calc_press_key(handle, "") == 0 && calc_press_key(handle, "12") == 0,
                "Key input must contain exactly one supported scalar");
        Require(calc_press_key(handle, "\xc0\xaf") == 0 && calc_paste_number(handle, "\xed\xa0\x80") == 0,
                "Malformed UTF-8 must never reach the calculator engine");
        Require(calc_paste_number(handle, nullptr) == 0 && calc_paste_number(handle, "2+") == 0,
                "Paste must reject null input and malformed expressions");
        Require(calc_paste_number(handle, "12.5") == 1 && ReadState(handle).find("\"primary\":\"12.5\"") != std::string::npos,
                "Paste must preserve a valid locale-aware decimal number");
    }

    void CheckMemoryAndHistory(CalcHandle handle)
    {
        Require(calc_set_mode(handle, 0) == 1, "Standard mode selection failed");
        Send(handle, Command::CommandCLEAR);
        Require(calc_press_key(handle, "2") == 1 && calc_press_key(handle, "+") == 1 && calc_press_key(handle, "3") == 1,
                "Memory test setup failed");
        Send(handle, Command::CommandEQU);
        Send(handle, Command::CommandSTORE);
        Require(ReadState(handle).find("\"memory\":[\"5\"]") != std::string::npos, "Stored memory must appear in the snapshot");
        Require(calc_paste_number(handle, "2") == 1 && calc_memory_action(handle, 1, 0) == 1, "Per-slot memory addition failed");
        Require(ReadState(handle).find("\"memory\":[\"7\"]") != std::string::npos, "Memory addition did not update its slot");
        Require(calc_memory_action(handle, 0, 0) == 1 && ReadState(handle).find("\"primary\":\"7\"") != std::string::npos,
                "Per-slot memory recall failed");
        Require(calc_paste_number(handle, "2") == 1 && calc_memory_action(handle, 2, 0) == 1, "Per-slot memory subtraction failed");
        Require(ReadState(handle).find("\"memory\":[\"5\"]") != std::string::npos, "Memory subtraction did not update its slot");
        Require(calc_memory_action(handle, 3, 0) == 1 && ReadState(handle).find("\"memory\":[]") != std::string::npos,
                "Per-slot memory removal failed");
        Require(calc_memory_action(handle, 0, 0) == 0 && calc_memory_action(handle, 4, 0) == 0 && calc_memory_action(handle, 0, -1) == 0,
                "Memory operations must validate operation and index");
        Require(calc_history_action(handle, 0, 0) == 1 && ReadState(handle).find("\"primary\":\"5\"") != std::string::npos,
                "History recall must use the stored result");
        Require(calc_history_action(handle, 1, 0) == 1, "History-item removal failed");
        Require(calc_history_action(handle, 2, -1) == 1 && ReadState(handle).find("\"history\":[]") != std::string::npos,
                "History clear must accept its ignored index and clear all items");
        Require(calc_history_action(handle, 0, -1) == 0 && calc_history_action(handle, 3, 0) == 0,
                "History operations must validate operation and applicable index");
    }

    void CheckModes(CalcHandle handle)
    {
        Require(calc_set_mode(handle, 1) == 1 && calc_paste_number(handle, "27") == 1, "Scientific mode setup failed");
        Send(handle, Command::CommandCUBEROOT);
        const auto scientific = ReadState(handle);
        Require(scientific.find("\"primary\":\"3\"") != std::string::npos &&
                    scientific.find("Inverse hyperbolic functions") != std::string::npos,
                "Scientific menus and commands must share the existing model");
        Send(handle, Command::CommandINV);
        Require(ReadState(handle).find("\"scientificShift\":true") != std::string::npos,
                "Second functions must be available through the bridge snapshot");
        Require(calc_paste_number(handle, "5") == 1, "Shifted operation setup failed");
        Send(handle, Command::CommandPOW2);
        const auto shiftedResult = ReadState(handle);
        Require(shiftedResult.find("\"primary\":\"32\"") != std::string::npos &&
                    shiftedResult.find("\"scientificShift\":false") != std::string::npos,
                "Shifted commands must stay whitelisted and reset the presentation state");
        Send(handle, Command::CommandFE);
        const auto formatted = ReadState(handle);
        Require(formatted.find("\"scientificFormat\":true") != std::string::npos &&
                    formatted.find("\"scientificFormatButton\":{\"title\":\"F-E\",\"command\":119") != std::string::npos,
                "Scientific notation must expose its toggle and standalone command specification");
        Send(handle, Command::CommandFE);
        Send(handle, Command::CommandCENTR);
        Require(calc_paste_number(handle, "3.5") == 1, "Legacy scientific command setup failed");
        Send(handle, Command::CommandCHOP);
        Require(ReadState(handle).find("\"primary\":\"3\"") != std::string::npos,
                "Previously accepted scientific commands must remain whitelisted");
        Send(handle, Command::CommandRAD);
        Require(ReadState(handle).find("\"angleCommand\":322") != std::string::npos, "Angle selection must appear in the snapshot");
        Require(calc_set_mode(handle, 2) == 1, "Programmer mode selection failed");
        Send(handle, Command::CommandCLEAR);
        Send(handle, Command::CommandBINPOS0);
        Require(ReadState(handle).find("\"primary\":\"1\"") != std::string::npos, "Programmer bit command failed");
        const auto bits = ReadState(handle);
        const auto firstBit =
            bits.find("\"bitButtons\":[{\"title\":\"0\",\"command\":763,\"accessibleName\":\"Toggle bit 63\",\"enabled\":true}");
        const auto lastBit = bits.find("{\"title\":\"1\",\"command\":700,\"accessibleName\":\"Toggle bit 0\",\"enabled\":true}", firstBit);
        Require(firstBit != std::string::npos && lastBit != std::string::npos && firstBit < lastBit,
                "Bit button records must provide native commands in bit 63 through 0 order");
        Send(handle, Command::CommandHex);
        Require(calc_press_key(handle, "f") == 1 && ReadState(handle).find("\"radix\":16") != std::string::npos,
                "Programmer letter keys must respect the selected base");
        Send(handle, Command::CommandByte);
        Require(ReadState(handle).find("\"wordWidth\":8") != std::string::npos && calc_send_command(handle, 708) == 0,
                "Bits beyond the selected word width must be disabled at the ABI edge");
        Require(ReadState(handle).find("\"accessibleName\":\"Toggle bit 63\",\"enabled\":false") != std::string::npos,
                "Bit button records must disable bits outside the selected word width");
        Send(handle, Command::CommandBin);
        Require(calc_press_key(handle, "2") == 0, "Disabled radix digit must be rejected by key input");
        Require(calc_set_mode(handle, 3) == 1 && calc_set_category(handle, 0) == 1 && calc_set_units(handle, 0, 0) == 1,
                "Converter category and unit selection failed");
        Require(calc_set_category(handle, -1) == 0 && calc_set_category(handle, 100000) == 0 && calc_set_units(handle, -1, 0) == 0 &&
                    calc_set_units(handle, 0, 100000) == 0,
                "Converter selection must reject unavailable indices");
        Require(calc_memory_action(handle, 0, 0) == 0 && calc_send_command(handle, static_cast<int>(Command::CommandSQRT)) == 0,
                "Converter mode must reject unrelated calculator actions");
        Require(calc_set_mode(handle, 4) == 1, "Date mode selection failed");
        Require(ReadState(handle).find("\"buttonRows\":[]") != std::string::npos && calc_press_key(handle, "1") == 0 &&
                    calc_paste_number(handle, "1") == 0,
                "Date mode must keep numeric calculator actions unavailable");
    }

    void CheckDatesAndAbout(CalcHandle handle)
    {
        Require(ReadJson(calc_date_difference(handle, 1601, 1, 1, 9999, 12, 31)) ==
                    "{\"years\":8398,\"months\":11,\"weeks\":4,\"days\":2,\"totalDays\":3067670}",
                "C ABI full-range date difference changed the shared civil-date contract");
        Require(ReadJson(calc_date_difference(handle, 2019, 3, 11, 2019, 3, 10)).find("\"totalDays\":1") != std::string::npos,
                "Date difference must ignore endpoint order and daylight-saving offsets");
        Require(ReadJson(calc_date_shift(handle, 2008, 3, 10, 0, 1, 10, 1)) == "{\"year\":2008,\"month\":1,\"day\":29}",
                "C ABI subtraction must preserve days-before-months semantics");
        Require(ReadJson(calc_date_shift(handle, 2008, 1, 31, 0, 1, 0, 0)) == "{\"year\":2008,\"month\":2,\"day\":29}",
                "C ABI addition must clamp month ends");
        Require(calc_date_difference(handle, 1600, 12, 31, 2000, 1, 1) == nullptr &&
                    calc_date_difference(handle, 2000, -1, 1, 2000, 1, 1) == nullptr &&
                    calc_date_shift(handle, 9999, 12, 31, 0, 0, 1, 0) == nullptr &&
                    calc_date_shift(handle, 1601, 1, 1, 0, 0, 1, 1) == nullptr,
                "C ABI dates must validate signed fields and range overflow");
        Require(calc_date_shift(handle, 2024, 2, 29, -1, 0, 0, 0) == nullptr && calc_date_shift(handle, 2024, 2, 29, 0, 0, 0, 2) == nullptr,
                "C ABI durations and operation flags must be validated");
        const auto about = ReadJson(calc_get_about(handle));
        for (const char* field :
             {"version", "copyright", "license", "sourceUrl", "privacyUrl", "servicesUrl", "graphingEnabled", "graphingVersion"})
            Require(about.find(std::string("\"") + field + "\":") != std::string::npos, "About JSON omitted a public contract field");
        Require(about.find("Permission is hereby granted") != std::string::npos && about.find("\\n") != std::string::npos,
                "About JSON must preserve and escape the full license");
    }

    std::string ReadPrimary(CalcHandle handle)
    {
        const auto state = ReadState(handle);
        const auto start = state.find("\"primary\":\"");
        Require(start != std::string::npos, "Snapshot must include the primary display");
        const auto first = start + std::string("\"primary\":\"").size();
        const auto last = state.find('"', first);
        Require(last != std::string::npos, "Primary display string must be closed");
        return state.substr(first, last - first);
    }

    void CheckSessionLocaleIsolation()
    {
        using Session = std::unique_ptr<CalcSession, int (*)(CalcHandle)>;
        Session english(calc_create("en-US", "US", ".", ","), calc_destroy);
        Require(english != nullptr, "English context session failed");
        Require(calc_paste_number(english.get(), "1.5") == 1, "English decimal input failed");
        Send(english.get(), Command::CommandADD);
        Require(calc_press_key(english.get(), "2") == 1, "English second operand failed");
        Send(english.get(), Command::CommandEQU);
        Require(ReadPrimary(english.get()) == "3.5", "English arithmetic must use its decimal separator");
        Send(english.get(), Command::CommandSTORE);
        const auto storedEnglish = ReadState(english.get());

        Session portuguese(calc_create("pt-BR", "BR", ",", "."), calc_destroy);
        Require(portuguese != nullptr, "Portuguese context session failed");
        Require(ReadState(english.get()) == storedEnglish,
                "Constructing another locale must preserve existing numeric state, history, and memory");
        Require(calc_paste_number(portuguese.get(), "1,5") == 1, "Portuguese decimal input failed");
        Send(portuguese.get(), Command::CommandADD);
        Require(calc_press_key(portuguese.get(), "2") == 1, "Portuguese second operand failed");
        Send(portuguese.get(), Command::CommandEQU);
        Require(ReadPrimary(portuguese.get()) == "3,5", "Portuguese arithmetic must use its decimal separator");
        Send(portuguese.get(), Command::CommandSTORE);
        const auto storedPortuguese = ReadState(portuguese.get());

        Send(english.get(), Command::CommandADD);
        Require(ReadState(portuguese.get()) == storedPortuguese,
                "Interleaved locale reads must preserve pending and accepted calculations");
        Require(calc_press_key(english.get(), "2") == 1, "English interleaved operand failed");
        ReadState(portuguese.get());
        Send(english.get(), Command::CommandEQU);
        const auto englishResult = ReadState(english.get());
        Require(ReadPrimary(english.get()) == "5.5" && englishResult.find("\"memory\":[\"3.5\"]") != std::string::npos &&
                    englishResult.find("\"result\":\"3.5\"") != std::string::npos,
                "English context switches must preserve operands, decimal format, stored memory, and history");
        Send(portuguese.get(), Command::CommandADD);
        ReadState(english.get());
        Require(calc_press_key(portuguese.get(), "2") == 1, "Portuguese interleaved operand failed");
        ReadState(english.get());
        Send(portuguese.get(), Command::CommandEQU);
        const auto portugueseResult = ReadState(portuguese.get());
        Require(ReadPrimary(portuguese.get()) == "5,5" && portugueseResult.find("\"memory\":[\"3,5\"]") != std::string::npos &&
                    portugueseResult.find("\"result\":\"3,5\"") != std::string::npos,
                "Portuguese context switches must preserve decimal format, stored memory, and history");

        Require(calc_set_mode(english.get(), 1) == 1 && calc_paste_number(english.get(), "1") == 1, "Scientific precision setup failed");
        Send(english.get(), Command::CommandDIV);
        Require(calc_press_key(english.get(), "3") == 1, "Scientific precision denominator failed");
        Send(english.get(), Command::CommandEQU);
        const auto scientificThird = ReadPrimary(english.get());
        Require(scientificThird.find("0.33333333333333333333333333333333") == 0,
                "Scientific mode must retain its 32-digit display precision");
        const auto scientificHistory = ReadState(english.get());

        Require(calc_set_mode(portuguese.get(), 2) == 1 && calc_paste_number(portuguese.get(), "255") == 1,
                "Lazy programmer context setup failed");
        Send(portuguese.get(), Command::CommandHex);
        const auto programmerState = ReadState(portuguese.get());
        Require(programmerState.find("\"primary\":\"FF\"") != std::string::npos &&
                    programmerState.find("\"radixDisplays\":[") != std::string::npos,
                "Programmer snapshots must retain the selected radix while computing alternate radix labels");
        Require(ReadState(english.get()) == scientificHistory,
                "Lazy engine construction must preserve another session's scientific state and history");
        Require(calc_paste_number(english.get(), "1") == 1, "Scientific fraction reset failed");
        Send(english.get(), Command::CommandDIV);
        ReadState(portuguese.get());
        Require(calc_press_key(english.get(), "3") == 1, "Scientific interleaved denominator failed");
        ReadState(portuguese.get());
        Send(english.get(), Command::CommandEQU);
        Require(ReadPrimary(english.get()) == scientificThird,
                "Radix snapshot reads must not overwrite another session's scientific precision");
        Require(ReadState(portuguese.get()) == programmerState,
                "Scientific commands must not overwrite another session's programmer base or result");
        Send(portuguese.get(), Command::CommandADD);
        ReadState(english.get());
        Require(calc_press_key(portuguese.get(), "1") == 1, "Programmer interleaved operand failed");
        ReadState(english.get());
        Send(portuguese.get(), Command::CommandEQU);
        Require(ReadPrimary(portuguese.get()) == "100", "Programmer arithmetic must resume with its stored hexadecimal radix");

        Desktop::ResourceProvider englishResources("en-US");
        Desktop::ResourceProvider portugueseResources("pt-BR", {L",", L".", L"3;0"});
        const auto englishError = Desktop::BridgeJson::EncodeUtf8(englishResources.GetCEngineString(L"99"));
        const auto portugueseError = Desktop::BridgeJson::EncodeUtf8(portugueseResources.GetCEngineString(L"99"));
        Require(englishError != portugueseError, "Locale error-resource fixtures must differ");
        Send(english.get(), Command::CommandCLEAR);
        Require(calc_press_key(english.get(), "1") == 1, "English error fixture setup failed");
        Send(english.get(), Command::CommandDIV);
        ReadState(portuguese.get());
        Require(calc_press_key(english.get(), "0") == 1, "English zero denominator failed");
        ReadState(portuguese.get());
        Send(english.get(), Command::CommandEQU);
        Require(ReadPrimary(english.get()) == Desktop::BridgeJson::EscapeJson(englishError),
                "English division errors must restore English engine resources");
        Send(portuguese.get(), Command::CommandDec);
        Send(portuguese.get(), Command::CommandCLEAR);
        Require(calc_press_key(portuguese.get(), "1") == 1, "Portuguese error fixture setup failed");
        Send(portuguese.get(), Command::CommandDIV);
        ReadState(english.get());
        Require(calc_press_key(portuguese.get(), "0") == 1, "Portuguese zero denominator failed");
        ReadState(english.get());
        Send(portuguese.get(), Command::CommandEQU);
        Require(ReadPrimary(portuguese.get()) == Desktop::BridgeJson::EscapeJson(portugueseError),
                "Portuguese division errors must restore Portuguese engine resources");
        portuguese.reset();
        Send(english.get(), Command::CommandCLEAR);
        Require(calc_paste_number(english.get(), "1.5") == 1, "Remaining session input after context destruction failed");
        Send(english.get(), Command::CommandADD);
        Require(calc_press_key(english.get(), "2") == 1, "Remaining session operand after context destruction failed");
        Send(english.get(), Command::CommandEQU);
        Require(ReadPrimary(english.get()) == "3.5", "Destroying the active engine must invalidate its cached context safely");
    }

    void CheckUnicodeHelpers()
    {
        const std::string text = "\xcf\x80 \xf0\x9f\x98\x80";
        Require(Desktop::BridgeJson::EncodeUtf8(Desktop::BridgeJson::DecodeUtf8(text)) == text,
                "UTF-8 helpers must round trip BMP and supplementary Unicode scalars");
        Require(Desktop::BridgeJson::EscapeJson(std::string("\"\\\n\0", 4)) == "\\\"\\\\\\n\\u0000",
                "JSON escaping must protect quotes, slashes, control characters and NUL");
        for (const std::string invalid : {std::string("\xc0\xaf"), std::string("\xe2\x82"), std::string("\xed\xa0\x80"),
                                          std::string("\xf4\x90\x80\x80"), std::string("\x80")})
        {
            bool rejected = false;
            try
            {
                Desktop::BridgeJson::DecodeUtf8(invalid);
            }
            catch (const std::invalid_argument&)
            {
                rejected = true;
            }
            Require(rejected, "Unicode decoder must reject noncanonical or invalid scalars");
        }
        bool rejected = false;
        try
        {
            Desktop::BridgeJson::EncodeUtf8(std::wstring(1, static_cast<wchar_t>(0xd800)));
        }
        catch (const std::invalid_argument&)
        {
            rejected = true;
        }
        Require(rejected, "Wide-string encoder must reject an unpaired surrogate");
    }

    void CheckCurrency()
    {
        // ECB's 2026-10-09 reference rates, quoted per euro; EUR=1 is the base identity.
        const char* rates = "EUR=1\nUSD=1.1206\nBRL=5.6086\nGBP=0.84763\nJPY=177.34\n";
        const char* referenceDate = "2026-10-09";
        const char* fetchedAt = "2026-10-10T12:00:00Z";
        std::unique_ptr<CalcSession, int (*)(CalcHandle)> session(calc_create("en-US", "US", ".", ","), calc_destroy);
        const auto handle = session.get();
        const auto convertsTo = [handle](double expected) {
            const auto current = ReadState(handle);
            const auto start = current.find("\"converted\":\"") + std::string("\"converted\":\"").size();
            const auto end = current.find('"', start);
            const auto value = std::stod(current.substr(start, end - start));
            return std::abs(value - expected) < 1e-10;
        };
        Require(handle != nullptr && calc_set_mode(handle, 3) == 1 && calc_set_category(handle, 12) == 1,
                "Currency must append to the established twelve native converter categories");
        const auto unavailable = ReadState(handle);
        Require(unavailable.find("\"Currency\"") != std::string::npos && unavailable.find("CategoryName_") == std::string::npos,
                "Converters must display localized category names instead of resource keys");
        Require(unavailable.find("\"available\":false") != std::string::npos &&
                    unavailable.find("\"converted\":\"\"") != std::string::npos &&
                    unavailable.find("\"units\":[]") != std::string::npos,
                "Missing currency data must not present an identity conversion");
        Require(calc_press_key(handle, "1") == 0 && calc_paste_number(handle, "10") == 0,
                "Unavailable currency data must disable numerical input at the ABI edge");
        Require(calc_currency_set_rates(handle, referenceDate, fetchedAt, rates, 0) == 1, "Dated ECB rates must load");
        auto state = ReadState(handle);
        Require(state.find("\"units\":[\"BRL\",\"EUR\",\"GBP\",\"JPY\",\"USD\"]") != std::string::npos &&
                    state.find("\"available\":true") != std::string::npos && state.find("\"status\":\"ready\"") != std::string::npos &&
                    state.find("\"referenceDate\":\"2026-10-09\"") != std::string::npos &&
                    state.find("European Central Bank") != std::string::npos,
                "Currency state must expose real units, rate date, provider, and readiness");
        Require(calc_set_units(handle, 1, 4) == 1 && calc_paste_number(handle, "100") == 1 &&
                    convertsTo(112.06),
                "EUR to USD must use quote-per-euro direction");
        Require(calc_set_units(handle, 4, 1) == 1 && calc_paste_number(handle, "112.06") == 1 &&
                    convertsTo(100),
                "USD to EUR must use the reciprocal rate");
        Require(calc_set_units(handle, 1, 1) == 1 && calc_paste_number(handle, "12.34") == 1 &&
                    convertsTo(12.34),
                "A loaded currency must support identity conversion");
        Require(calc_set_units(handle, 0, 4) == 1 && calc_paste_number(handle, "560.86") == 1 &&
                    convertsTo(112.06),
                "BRL to USD must divide their rates against the common euro base");
        Require(calc_currency_set_rates(handle, referenceDate, fetchedAt, rates, 1) == 1 &&
                    ReadState(handle).find("\"fromUnitIndex\":0,\"toUnitIndex\":4") != std::string::npos,
                "Cached refreshes must preserve currency selections by stable unit IDs");
        Require(calc_currency_set_status(handle, 4, "Offline. Using cached rates.") == 1, "Refresh failure status failed");
        state = ReadState(handle);
        Require(state.find("\"available\":true") != std::string::npos && state.find("\"status\":\"refreshFailed\"") != std::string::npos &&
                    convertsTo(112.06),
                "Offline refresh failure must retain valid cached conversion data");
        for (const char* invalid : {"EUR=1", "EUR=2\nUSD=1.1206", "EUR=1\nUSD=0", "EUR=1\nUSD=-1", "EUR=1\nUSD=NaN",
                                    "EUR=1\nUSD=inf", "EUR=1\nUSD=1.1206\nUSD=1.1206", "EUR=1\nUS1=1", "EUR=1\nUSD=1,1206",
                                    "EUR=1\nUSD=1e309", "EUR=1\nUSD=1e-999", "EUR=1\nUSD=1.1206\n\n"})
        {
            Require(calc_currency_set_rates(handle, referenceDate, fetchedAt, invalid, 0) == 0 && ReadState(handle) == state,
                    "Malformed rate batches must preserve all valid currency data and selection state");
        }
        Require(calc_currency_set_rates(handle, "2026-02-30", fetchedAt, rates, 0) == 0 &&
                    calc_currency_set_rates(handle, referenceDate, "2026-10-08T12:00:00Z", rates, 0) == 0 &&
                    calc_currency_set_rates(handle, referenceDate, "2026-10-10T24:00:00Z", rates, 0) == 0 &&
                    calc_currency_set_rates(handle, referenceDate, fetchedAt, rates, 2) == 0 &&
                    calc_currency_set_rates(handle, referenceDate, fetchedAt, nullptr, 0) == 0 &&
                    calc_currency_set_status(handle, 2, "") == 0 && calc_currency_set_status(handle, 0, "") == 0 &&
                    ReadState(handle) == state,
                "Invalid dates, flags, and status transitions must preserve valid currency state");
        Require(calc_set_category(handle, 0) == 1 && calc_paste_number(handle, "10") == 1,
                "Currency support must preserve existing static converter indices");
        Require(calc_set_mode(handle, 0) == 1 && calc_paste_number(handle, "23") == 1,
                "Calculator mode restore after currency setup failed");
        const auto primary = ReadPrimary(handle);
        Require(calc_currency_set_rates(handle, referenceDate, fetchedAt, rates, 0) == 1 && ReadPrimary(handle) == primary,
                "Background currency updates must preserve calculator display state");
        // Frankfurter's observed 2026-10-11/12 bulk EUR quotes include currencies outside ECB coverage.
        const char* broadRates = "EUR=1\nAED=4.1198\nAFN=72.756\nBRL=5.6181\nJPY=177.53\nUSD=1.1218\nVND=29002";
        Require(calc_currency_set_rates_ex(handle, 1, "2026-10-11", "2026-10-12", "2026-10-12T03:00:00Z", broadRates, 0) == 1,
                "Frankfurter quotes must support additional currencies and mixed observation dates");
        Require(calc_set_mode(handle, 3) == 1 && calc_set_category(handle, 12) == 1, "Frankfurter conversion setup failed");
        state = ReadState(handle);
        Require(state.find("\"providerId\":1,\"provider\":\"Frankfurter\"") != std::string::npos &&
                    state.find("https://api.frankfurter.dev/v2/rates?base=EUR") != std::string::npos &&
                    state.find("\"referenceDate\":\"2026-10-11\",\"referenceEndDate\":\"2026-10-12\"") != std::string::npos &&
                    state.find("\"units\":[\"AED\",\"AFN\",\"BRL\",\"EUR\",\"JPY\",\"USD\",\"VND\"]") != std::string::npos,
                "Frankfurter state must expose precise provider and observation-date range metadata");
        Require(calc_set_units(handle, 3, 0) == 1 && calc_paste_number(handle, "100") == 1 && convertsTo(411.98),
                "Currencies outside ECB coverage must use native conversion ratios");
        Require(calc_set_units(handle, 2, 5) == 1 && calc_paste_number(handle, "561.81") == 1 && convertsTo(112.18),
                "Frankfurter cross rates must divide quotes against the common euro base");
        state = ReadState(handle);
        Require(calc_currency_set_provider(handle, 2) == 0 &&
                    calc_currency_set_rates_ex(handle, 2, "2026-10-11", "2026-10-12", "2026-10-12T03:00:00Z", broadRates, 0) == 0 &&
                    calc_currency_set_rates_ex(handle, 1, "2026-10-11", "2026-10-10", "2026-10-12T03:00:00Z", broadRates, 0) == 0 &&
                    calc_currency_set_rates_ex(handle, 1, "2026-10-11", "2026-10-13", "2026-10-12T03:00:00Z", broadRates, 0) == 0 &&
                    calc_currency_set_rates_ex(handle, 0, "2026-10-11", "2026-10-12", "2026-10-12T03:00:00Z", broadRates, 0) == 0 &&
                    ReadState(handle) == state,
                "Invalid provider/date-range metadata must preserve the complete previous batch");
    }
} // namespace

int main()
{
    try
    {
        CheckUnicodeHelpers();
        CheckCurrency();
        CheckSessionLocaleIsolation();
        Require(calc_get_state(nullptr) == nullptr && calc_destroy(nullptr) == 0, "Null session handles must be rejected");
        const auto forged = reinterpret_cast<CalcHandle>(static_cast<std::uintptr_t>(1));
        Require(calc_set_mode(forged, 0) == 0 && calc_get_state(forged) == nullptr, "Unknown handles must not be dereferenced");
        Require(calc_create("en-US", "US", "..", ",") == nullptr && std::string(calc_last_error(nullptr)).size() > 0,
                "Creation failure must expose a thread-local diagnostic");
        Require(calc_create("\xc0\xaf", "US", ".", ",") == nullptr, "Creation must validate UTF-8 settings");
        std::unique_ptr<CalcSession, int (*)(CalcHandle)> session(calc_create("en-US", "US", ".", ","), calc_destroy);
        Require(session != nullptr, "Calculator session creation failed");
        CheckSnapshotAndCalculator(session.get());
        CheckMemoryAndHistory(session.get());
        CheckModes(session.get());
        CheckDatesAndAbout(session.get());
        std::unique_ptr<CalcSession, int (*)(CalcHandle)> localized(calc_create("pt-BR", "BR", ",", "."), calc_destroy);
        Require(localized != nullptr && calc_paste_number(localized.get(), "1,5") == 1 &&
                    ReadState(localized.get()).find("\"primary\":\"1,5\"") != std::string::npos,
                "Locale-aware decimal input must survive UTF-8 interop");
        std::unique_ptr<CalcSession, int (*)(CalcHandle)> defaults(calc_create(nullptr, nullptr, nullptr, nullptr), calc_destroy);
        Require(defaults != nullptr, "Null creation settings must select documented defaults");
        const auto destroyed = defaults.release();
        Require(calc_destroy(destroyed) == 1 && calc_destroy(destroyed) == 0 && calc_get_state(destroyed) == nullptr,
                "Destroyed session handles must be rejected without dereferencing freed memory");
        std::cout << "Calculator C ABI bridge tests passed\n";
        return 0;
    }
    catch (const std::exception& error)
    {
        std::cerr << error.what() << '\n';
        return 1;
    }
}
